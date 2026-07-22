using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SIPSorcery.Net;

namespace Aetherfit.Sharing;

public enum LiveSharePhase
{
    Idle,
    WaitingForPeer,     // host: pairing code shown, no guest yet
    Connecting,         // guest: joined, waiting for the offer
    Handshaking,        // WebRTC offer/answer/ICE in flight
    ExportingBundle,    // host: building the temp .afgallery file
    Transferring,       // data channel open, bytes moving
    Importing,          // guest: handing the received file to GallerySharingService
    Done,
    Failed,
}

// Transfers a gallery bundle directly to another player's plugin over a live WebRTC data channel,
// instead of the manual "export a file, send it yourself" flow. It never reimplements the bundle
// format: the host does a completely normal export to a temp file and streams those exact bytes;
// the guest writes them to a temp file and does a completely normal import.
public sealed class GalleryLiveShareService : IDisposable
{
    private const string DataChannelLabel = "afgallery";
    private const int ChunkSize = 16 * 1024;
    // Local sanity cap only - the authoritative size guardrail is GallerySharingService's own,
    // enforced when the received bytes are handed to ImportFromFileAsync.
    private const long MaxTransferBytes = 512L * 1024 * 1024;
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // no 0/O/1/I/L
    private const int CodeLength = 6;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly Plugin plugin;

    private LiveShareSignalingClient? signaling;
    private RTCPeerConnection? peerConnection;
    private RTCDataChannel? dataChannel;
    private CancellationTokenSource? sessionCts;
    private string? tempBundlePath;
    private MemoryStream? receiveBuffer;
    private long receiveTotalBytes;
    private int failGuard;

    public GalleryLiveShareService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public bool IsBusy { get; private set; }
    public LiveSharePhase Phase { get; private set; } = LiveSharePhase.Idle;
    public string? PairingCode { get; private set; }
    public float Progress { get; private set; }
    public string? ErrorMessage { get; private set; }

    private static string TempRoot =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "live-share-temp");

    public static void ClearAllTemp()
    {
        try
        {
            if (Directory.Exists(TempRoot))
                Directory.Delete(TempRoot, recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to clear live-share temp directory");
        }
    }

    public void HostAsync(string sharerLabel, IReadOnlySet<Guid>? onlyIds)
    {
        if (IsBusy)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}A live share is already running.");
            return;
        }

        ResetState();
        IsBusy = true;
        Phase = LiveSharePhase.WaitingForPeer;
        PairingCode = GenerateCode();

        var cts = new CancellationTokenSource();
        sessionCts = cts;
        _ = RunHostAsync(sharerLabel, onlyIds, PairingCode, cts.Token);
    }

    public void JoinAsync(string code)
    {
        if (IsBusy)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}A live share is already running.");
            return;
        }

        ResetState();
        IsBusy = true;
        Phase = LiveSharePhase.Connecting;

        var cts = new CancellationTokenSource();
        sessionCts = cts;
        _ = RunGuestAsync(code.Trim(), cts.Token);
    }

    public void Cancel() => Teardown(LiveSharePhase.Idle, errorMessage: null);

    private async Task RunHostAsync(string sharerLabel, IReadOnlySet<Guid>? onlyIds, string code, CancellationToken ct)
    {
        try
        {
            var signal = new LiveShareSignalingClient();
            signaling = signal;
            WireCommonSignalingEvents(signal);
            signal.OnPeerJoined += () => _ = OnHostPeerJoinedAsync(sharerLabel, onlyIds, ct);

            await signal.ConnectAndJoinAsync(plugin.Configuration.SignalingServerUrl, code);
            StartHandshakeTimeoutWatch(ct);
        }
        catch (Exception ex)
        {
            Fail($"Couldn't reach the signaling server: {ex.Message}");
        }
    }

    private async Task OnHostPeerJoinedAsync(string sharerLabel, IReadOnlySet<Guid>? onlyIds, CancellationToken ct)
    {
        try
        {
            SetOnFramework(() => Phase = LiveSharePhase.ExportingBundle);

            Directory.CreateDirectory(TempRoot);
            var tempPath = Path.Combine(TempRoot, $"{Guid.NewGuid():N}{GallerySharingService.FileExtension}");
            tempBundlePath = tempPath;
            plugin.GallerySharing.ExportToFileAsync(sharerLabel, tempPath, onlyIds);

            while (await Plugin.Framework.RunOnFrameworkThread(() => plugin.GallerySharing.IsBusy))
                await Task.Delay(150, ct);

            if (!File.Exists(tempPath))
            {
                Fail("Export failed; check the chat log for details.");
                return;
            }

            SetOnFramework(() => Phase = LiveSharePhase.Handshaking);

            var pc = CreatePeerConnection(ct);
            peerConnection = pc;
            var dc = await pc.createDataChannel(DataChannelLabel, new RTCDataChannelInit { ordered = true });
            dataChannel = dc;
            dc.onopen += () => _ = SendBundleAsync(tempPath, ct);

            var offer = pc.createOffer(new RTCOfferOptions());
            await pc.setLocalDescription(offer);
            await signaling!.SendOfferAsync(offer.sdp);
        }
        catch (Exception ex)
        {
            Fail($"Failed to start the transfer: {ex.Message}");
        }
    }

    private async Task RunGuestAsync(string code, CancellationToken ct)
    {
        try
        {
            var signal = new LiveShareSignalingClient();
            signaling = signal;
            WireCommonSignalingEvents(signal);
            signal.OnOffer += sdp => _ = OnGuestOfferAsync(sdp, ct);

            await signal.ConnectAndJoinAsync(plugin.Configuration.SignalingServerUrl, code);
            StartHandshakeTimeoutWatch(ct);
        }
        catch (Exception ex)
        {
            Fail($"Couldn't reach the signaling server: {ex.Message}");
        }
    }

    private async Task OnGuestOfferAsync(string offerSdp, CancellationToken ct)
    {
        try
        {
            SetOnFramework(() => Phase = LiveSharePhase.Handshaking);

            var pc = CreatePeerConnection(ct);
            peerConnection = pc;
            pc.ondatachannel += dc =>
            {
                dataChannel = dc;
                dc.onmessage += (_, protocol, data) => HandleIncoming(protocol, data);
            };

            pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
            var answer = pc.createAnswer(new RTCAnswerOptions());
            await pc.setLocalDescription(answer);
            await signaling!.SendAnswerAsync(answer.sdp);
        }
        catch (Exception ex)
        {
            Fail($"Failed to accept the connection: {ex.Message}");
        }
    }

    private RTCPeerConnection CreatePeerConnection(CancellationToken ct)
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } },
        };
        var pc = new RTCPeerConnection(config, 0, null, false);

        pc.onicecandidate += candidate =>
        {
            // A null/empty candidate signals end-of-gathering; nothing to relay.
            if (candidate?.candidate is { Length: > 0 })
                _ = signaling!.SendIceCandidateAsync(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
        };
        pc.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
                HandleConnectionLost();
        };

        return pc;
    }

    private void WireCommonSignalingEvents(LiveShareSignalingClient signal)
    {
        signal.OnIceCandidate += (candidate, sdpMid, sdpMLineIndex) =>
        {
            try
            {
                peerConnection?.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = candidate,
                    sdpMid = sdpMid,
                    sdpMLineIndex = (ushort)(sdpMLineIndex ?? 0),
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to add a remote ICE candidate");
            }
        };
        signal.OnPeerLeft += HandleConnectionLost;
        signal.OnServerError += message => Fail(message);
        signal.OnTransportError += message => Fail($"Connection to the signaling server failed: {message}");
    }

    private void HandleConnectionLost()
    {
        var phase = Phase;
        if (phase is LiveSharePhase.Idle or LiveSharePhase.Done or LiveSharePhase.Failed)
            return;

        Fail(phase is LiveSharePhase.WaitingForPeer or LiveSharePhase.Connecting or LiveSharePhase.Handshaking
            ? "Couldn't establish a direct connection — this can happen with certain router/NAT setups. A relay fallback isn't available yet."
            : "The other player disconnected before the transfer finished.");
    }

    private void StartHandshakeTimeoutWatch(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(HandshakeTimeout, ct); }
            catch (OperationCanceledException) { return; }

            if (peerConnection?.connectionState != RTCPeerConnectionState.connected)
                Fail("Couldn't establish a direct connection — this can happen with certain router/NAT setups. A relay fallback isn't available yet.");
        }, ct);
    }

    private async Task SendBundleAsync(string path, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            SetOnFramework(() => Phase = LiveSharePhase.Transferring);

            dataChannel!.send(new JObject { ["totalBytes"] = bytes.LongLength }.ToString(Formatting.None));

            var sent = 0;
            while (sent < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                var take = Math.Min(ChunkSize, bytes.Length - sent);
                var chunk = new byte[take];
                Array.Copy(bytes, sent, chunk, 0, take);
                dataChannel.send(chunk);
                sent += take;

                var progress = (float)sent / bytes.Length;
                SetOnFramework(() => Progress = progress);
            }

            SetOnFramework(() =>
            {
                Phase = LiveSharePhase.Done;
                IsBusy = false;
            });
            TryDeleteTemp(path);
        }
        catch (OperationCanceledException)
        {
            // Session was cancelled/torn down; Teardown already handled state.
        }
        catch (Exception ex)
        {
            Fail($"Failed to send the bundle: {ex.Message}");
        }
    }

    private void HandleIncoming(DataChannelPayloadProtocols protocol, byte[] data)
    {
        try
        {
            if (protocol == DataChannelPayloadProtocols.WebRTC_String)
            {
                var header = JObject.Parse(Encoding.UTF8.GetString(data));
                var totalBytes = header["totalBytes"]?.Value<long>() ?? -1;
                if (totalBytes < 0 || totalBytes > MaxTransferBytes)
                {
                    Fail("The incoming bundle is too large to accept.");
                    return;
                }

                receiveTotalBytes = totalBytes;
                receiveBuffer = new MemoryStream((int)Math.Min(totalBytes, int.MaxValue));
                SetOnFramework(() => Phase = LiveSharePhase.Transferring);
                return;
            }

            if (receiveBuffer == null)
                return; // chunk arrived before the header somehow; drop it defensively

            receiveBuffer.Write(data, 0, data.Length);
            var progress = receiveTotalBytes > 0 ? (float)receiveBuffer.Length / receiveTotalBytes : 0f;
            SetOnFramework(() => Progress = progress);

            if (receiveBuffer.Length >= receiveTotalBytes)
                FinishReceiving();
        }
        catch (Exception ex)
        {
            Fail($"Failed to receive the bundle: {ex.Message}");
        }
    }

    private void FinishReceiving()
    {
        try
        {
            Directory.CreateDirectory(TempRoot);
            var tempPath = Path.Combine(TempRoot, $"{Guid.NewGuid():N}{GallerySharingService.FileExtension}");
            tempBundlePath = tempPath;
            File.WriteAllBytes(tempPath, receiveBuffer!.ToArray());
            receiveBuffer.Dispose();
            receiveBuffer = null;

            SetOnFramework(() => Phase = LiveSharePhase.Importing);
            _ = ImportReceivedBundleAsync(tempPath);
        }
        catch (Exception ex)
        {
            Fail($"Failed to save the received bundle: {ex.Message}");
        }
    }

    private async Task ImportReceivedBundleAsync(string tempPath)
    {
        var imported = false;
        plugin.GallerySharing.ImportFromFileAsync(tempPath, foreign =>
        {
            imported = true;
            plugin.ForeignGallery.Show(foreign);
        });

        while (await Plugin.Framework.RunOnFrameworkThread(() => plugin.GallerySharing.IsBusy))
            await Task.Delay(150);

        TryDeleteTemp(tempPath);

        if (!imported)
        {
            Fail("Import failed; check the chat log for details.");
            return;
        }

        SetOnFramework(() =>
        {
            Phase = LiveSharePhase.Done;
            IsBusy = false;
        });
    }

    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref failGuard, 1) != 0)
            return;

        SetOnFramework(() =>
        {
            Phase = LiveSharePhase.Failed;
            ErrorMessage = message;
            IsBusy = false;
        });
        Plugin.Framework.RunOnFrameworkThread(() => Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Live share: {message}"));
        TeardownConnections();
    }

    private void Teardown(LiveSharePhase phase, string? errorMessage)
    {
        TeardownConnections();
        SetOnFramework(() =>
        {
            Phase = phase;
            ErrorMessage = errorMessage;
            IsBusy = false;
        });
        if (tempBundlePath != null)
            TryDeleteTemp(tempBundlePath);
    }

    // Nulls everything out after disposing it, so a second call (e.g. Cancel() immediately
    // followed by a fresh HostAsync/JoinAsync) is a safe no-op instead of double-disposing.
    private void TeardownConnections()
    {
        sessionCts?.Cancel();
        sessionCts?.Dispose();
        sessionCts = null;
        try { dataChannel?.close(); } catch { /* already gone */ }
        try { peerConnection?.close(); } catch { /* already gone */ }
        peerConnection?.Dispose();
        peerConnection = null;
        dataChannel = null;
        signaling?.Dispose();
        signaling = null;
        receiveBuffer?.Dispose();
        receiveBuffer = null;
    }

    private void ResetState()
    {
        TeardownConnections();
        tempBundlePath = null;
        receiveTotalBytes = 0;
        failGuard = 0;
        PairingCode = null;
        Progress = 0f;
        ErrorMessage = null;
    }

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete live-share temp file {Path}", path);
        }
    }

    private static void SetOnFramework(Action action) => Plugin.Framework.RunOnFrameworkThread(action);

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    public void Dispose() => TeardownConnections();
}
