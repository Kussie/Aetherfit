using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AetherfitSignaling;

// Handles one WebSocket connection end-to-end: the initial join, then a byte-for-byte relay of
// whatever the two peers send each other (offer/answer/ICE candidates). We only ever parse the
// first "join" message - everything after that is opaque to us.
internal static class SignalConnectionHandler
{
    private const int MaxCodeLength = 32;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    public static async Task HandleAsync(WebSocket socket, SessionStore store, CancellationToken cancellationToken)
    {
        PairSession? session = null;
        var isHost = false;

        try
        {
            using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            joinCts.CancelAfter(JoinTimeout);

            string joinJson;
            try
            {
                joinJson = await ReceiveFullTextMessageAsync(socket, joinCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TrySendAsync(socket, Encode("error", message: "Timed out waiting for a join message."), cancellationToken);
                return;
            }

            var code = ParseJoinCode(joinJson);
            if (code == null)
            {
                await TrySendAsync(socket, Encode("error", message: "Expected a join message with a pairing code."), cancellationToken);
                return;
            }

            session = store.GetOrCreate(code);
            lock (session.Gate)
            {
                if (session.Host == null)
                {
                    session.Host = socket;
                    isHost = true;
                }
                else if (session.Guest == null)
                {
                    session.Guest = socket;
                }
                else
                {
                    session = null; // full - handled below
                }
            }

            if (session == null)
            {
                await TrySendAsync(socket, Encode("error", message: "That pairing code already has two peers."), cancellationToken);
                return;
            }

            await TrySendAsync(socket, Encode("joined", role: isHost ? "host" : "guest"), cancellationToken);
            if (!isHost)
                await TrySendAsync(session.Host!, Encode("peer-joined"), cancellationToken);

            await RelayLoopAsync(socket, session, isHost, cancellationToken);
        }
        catch (WebSocketException)
        {
            // Peer dropped mid-message; falls through to the peer-left notification below.
        }
        finally
        {
            if (session != null)
            {
                store.RemoveIfCurrent(session);
                var peer = isHost ? session.Guest : session.Host;
                if (peer != null)
                    await TrySendAsync(peer, Encode("peer-left"), CancellationToken.None);
            }
        }
    }

    private static async Task RelayLoopAsync(WebSocket socket, PairSession session, bool isHost, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            session.LastActivityAt = DateTimeOffset.UtcNow;

            var peer = isHost ? session.Guest : session.Host;
            if (peer is { State: WebSocketState.Open })
                await peer.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType, result.EndOfMessage, cancellationToken);
        }
    }

    private static async Task<string> ReceiveFullTextMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Connection closed before joining.");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? ParseJoinCode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "join")
                return null;
            if (!root.TryGetProperty("code", out var codeEl))
                return null;

            var code = codeEl.GetString();
            if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
                return null;
            return code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Encode(string type, string? role = null, string? message = null)
    {
        var obj = new Dictionary<string, string> { ["type"] = type };
        if (role != null) obj["role"] = role;
        if (message != null) obj["message"] = message;
        return JsonSerializer.Serialize(obj);
    }

    private static async Task TrySendAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
            return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (WebSocketException)
        {
            // Peer already gone; nothing to do.
        }
    }
}
