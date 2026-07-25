using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

// Cover Mode's "Group by source" view: one flat, collapsible section per provider containing that
// provider's designs in the gallery's normal sort order - no folder nesting, Cover Mode doesn't
// reflect folder structure anywhere else either (see GalleryTagGroups).
public partial class MainWindow
{
    private List<(string Label, List<DesignLeaf> Designs)> cachedCoverSourceGroups = new();
    private int cachedCoverSourceGroupVersion = -1;
    private readonly Dictionary<string, bool> coverSourceSectionOpen = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildCoverSourceGroupCache()
    {
        var bySource = new Dictionary<DesignSource, List<DesignLeaf>>();
        foreach (var leaf in cachedVisible)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(leaf.Id, out var cached);
            var source = cached?.Source ?? DesignSource.Glamourer;
            if (!bySource.TryGetValue(source, out var list))
            {
                list = new List<DesignLeaf>();
                bySource[source] = list;
            }
            list.Add(leaf);
        }

        cachedCoverSourceGroups = plugin.DesignProviders
            .Where(p => bySource.ContainsKey(p.Source))
            .Select(p => (p.DisplayName, bySource[p.Source]))
            .ToList();

        cachedCoverSourceGroupVersion = galleryCacheVersion;
    }

    private void DrawCoverGroupedBySource()
    {
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        if (cachedCoverSourceGroupVersion != galleryCacheVersion)
            RebuildCoverSourceGroupCache();

        foreach (var (label, designs) in cachedCoverSourceGroups)
        {
            ImGui.Separator();
            if (!DrawCoverSourceSectionHeader($"{label} ({designs.Count})", label))
                continue;

            ImGui.Spacing();
            var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
            DrawCoverGridRange(designs, 0, designs.Count, columns, thumbWidth, thumbHeight);
            ImGui.Spacing();
        }
    }

    private bool DrawCoverSourceSectionHeader(string label, string key)
    {
        if (!coverSourceSectionOpen.TryGetValue(key, out var open))
            open = true;
        using var id = ImRaii.PushId(key);
        var result = DrawCollapsibleSubheader(label, ref open);
        coverSourceSectionOpen[key] = open;
        return result;
    }
}
