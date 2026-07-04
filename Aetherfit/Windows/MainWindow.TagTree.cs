using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private void DrawTagTree(bool hasFilter)
    {
        var allLeaves = new List<DesignLeaf>();
        CollectAllLeaves(root, allLeaves);

        // A design appears under every tag it carries, so it can show up multiple times.
        var byTag = new Dictionary<string, List<DesignLeaf>>(StringComparer.OrdinalIgnoreCase);
        var untagged = new List<DesignLeaf>();

        foreach (var leaf in allLeaves)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(leaf.Id, out var cached);
            if (!DesignMatchesFilters(leaf, cached)) continue;

            if (cached == null || cached.Tags.Count == 0)
            {
                untagged.Add(leaf);
                continue;
            }

            foreach (var tag in cached.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!byTag.TryGetValue(tag, out var list))
                {
                    list = new List<DesignLeaf>();
                    byTag[tag] = list;
                }
                list.Add(leaf);
            }
        }

        foreach (var list in byTag.Values)
            list.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));

        foreach (var tag in byTag.Keys.OrderBy(t => t, NaturalStringComparer.OrdinalIgnoreCase))
        {
            if (!DrawJobGroupHeader($"{tag}##tag{tag}", hasFilter))
                continue;

            foreach (var leaf in byTag[tag])
                DrawDesignLeaf(leaf);

            ImGui.TreePop();
        }

        if (untagged.Count > 0 && DrawJobGroupHeader("Untagged##untaggedDesigns", hasFilter))
        {
            DrawTree(BuildFolderTree(untagged), hasFilter);
            ImGui.TreePop();
        }
    }
}
