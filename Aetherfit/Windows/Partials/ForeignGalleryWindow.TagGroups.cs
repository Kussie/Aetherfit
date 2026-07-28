using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Models;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

// "Group by tags" for the shared gallery view - same composite-tag nesting and collapsible-section
// look as Cover Mode's own tag grouping (MainWindow.GalleryTagGroups.cs), built fresh each draw from
// the filtered design list rather than cached, since a shared gallery's design count is small and fixed.
public sealed partial class ForeignGalleryWindow
{
    private sealed class ForeignTagNode
    {
        public SortedDictionary<string, ForeignTagNode> Children { get; } = new(NaturalStringComparer.OrdinalIgnoreCase);
        public List<ForeignDesign> Designs { get; } = new();
    }

    private void DrawGroupedByTags(List<ForeignDesign> visible)
    {
        var tagRoot = new ForeignTagNode();
        var untagged = new List<ForeignDesign>();

        foreach (var design in visible)
        {
            if (design.Tags.Count == 0)
            {
                untagged.Add(design);
                continue;
            }

            foreach (var tag in design.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var segments = tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segments.Length == 0)
                    segments = new[] { tag };

                var node = tagRoot;
                foreach (var segment in segments)
                {
                    if (!node.Children.TryGetValue(segment, out var child))
                    {
                        child = new ForeignTagNode();
                        node.Children[segment] = child;
                    }
                    node = child;
                }

                // Two raw tags can normalise to the same path (e.g. "a/b" and "a / b") - don't list the design twice.
                if (!node.Designs.Contains(design))
                    node.Designs.Add(design);
            }
        }

        var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();

        foreach (var (name, child) in tagRoot.Children)
            DrawTagGroupNode(name, name, child, columns, thumbWidth, thumbHeight);

        if (untagged.Count > 0)
        {
            ImGui.Separator();
            if (DrawTagSectionHeader($"Untagged ({untagged.Count})", "untagged"))
            {
                ImGui.Spacing();
                DrawGridRange(untagged, 0, untagged.Count, columns, thumbWidth, thumbHeight);
                ImGui.Spacing();
            }
        }
    }

    private void DrawTagGroupNode(string name, string path, ForeignTagNode node, int columns, float thumbWidth, float thumbHeight)
    {
        ImGui.Separator();
        var label = $"{name} ({CountTagNodeDesigns(node)})";
        if (!DrawTagSectionHeader(label, path))
            return;

        ImGui.Indent();
        ImGui.Spacing();

        foreach (var (childName, child) in node.Children)
            DrawTagGroupNode(childName, $"{path}/{childName}", child, columns, thumbWidth, thumbHeight);

        if (node.Designs.Count > 0)
        {
            DrawGridRange(node.Designs, 0, node.Designs.Count, columns, thumbWidth, thumbHeight);
            ImGui.Spacing();
        }

        ImGui.Unindent();
    }

    // Total designs anywhere in this branch (own + every descendant's), so a collapsed parent still
    // conveys how much is inside it.
    private static int CountTagNodeDesigns(ForeignTagNode node)
    {
        var count = node.Designs.Count;
        foreach (var child in node.Children.Values)
            count += CountTagNodeDesigns(child);
        return count;
    }
}
