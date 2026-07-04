using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aetherfit.Services;

// Runs the WD tagger over a design's screenshots on a background thread; the UI polls the per-design
// run state each frame. The session is kept loaded once used - re-reading 400 MB of weights per run
// would be worse than the memory cost.
public sealed class TagSuggestionService : IDisposable
{
    public enum RunState { Running, Done, Failed }

    public sealed record Suggestion(string Tag, float Score);

    public sealed class DesignRun
    {
        public RunState State;
        public int ImageIndex;
        public int ImageCount;
        public List<Suggestion> Results = new();
        public string? Error;
        public int SkippedImages;
        public readonly HashSet<string> Selected = new(StringComparer.OrdinalIgnoreCase);
    }

    private const int MaxSuggestedTags = 30;

    private readonly TagModelStore modelStore;
    private readonly Configuration configuration;

    private readonly object sync = new();
    private readonly Dictionary<Guid, DesignRun> runs = new();
    private WdTagger? tagger;
    private string? taggerModelPath;
    private int activeInferences;
    private bool disposed;

    public TagSuggestionService(TagModelStore modelStore, Configuration configuration)
    {
        this.modelStore = modelStore;
        this.configuration = configuration;
    }

    public DesignRun? GetRun(Guid designId)
    {
        lock (sync)
            return runs.TryGetValue(designId, out var run) ? run : null;
    }

    public bool IsBusy
    {
        get
        {
            lock (sync)
                return runs.Values.Any(r => r.State == RunState.Running);
        }
    }

    public void Dismiss(Guid designId)
    {
        lock (sync)
        {
            if (runs.TryGetValue(designId, out var run) && run.State != RunState.Running)
                runs.Remove(designId);
        }
    }

    public void StartSuggestion(Guid designId, IReadOnlyList<string> imagePaths, IReadOnlyList<string> existingTags)
    {
        DesignRun run;
        lock (sync)
        {
            if (disposed)
                return;
            if (runs.TryGetValue(designId, out var current) && current.State == RunState.Running)
                return;

            run = new DesignRun { State = RunState.Running, ImageCount = imagePaths.Count };
            runs[designId] = run;
        }

        if (modelStore.CurrentState != TagModelStore.State.Ready)
        {
            Fail(run, "The tag model is not downloaded. Download it in Settings first.");
            return;
        }

        var threshold = Math.Clamp(configuration.TagSuggestionThreshold, 0.05f, 0.95f);
        var excluded = new HashSet<string>(existingTags, StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(configuration.TagSuggestionBlacklist);
        var renames = configuration.TagSuggestionRenames
            .Where(kv => kv.Key.Length > 0)
            .Select(kv => (From: kv.Key, To: kv.Value))
            .ToList();
        var paths = imagePaths.ToList();

        Task.Run(() => Run(run, paths, excluded, renames, threshold));
    }

    private void Run(DesignRun run, List<string> paths, HashSet<string> excluded,
        List<(string From, string To)> renames, float threshold)
    {
        try
        {
            if (!modelStore.EnsureNativeRuntimeLoaded())
            {
                Fail(run, "The ONNX runtime is missing. Re-download the model in Settings.");
                return;
            }

            var active = AcquireTagger();
            if (active == null)
                return;

            try
            {
                var best = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < paths.Count; i++)
                {
                    lock (sync)
                    {
                        if (disposed)
                            return;
                        run.ImageIndex = i + 1;
                    }

                    try
                    {
                        foreach (var (tag, score) in active.Tag(paths[i], threshold))
                        {
                            var name = tag;
                            foreach (var (from, to) in renames)
                                name = name.Replace(from, to, StringComparison.OrdinalIgnoreCase);

                            if (!best.TryGetValue(name, out var prev) || score > prev)
                                best[name] = score;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning(ex, "Could not analyze image {Path}", paths[i]);
                        lock (sync) run.SkippedImages++;
                    }
                }

                lock (sync)
                {
                    if (run.SkippedImages >= paths.Count)
                    {
                        run.State = RunState.Failed;
                        run.Error = "None of this design's images could be read.";
                        return;
                    }

                    run.Results = best
                        .Where(kv => !excluded.Contains(kv.Key))
                        .OrderByDescending(kv => kv.Value)
                        .Take(MaxSuggestedTags)
                        .Select(kv => new Suggestion(kv.Key, kv.Value))
                        .ToList();
                    run.State = RunState.Done;
                }
            }
            finally
            {
                ReleaseTagger();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Tag suggestion failed");
            Fail(run, $"Tag analysis failed: {ex.Message} Try re-downloading the model in Settings.");
        }
    }

    // Loading takes seconds, so it happens outside the lock - the draw thread polls GetRun every frame
    // and must never wait on it. The use-count stops Dispose (or a model switch) tearing the native
    // session down under a running inference.
    private WdTagger? AcquireTagger()
    {
        var modelPath = modelStore.ModelPath;
        var labelsPath = modelStore.LabelsPath;

        lock (sync)
        {
            if (disposed)
                return null;
            if (tagger != null)
            {
                if (taggerModelPath == modelPath || activeInferences > 0)
                {
                    activeInferences++;
                    return tagger;
                }

                tagger.Dispose();
                tagger = null;
                taggerModelPath = null;
            }
        }

        var loaded = WdTagger.Load(modelPath, labelsPath);
        lock (sync)
        {
            if (disposed)
            {
                loaded.Dispose();
                return null;
            }

            if (tagger == null)
            {
                tagger = loaded;
                taggerModelPath = modelPath;
            }
            else
            {
                loaded.Dispose();
            }
            activeInferences++;
            return tagger;
        }
    }

    private void ReleaseTagger()
    {
        lock (sync)
        {
            activeInferences--;
            if (disposed && activeInferences == 0)
            {
                tagger?.Dispose();
                tagger = null;
            }
        }
    }

    private void Fail(DesignRun run, string message)
    {
        lock (sync)
        {
            run.State = RunState.Failed;
            run.Error = message;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            if (activeInferences == 0)
            {
                tagger?.Dispose();
                tagger = null;
            }
        }
    }
}
