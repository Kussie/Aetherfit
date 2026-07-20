using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace Aetherfit.Services;

// Downloads the tagger model + native ONNX Runtime into {ConfigDirectory}/models on first use,
// so none of it has to ship in the plugin zip.
public sealed class TagModelStore : IDisposable
{
    public enum State { NotDownloaded, Downloading, Ready, Failed }

    // The v3 taggers are interchangeable (same preprocessing, same output shape) as long as each
    // model is paired with its own selected_tags.csv.
    public sealed record ModelInfo(
        string Id, string DisplayName, string Revision, string Description, long ModelBytes);

    public static readonly IReadOnlyList<ModelInfo> Models = new[]
    {
        new ModelInfo("wd-vit-tagger-v3", "ViT v3",
            "7f6b584d0bd3f55c4531f14ba3d4761b2bccdc0f",
            "Balanced quality and speed — a good default.", 378_536_310),
        new ModelInfo("wd-swinv2-tagger-v3", "SwinV2 v3",
            "627aef95638667ddcaa3ac8ae625e88ea5b02f51",
            "Slightly better fine detail than ViT at similar speed.", 467_460_978),
        new ModelInfo("wd-eva02-large-tagger-v3", "EVA02-Large v3",
            "b25b82a03f7282e41aa2f257a52c7583b710bd1c",
            "Best quality, but a much larger download and slower on CPU.", 1_260_435_999),
    };

    private const string OrtVersion = "1.22.1";
    private const string OrtZipUrl =
        $"https://github.com/microsoft/onnxruntime/releases/download/v{OrtVersion}/onnxruntime-win-x64-{OrtVersion}.zip";
    private const string OrtZipSha256 = "855276cd4be3cda14fe636c69eb038d75bf5bcd552bda1193a5d79c51f436dfe";
    private const long OrtZipBytes = 73_731_806;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Configuration configuration;
    private readonly string modelsRoot;
    private readonly string runtimeDir;

    private readonly object sync = new();
    private State state;
    private string? currentFile;
    private float progress;
    private string? lastError;
    private CancellationTokenSource? downloadCts;
    private Task? downloadTask;

    public TagModelStore(Configuration configuration)
    {
        this.configuration = configuration;
        modelsRoot = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "models");
        runtimeDir = Path.Combine(modelsRoot, "onnxruntime", OrtVersion);

        if (FilesPresent(SelectedModel))
            state = State.Ready;
    }

    public ModelInfo SelectedModel
        => Models.FirstOrDefault(m => m.Id == configuration.TagSuggestionModel) ?? Models[0];

    public string ModelPath => Path.Combine(modelsRoot, SelectedModel.Id, "model.onnx");
    public string LabelsPath => Path.Combine(modelsRoot, SelectedModel.Id, "selected_tags.csv");
    private string OnnxRuntimeDllPath => Path.Combine(runtimeDir, "onnxruntime.dll");

    public State CurrentState { get { lock (sync) return state; } }
    public string? CurrentFile { get { lock (sync) return currentFile; } }
    public float Progress { get { lock (sync) return progress; } }
    public string? LastError { get { lock (sync) return lastError; } }

    public long RemainingDownloadBytes => GetSizes().Remaining;

    public long TotalBytesOnDisk => GetSizes().OnDisk;

    // The settings window reads the two size properties every frame while open; cache the file-system
    // walk behind a short TTL so that doesn't turn into per-frame disk IO.
    private (long Remaining, long OnDisk)? sizeCache;
    private DateTime sizeCacheAtUtc;
    private static readonly TimeSpan SizeCacheTtl = TimeSpan.FromSeconds(5);

    private (long Remaining, long OnDisk) GetSizes()
    {
        lock (sync)
        {
            if (sizeCache is { } cached && DateTime.UtcNow - sizeCacheAtUtc < SizeCacheTtl)
                return cached;
        }

        var remaining = (File.Exists(ModelPath) ? 0 : SelectedModel.ModelBytes)
                      + (File.Exists(OnnxRuntimeDllPath) ? 0 : OrtZipBytes);
        var onDisk = Directory.Exists(modelsRoot)
            ? new DirectoryInfo(modelsRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

        lock (sync)
        {
            sizeCache = (remaining, onDisk);
            sizeCacheAtUtc = DateTime.UtcNow;
            return sizeCache.Value;
        }
    }

    public bool IsModelDownloaded(ModelInfo model)
        => File.Exists(Path.Combine(modelsRoot, model.Id, "model.onnx"));

    private bool FilesPresent(ModelInfo model)
        => IsModelDownloaded(model)
        && File.Exists(Path.Combine(modelsRoot, model.Id, "selected_tags.csv"))
        && File.Exists(OnnxRuntimeDllPath);

    // The settings window disables the combo while a download runs, so an in-flight download can't
    // have its target switched under it.
    public void SelectModel(string id)
    {
        if (configuration.TagSuggestionModel == id)
            return;

        configuration.TagSuggestionModel = id;
        configuration.Save();

        lock (sync)
        {
            if (state == State.Downloading)
                return;
            lastError = null;
            sizeCache = null;
            state = FilesPresent(SelectedModel) ? State.Ready : State.NotDownloaded;
        }
    }

    public void StartDownload()
    {
        lock (sync)
        {
            if (state == State.Downloading)
                return;

            state = State.Downloading;
            lastError = null;
            progress = 0f;
            currentFile = null;
            var cts = new CancellationTokenSource();
            downloadCts = cts;
            var model = SelectedModel;
            downloadTask = Task.Run(() => DownloadAll(model, cts), CancellationToken.None);
        }
    }

    public void CancelDownload()
    {
        lock (sync)
            downloadCts?.Cancel();
    }

    // Deletes every model plus the runtime. The settings window blocks this while an inference runs.
    public void DeleteAll()
    {
        lock (sync)
        {
            if (state == State.Downloading)
                return;

            try
            {
                if (Directory.Exists(modelsRoot))
                    Directory.Delete(modelsRoot, true);
                state = State.NotDownloaded;
                lastError = null;
            }
            catch (Exception ex)
            {
                // The native dll stays locked once loaded into the process; the model files still get removed.
                Plugin.Log.Warning(ex, "Could not fully delete tag model files");
                state = FilesPresent(SelectedModel) ? State.Ready : State.NotDownloaded;
                lastError = "Some files are in use and could not be deleted. Restart the game and try again.";
            }
            finally
            {
                sizeCache = null;
            }
        }
    }

    private void DownloadAll(ModelInfo model, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var modelDir = Path.Combine(modelsRoot, model.Id);
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(runtimeDir);

            var baseUrl = $"https://huggingface.co/SmilingWolf/{model.Id}/resolve/{model.Revision}";
            DownloadFile($"{baseUrl}/selected_tags.csv", Path.Combine(modelDir, "selected_tags.csv"),
                "selected_tags.csv", token);
            DownloadFile($"{baseUrl}/model.onnx", Path.Combine(modelDir, "model.onnx"),
                "model.onnx", token);

            if (!File.Exists(OnnxRuntimeDllPath))
            {
                var zipPath = Path.Combine(runtimeDir, "onnxruntime.zip");
                DownloadFile(OrtZipUrl, zipPath, "onnxruntime (runtime)", token);
                VerifySha256(zipPath, OrtZipSha256);
                ExtractRuntime(zipPath);
                File.Delete(zipPath);
            }

            lock (sync)
            {
                if (FilesPresent(model))
                {
                    state = State.Ready;
                }
                else
                {
                    state = State.Failed;
                    lastError = "Download finished but files are missing. Try again.";
                }
                currentFile = null;
                sizeCache = null;
            }
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                state = FilesPresent(SelectedModel) ? State.Ready : State.NotDownloaded;
                lastError = null;
                currentFile = null;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Tag model download failed");
            lock (sync)
            {
                state = State.Failed;
                lastError = ex.Message;
                currentFile = null;
            }
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(downloadCts, cts))
                    downloadCts = null;
                cts.Dispose();
            }
        }
    }

    private void DownloadFile(string url, string targetPath, string displayName, CancellationToken token)
    {
        if (File.Exists(targetPath))
            return;

        var partPath = targetPath + ".part";
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var resuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var remaining = response.Content.Headers.ContentLength;
        var totalBytes = remaining.HasValue ? remaining.Value + (resuming ? existing : 0) : (long?)null;

        lock (sync)
        {
            currentFile = displayName;
            progress = 0f;
        }

        using (var http = response.Content.ReadAsStream(token))
        using (var file = new FileStream(partPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write))
        {
            var written = resuming ? existing : 0L;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = http.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                file.Write(buffer, 0, read);
                written += read;
                if (totalBytes is > 0)
                {
                    var p = (float)written / totalBytes.Value;
                    lock (sync) progress = p;
                }
            }

            if (totalBytes.HasValue && written != totalBytes.Value)
                throw new IOException($"{displayName}: download ended early ({written} of {totalBytes.Value} bytes).");
        }

        File.Move(partPath, targetPath, true);
    }

    private static void VerifySha256(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The ONNX Runtime download failed its integrity check. Try again.");
        }
    }

    private void ExtractRuntime(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var name in new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll" })
        {
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                e.FullName.Contains("/lib/", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidDataException($"The ONNX Runtime archive is missing {name}.");
            entry.ExtractToFile(Path.Combine(runtimeDir, name), true);
        }
    }

    // The native dll sits in our config dir where the loader would never find it, so hook the managed
    // assembly's dll resolution. Has to happen once per plugin load - each reload is a new load context.
    private bool resolverRegistered;

    public bool EnsureNativeRuntimeLoaded()
    {
        if (!File.Exists(OnnxRuntimeDllPath))
            return false;

        lock (sync)
        {
            if (resolverRegistered)
                return true;

            try
            {
                var dir = runtimeDir;
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
                {
                    if (!name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
                        return IntPtr.Zero;
                    var candidate = Path.Combine(dir, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
                    return NativeLibrary.TryLoad(candidate, out var handle) ? handle : IntPtr.Zero;
                });
            }
            catch (InvalidOperationException)
            {
                // A resolver from an earlier load of this same assembly instance is already in place.
            }

            resolverRegistered = true;
            return true;
        }
    }

    public void Dispose()
    {
        CancelDownload();
        try { downloadTask?.Wait(TimeSpan.FromSeconds(5)); }
        catch { /* cancellation surfaces as an exception here */ }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aetherfit");
        return client;
    }
}
