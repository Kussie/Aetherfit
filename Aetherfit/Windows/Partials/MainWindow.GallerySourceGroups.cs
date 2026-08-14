using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

// Cover Mode's "Group by source" view: one flat, collapsible section per provider containing that
// provider's designs in the gallery's normal sort order - no folder nesting, Cover Mode doesn't
// reflect folder structure anywhere else either (see GalleryTagGroups). Simple Glamour Switcher is the
// one exception: its designs get a further nested collapsible section per owning character (SourceSubGroup) -
// a coarser, two-level grouping rather than true folder nesting, to fit Cover Mode's flat-sections shape.
public partial class MainWindow
{
    private readonly record struct CoverSourceSubGroup(string Label, List<DesignLeaf> Designs);
    private readonly record struct CoverSourceGroup(string Label, List<DesignLeaf> Designs, List<CoverSourceSubGroup> SubGroups);

    private List<CoverSourceGroup> cachedCoverSourceGroups = new();
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
            .Select(p =>
            {
                var leaves = bySource[p.Source];
                var direct = leaves.Where(l => l.SourceSubGroup == null).ToList();
                var subGroups = leaves.Where(l => l.SourceSubGroup != null)
                    .GroupBy(l => l.SourceSubGroup!)
                    .OrderBy(g => g.Key, NaturalStringComparer.OrdinalIgnoreCase)
                    .Select(g => new CoverSourceSubGroup(g.Key, g.ToList()))
                    .ToList();
                return new CoverSourceGroup(p.DisplayName, direct, subGroups);
            })
            .ToList();

        cachedCoverSourceGroupVersion = galleryCacheVersion;
    }

    private void DrawCoverGroupedBySource()
    {
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        if (cachedCoverSourceGroupVersion != galleryCacheVersion)
            RebuildCoverSourceGroupCache();

        foreach (var group in cachedCoverSourceGroups)
        {
            var total = group.Designs.Count + group.SubGroups.Sum(s => s.Designs.Count);
            ImGui.Separator();
            if (!Pills.DrawKeyedSubheader(coverSourceSectionOpen, $"{group.Label} ({total})", group.Label))
                continue;

            ImGui.Spacing();
            if (group.Designs.Count > 0)
            {
                var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
                DrawCoverGridRange(group.Designs, 0, group.Designs.Count, columns, thumbWidth, thumbHeight);
                if (group.SubGroups.Count > 0)
                    ImGui.Spacing();
            }

            foreach (var sub in group.SubGroups)
            {
                if (!Pills.DrawKeyedSubheader(coverSourceSectionOpen, $"{sub.Label} ({sub.Designs.Count})", $"{group.Label}/{sub.Label}"))
                    continue;

                ImGui.Spacing();
                var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
                DrawCoverGridRange(sub.Designs, 0, sub.Designs.Count, columns, thumbWidth, thumbHeight);
                ImGui.Spacing();
            }

            ImGui.Spacing();
        }
    }

}
