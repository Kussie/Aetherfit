using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

// "Group by source" - each provider's own designs under its own top-level node, with that
// provider's own folder structure preserved underneath (the same shape the default tree used to
// always show before sources were merged; now opt-in instead of automatic).
public partial class MainWindow
{
    private readonly record struct SourceGroup(string Label, FolderNode Folder);

    private List<SourceGroup> cachedSourceGroups = new();
    private int cachedSourceTreeGeneration = -1;
    private FilterSnapshot cachedSourceTreeFilterSnapshot;
    private Dictionary<string, bool> cachedSourceTreeFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, bool> cachedSourceTreeFilterJobs = new();

    private bool IsSourceTreeCacheStale() =>
        cachedSourceTreeGeneration != designListGeneration ||
        cachedSourceTreeFilterSnapshot != CaptureFilterSnapshot() ||
        !FiltersEqual(cachedSourceTreeFilterTags, filterTags) ||
        !FiltersEqual(cachedSourceTreeFilterJobs, filterJobs);

    private void RebuildSourceTreeCache()
    {
        var allLeaves = new List<DesignLeaf>();
        CollectAllLeaves(root, allLeaves);

        var bySource = new Dictionary<DesignSource, List<DesignLeaf>>();
        foreach (var leaf in allLeaves)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(leaf.Id, out var cached);
            if (!DesignMatchesFilters(leaf, cached)) continue;

            var source = cached?.Source ?? DesignSource.Glamourer;
            if (!bySource.TryGetValue(source, out var list))
            {
                list = new List<DesignLeaf>();
                bySource[source] = list;
            }
            list.Add(leaf);
        }

        // Ordered by the providers' own registration order rather than the dictionary's, and skips
        // any source with nothing to show (e.g. Glamaholic before it's ever been used).
        cachedSourceGroups = plugin.DesignProviders
            .Where(p => bySource.ContainsKey(p.Source))
            .Select(p => new SourceGroup(p.DisplayName, BuildFolderTree(bySource[p.Source])))
            .ToList();

        cachedSourceTreeGeneration = designListGeneration;
        cachedSourceTreeFilterSnapshot = CaptureFilterSnapshot();
        cachedSourceTreeFilterTags = new(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedSourceTreeFilterJobs = new(filterJobs);
    }

    private void DrawSourceTree(bool hasFilter)
    {
        if (IsSourceTreeCacheStale())
            RebuildSourceTreeCache();

        foreach (var group in cachedSourceGroups)
        {
            var label = $"{group.Label}##source{group.Label}";
            ForceOpenIfFiltering(label, hasFilter);
            if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                DrawTree(group.Folder, hasFilter);
                ImGui.TreePop();
            }
        }
    }
}
