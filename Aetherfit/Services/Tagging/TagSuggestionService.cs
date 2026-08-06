using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aetherfit.Services.Tagging;

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

                if (configuration.TagSuggestionComposites)
                {
                    AddCompositeTags(best);
                    AddColourTags(best);
                }

                // "sheer" isn't a Danbooru synonym of "see-through", so it can't ride the composite map.
                // Runs after composites so "see-through dress" etc. and their "dress/see-through dress"
                // composites fold in too, rather than just the bare "see-through" tag.
                ReplaceTagSubstring(best, "see-through", "sheer");

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
                        .Where(kv => !HideAsCompositeSource(kv.Key, best, excluded))
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

    // Only meaningful for the CompositeTags.Map relationship (e.g. "crop top" -> "tops/crop top") -
    // AddColourTags' "colour/blue" is a different facet extracted from a tag like "blue bikini", not
    // a redundant restatement of it, so that pairing is deliberately left alone here. Only hides the
    // flat tag when its composite actually survived into best AND isn't itself blacklisted - otherwise
    // the user could end up with neither suggested.
    private bool HideAsCompositeSource(string tag, Dictionary<string, float> best, HashSet<string> excluded)
        => configuration.TagSuggestionHideCompositeSources
           && CompositeTags.Map.TryGetValue(tag, out var composite)
           && best.ContainsKey(composite)
           && !excluded.Contains(composite);

    // Adds "category/type" composite tags (e.g. swimsuit/bikini) derived from Danbooru implications,
    // scored by the strongest contributing leaf. They sit alongside the flat tags they came from.
    private static void AddCompositeTags(Dictionary<string, float> best)
    {
        var map = CompositeTags.Map;
        if (map.Count == 0)
            return;

        var additions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, score) in best)
        {
            if (map.TryGetValue(tag, out var composite)
                && (!additions.TryGetValue(composite, out var prev) || score > prev))
                additions[composite] = score;
        }

        foreach (var (composite, score) in additions)
        {
            if (!best.TryGetValue(composite, out var prev) || score > prev)
                best[composite] = score;
        }
    }

    // The colour adjectives the composite-map generator strips from garment types (tools/generate_composite_tags.py).
    // Multi-word entries first so "light blue" isn't consumed as "light" + leftover.
    private static readonly string[] ColourWords =
    {
        "light blue", "dark blue", "dark green", "light brown", "light green",
        "black", "blue", "brown", "green", "grey", "gray", "orange", "pink", "purple",
        "red", "white", "yellow", "aqua", "gold", "silver", "multicolored", "two-toned",
        "rainbow", "tan", "beige",
    };

    // Adds "colour/<colour>" composites for colour-prefixed garment tags (e.g. "blue bikini" ->
    // colour/blue). Only clothing counts - "blue eyes" or "red hair" say nothing about the outfit.
    private static void AddColourTags(Dictionary<string, float> best)
    {
        var additions = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, score) in best)
        {
            foreach (var colour in ExtractClothingColours(tag))
            {
                var composite = $"colour/{colour}";
                if (!additions.TryGetValue(composite, out var prev) || score > prev)
                    additions[composite] = score;
            }
        }

        foreach (var (composite, score) in additions)
        {
            if (!best.TryGetValue(composite, out var prev) || score > prev)
                best[composite] = score;
        }
    }

    private static IReadOnlyList<string> ExtractClothingColours(string tag)
    {
        List<string>? colours = null;
        var rest = tag;
        while (true)
        {
            var colour = Array.Find(ColourWords, c =>
                rest.StartsWith(c, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == c.Length || rest[c.Length] == ' '));
            if (colour == null)
                break;
            (colours ??= new()).Add(colour == "gray" ? "grey" : colour);
            rest = rest[colour.Length..].TrimStart();
        }

        if (colours == null)
            return Array.Empty<string>();

        // A bare colour tag counts as-is; otherwise the remainder must be a garment the composite
        // map knows, so body traits and scenery don't colour the outfit.
        if (rest.Length > 0
            && !CompositeTags.ClothingTerms.Contains(rest)
            && !CompositeTags.Map.ContainsKey(tag))
            return Array.Empty<string>();

        return colours;
    }

    // Renames every key containing "from" to its "to" variant (e.g. "see-through dress" -> "sheer dress"),
    // merging into an existing target key by keeping the higher score.
    private static void ReplaceTagSubstring(Dictionary<string, float> tags, string from, string to)
    {
        var matches = tags.Where(kv => kv.Key.Contains(from, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (key, score) in matches)
        {
            tags.Remove(key);
            var renamed = key.Replace(from, to, StringComparison.OrdinalIgnoreCase);
            if (!tags.TryGetValue(renamed, out var prev) || score > prev)
                tags[renamed] = score;
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
