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
    private HashSet<EquipmentSlot> cachedSourceTreeFilterEquipmentSlots = new();
    private bool cachedSourceTreeGlamaholicEnabled = true;
    private bool cachedSourceTreeGlamourPlateEnabled = true;
    private bool cachedSourceTreeSimpleGlamourSwitcherEnabled = true;

    private bool IsSourceTreeCacheStale() =>
        cachedSourceTreeGeneration != designListGeneration ||
        cachedSourceTreeFilterSnapshot != CaptureFilterSnapshot() ||
        !FiltersEqual(cachedSourceTreeFilterTags, filterTags) ||
        !FiltersEqual(cachedSourceTreeFilterJobs, filterJobs) ||
        !cachedSourceTreeFilterEquipmentSlots.SetEquals(filterEquipmentSlots) ||
        cachedSourceTreeGlamaholicEnabled != plugin.Configuration.GlamaholicEnabled ||
        cachedSourceTreeGlamourPlateEnabled != plugin.Configuration.GlamourPlateEnabled ||
        cachedSourceTreeSimpleGlamourSwitcherEnabled != plugin.Configuration.SimpleGlamourSwitcherEnabled;

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

        cachedSourceGroups = plugin.DesignProviders
            .Where(p => bySource.ContainsKey(p.Source))
            .Select(p => new SourceGroup(p.DisplayName, BuildFolderTree(bySource[p.Source])))
            .ToList();

        cachedSourceTreeGeneration = designListGeneration;
        cachedSourceTreeFilterSnapshot = CaptureFilterSnapshot();
        cachedSourceTreeFilterTags = new(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedSourceTreeFilterJobs = new(filterJobs);
        cachedSourceTreeFilterEquipmentSlots = new(filterEquipmentSlots);
        cachedSourceTreeGlamaholicEnabled = plugin.Configuration.GlamaholicEnabled;
        cachedSourceTreeGlamourPlateEnabled = plugin.Configuration.GlamourPlateEnabled;
        cachedSourceTreeSimpleGlamourSwitcherEnabled = plugin.Configuration.SimpleGlamourSwitcherEnabled;
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
