using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Sharing;

public enum LiveSharePhase
{
    Idle,
    ExportingBundle,    // host: building the temp .afgallery file
    Uploading,          // host: sending it to the live-share backend
    Ready,              // host: code generated, waiting to be redeemed (or already forgotten about)
    Downloading,        // guest: fetching the bundle by code
    Importing,          // guest: handing the received file to GallerySharingService
    Done,
    Failed,
}

// Shares a gallery bundle with another player via a short-lived upload/download backend (a Cloudflare
// Worker + R2 bucket) instead of a live peer-to-peer connection. It never reimplements the bundle
// format: the host does a completely normal export to a temp file and uploads those exact bytes; the
// guest writes the response to a temp file and does a completely normal import. Neither side needs to
// be online at the same time - the code just has to be redeemed before it expires.
public sealed class GalleryLiveShareService : IDisposable
{
    // Stays comfortably under the Worker's own ~95MB cap (itself under Cloudflare Workers' 100MB
    // free-plan request body limit), so an oversized bundle is rejected locally before attempting an
    // upload that would fail anyway.
    private const long MaxUploadBytes = 90L * 1024 * 1024;
    private const string ApiBaseUrl = "https://aetherfit-live-share.kussie.workers.dev";
    private const string ClientHeaderName = "X-Aetherfit-Client";
    private const string ClientHeaderValue = "aetherfit-plugin";
    private const string IdentityHeaderName = "X-Aetherfit-Identity";
    private static readonly HttpClient Http = new();

    private readonly Plugin plugin;
    private CancellationTokenSource? operationCts;
    private string? tempBundlePath;

    public GalleryLiveShareService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public bool IsBusy { get; private set; }
    public LiveSharePhase Phase { get; private set; } = LiveSharePhase.Idle;
    public string? PairingCode { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public int RequestedTtlSeconds { get; private set; }
    public string? ErrorMessage { get; private set; }

    public float Progress { get; private set; }

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

    public void HostAsync(string sharerLabel, IReadOnlySet<Guid>? onlyIds, int ttlMinutes)
    {
        if (!plugin.FeatureFlags.EnableLiveSharing)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Live sharing is temporarily disabled.");
            return;
        }

        if (IsBusy)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}A live share is already running.");
            return;
        }

        ResetState();
        IsBusy = true;
        Phase = LiveSharePhase.ExportingBundle;
        Progress = 0.05f;
        RequestedTtlSeconds = ttlMinutes * 60;

        var cts = new CancellationTokenSource();
        operationCts = cts;
        _ = RunHostAsync(sharerLabel, onlyIds, cts.Token);
    }

    public void JoinAsync(string code)
    {
        if (!plugin.FeatureFlags.EnableLiveSharing)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Live sharing is temporarily disabled.");
            return;
        }

        if (IsBusy)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}A live share is already running.");
            return;
        }

        ResetState();
        IsBusy = true;
        Phase = LiveSharePhase.Downloading;

        var cts = new CancellationTokenSource();
        operationCts = cts;
        _ = RunGuestAsync(code.Trim(), cts.Token);
    }

    // Also used to dismiss a finished/failed modal - always resets fully back to Idle so reopening
    // the modal shows a fresh picker rather than stale state.
    public void Cancel() => Teardown(LiveSharePhase.Idle, errorMessage: null);

    private async Task RunHostAsync(string sharerLabel, IReadOnlySet<Guid>? onlyIds, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(TempRoot);
            var tempPath = Path.Combine(TempRoot, $"{Guid.NewGuid():N}{GallerySharingService.FileExtension}");
            tempBundlePath = tempPath;
            // WaitAsync throws on cancellation without cancelling the export itself - it just finishes
            // orphaned in the background, same as the old poll loop breaking early on a cancelled ct.
            await plugin.GallerySharing.ExportToFileAsync(sharerLabel, tempPath, onlyIds).WaitAsync(ct);

            if (!File.Exists(tempPath))
            {
                Fail("Export failed; check the chat log for details.");
                return;
            }

            var bytes = await File.ReadAllBytesAsync(tempPath, ct);
            if (bytes.LongLength > MaxUploadBytes)
            {
                Fail($"That gallery is too large to share live ({bytes.LongLength / 1024 / 1024}MB, "
                   + $"limit ~{MaxUploadBytes / 1024 / 1024}MB). Try a filtered/smaller share, or use the regular file export instead.");
                return;
            }

            SetOnFramework(() => Phase = LiveSharePhase.Uploading);

            using var content = new ProgressableByteArrayContent(bytes, fraction =>
                SetOnFramework(() => Progress = 0.05f + (float)fraction * 0.95f));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/upload?ttl={RequestedTtlSeconds}") { Content = content };
            request.Headers.Add(ClientHeaderName, ClientHeaderValue);
            request.Headers.Add(IdentityHeaderName, GetOrCreateInstallId());
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Fail($"Upload failed ({(int)response.StatusCode}).");
                return;
            }

            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            var code = json["code"]?.ToString();
            var expiresAtMs = json["expiresAt"]?.Value<long>() ?? 0;
            if (string.IsNullOrEmpty(code) || expiresAtMs <= 0)
            {
                Fail("The server returned an unexpected response.");
                return;
            }

            TryDeleteTemp(tempPath);
            SetOnFramework(() =>
            {
                PairingCode = code;
                ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs);
                Phase = LiveSharePhase.Ready;
                IsBusy = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled/torn down; Teardown already handled state.
        }
        catch (Exception ex)
        {
            Fail($"Failed to share the gallery: {ex.Message}");
        }
    }

    private async Task RunGuestAsync(string code, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                Fail("Enter a pairing code first.");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/download/{Uri.EscapeDataString(code)}");
            request.Headers.Add(ClientHeaderName, ClientHeaderValue);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                Fail("That code is invalid or has expired.");
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                Fail($"Download failed ({(int)response.StatusCode}).");
                return;
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var buffer = new byte[81920];
            using var bodyStream = new MemoryStream();
            await using (var responseStream = await response.Content.ReadAsStreamAsync(ct))
            {
                int read;
                while ((read = await responseStream.ReadAsync(buffer, ct)) > 0)
                {
                    bodyStream.Write(buffer, 0, read);
                    if (totalBytes > 0)
                    {
                        var fraction = (float)bodyStream.Length / totalBytes * 0.9f;
                        SetOnFramework(() => Progress = fraction);
                    }
                }
            }
            var bytes = bodyStream.ToArray();

            Directory.CreateDirectory(TempRoot);
            var tempPath = Path.Combine(TempRoot, $"{Guid.NewGuid():N}{GallerySharingService.FileExtension}");
            tempBundlePath = tempPath;
            await File.WriteAllBytesAsync(tempPath, bytes, ct);

            SetOnFramework(() =>
            {
                Phase = LiveSharePhase.Importing;
                Progress = 0.95f;
            });

            var imported = false;
            await plugin.GallerySharing.ImportFromFileAsync(tempPath, foreign =>
            {
                imported = true;
                plugin.ForeignGallery.Show(foreign);
            }).WaitAsync(ct);

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
        catch (OperationCanceledException)
        {
            // Cancelled/torn down; Teardown already handled state.
        }
        catch (Exception ex)
        {
            Fail($"Failed to receive the gallery: {ex.Message}");
        }
    }

    private void Fail(string message)
    {
        SetOnFramework(() =>
        {
            Phase = LiveSharePhase.Failed;
            ErrorMessage = message;
            IsBusy = false;
        });
        Plugin.Framework.RunOnFrameworkThread(() => Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Live share: {message}"));
        if (tempBundlePath != null)
            TryDeleteTemp(tempBundlePath);
    }

    private void Teardown(LiveSharePhase phase, string? errorMessage)
    {
        operationCts?.Cancel();
        operationCts?.Dispose();
        operationCts = null;
        SetOnFramework(() =>
        {
            Phase = phase;
            ErrorMessage = errorMessage;
            IsBusy = false;
        });
        if (tempBundlePath != null)
            TryDeleteTemp(tempBundlePath);
    }

    private void ResetState()
    {
        operationCts?.Cancel();
        operationCts?.Dispose();
        operationCts = null;
        tempBundlePath = null;
        PairingCode = null;
        ExpiresAt = null;
        RequestedTtlSeconds = 0;
        ErrorMessage = null;
        Progress = 0f;
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

    private string GetOrCreateInstallId()
    {
        if (string.IsNullOrEmpty(plugin.Configuration.LiveShareInstallId))
        {
            plugin.Configuration.LiveShareInstallId = Guid.NewGuid().ToString("N");
            plugin.Configuration.Save();
        }
        return plugin.Configuration.LiveShareInstallId;
    }

    public void Dispose()
    {
        operationCts?.Cancel();
        operationCts?.Dispose();
    }

    // Reports upload progress as the body is streamed - HttpClient has no built-in way to observe
    // this for a plain ByteArrayContent.
    private sealed class ProgressableByteArrayContent : HttpContent
    {
        private const int ChunkSize = 65536;
        private readonly byte[] data;
        private readonly Action<double> onProgress;

        public ProgressableByteArrayContent(byte[] data, Action<double> onProgress)
        {
            this.data = data;
            this.onProgress = onProgress;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < data.Length)
            {
                var count = Math.Min(ChunkSize, data.Length - written);
                await stream.WriteAsync(data.AsMemory(written, count), cancellationToken);
                written += count;
                onProgress((double)written / data.Length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = data.Length;
            return true;
        }
    }
}
