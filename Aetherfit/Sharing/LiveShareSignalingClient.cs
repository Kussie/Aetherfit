using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Sharing;

// Thin wrapper around the pairing-code handshake with the standalone signaling server. It only ever
// carries WebRTC offer/answer/ICE messages - the bundle itself never touches this connection.
// Events fire on the framework thread so callers can touch UI state directly.
internal sealed class LiveShareSignalingClient : IDisposable
{
    public event Action<string>? OnJoined;   // role: "host" or "guest"
    public event Action? OnPeerJoined;
    public event Action<string>? OnOffer;    // sdp
    public event Action<string>? OnAnswer;   // sdp
    public event Action<string, string?, int?>? OnIceCandidate; // candidate, sdpMid, sdpMLineIndex
    public event Action? OnPeerLeft;
    public event Action<string>? OnServerError;    // a signaling "error" message (bad/full code, etc.)
    public event Action<string>? OnTransportError; // the socket itself failed

    private ClientWebSocket? socket;
    private CancellationTokenSource? receiveCts;

    public async Task ConnectAndJoinAsync(string url, string code)
    {
        var ws = new ClientWebSocket();
        socket = ws;
        await ws.ConnectAsync(new Uri(url), CancellationToken.None);

        receiveCts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(ws, receiveCts.Token);

        await SendAsync(new JObject { ["type"] = "join", ["code"] = code });
    }

    public Task SendOfferAsync(string sdp) => SendAsync(new JObject { ["type"] = "offer", ["sdp"] = sdp });

    public Task SendAnswerAsync(string sdp) => SendAsync(new JObject { ["type"] = "answer", ["sdp"] = sdp });

    public Task SendIceCandidateAsync(string candidate, string? sdpMid, int? sdpMLineIndex)
    {
        var payload = new JObject { ["type"] = "ice-candidate", ["candidate"] = candidate };
        if (sdpMid != null) payload["sdpMid"] = sdpMid;
        if (sdpMLineIndex.HasValue) payload["sdpMLineIndex"] = sdpMLineIndex.Value;
        return SendAsync(payload);
    }

    private async Task SendAsync(JObject payload)
    {
        var ws = socket;
        if (ws is not { State: WebSocketState.Open })
            return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            RaiseOnFramework(() => OnTransportError?.Invoke($"Failed to send: {ex.Message}"));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        RaiseOnFramework(() => OnPeerLeft?.Invoke());
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                Dispatch(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed/cancelled locally - nothing to report.
        }
        catch (WebSocketException ex)
        {
            RaiseOnFramework(() => OnTransportError?.Invoke(ex.Message));
        }
    }

    private void Dispatch(string json)
    {
        JObject obj;
        try
        {
            obj = JObject.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        switch (obj["type"]?.ToString())
        {
            case "joined":
                var role = obj["role"]?.ToString() ?? string.Empty;
                RaiseOnFramework(() => OnJoined?.Invoke(role));
                break;
            case "peer-joined":
                RaiseOnFramework(() => OnPeerJoined?.Invoke());
                break;
            case "offer":
                var offerSdp = obj["sdp"]?.ToString() ?? string.Empty;
                RaiseOnFramework(() => OnOffer?.Invoke(offerSdp));
                break;
            case "answer":
                var answerSdp = obj["sdp"]?.ToString() ?? string.Empty;
                RaiseOnFramework(() => OnAnswer?.Invoke(answerSdp));
                break;
            case "ice-candidate":
                var candidate = obj["candidate"]?.ToString() ?? string.Empty;
                var sdpMid = obj["sdpMid"]?.ToString();
                var sdpMLineIndex = obj["sdpMLineIndex"]?.Value<int>();
                RaiseOnFramework(() => OnIceCandidate?.Invoke(candidate, sdpMid, sdpMLineIndex));
                break;
            case "peer-left":
                RaiseOnFramework(() => OnPeerLeft?.Invoke());
                break;
            case "error":
                var message = obj["message"]?.ToString() ?? "Unknown signaling error.";
                RaiseOnFramework(() => OnServerError?.Invoke(message));
                break;
        }
    }

    private static void RaiseOnFramework(Action action) => Plugin.Framework.RunOnFrameworkThread(action);

    public void Dispose()
    {
        receiveCts?.Cancel();
        receiveCts?.Dispose();
        try { socket?.Abort(); }
        catch { /* already gone */ }
        socket?.Dispose();
    }
}
