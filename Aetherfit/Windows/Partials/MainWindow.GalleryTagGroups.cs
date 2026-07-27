using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

// Cover Mode's "Group by tags" prototype: same composite-tag nesting as the Edit Mode tree
// (MainWindow.TagTree.cs), rendered as nested collapsible framed sections instead of tree rows.
//
// Built from cachedVisible rather than reusing the tree's cachedTagRoot, because the two views have
// different visibility rules - the tree still shows hidden designs, the gallery grid does not (see
// CollectVisibleDesigns vs CollectAllLeaves). Walking cachedVisible also means each node's design list
// inherits the gallery's sort order (and favourites-pin ordering) for free.
public partial class MainWindow
{
    private TagNode cachedCoverTagRoot = new();
    private List<DesignLeaf> cachedCoverUntagged = new();
    private int cachedCoverTagTreeVersion = -1;

    // Per-tag-path open state (DrawCollapsibleSubheader needs a persisted bool, unlike TreeNodeEx's
    // built-in per-ID storage used by the Edit Mode tree). Missing entries default to open.
    private readonly Dictionary<string, bool> coverTagSectionOpen = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildCoverTagTreeCache()
    {
        var tagRoot = new TagNode();
        var untagged = new List<DesignLeaf>();

        foreach (var leaf in cachedVisible)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(leaf.Id, out var cached);
            if (cached == null || cached.Tags.Count == 0)
            {
                untagged.Add(leaf);
                continue;
            }

            foreach (var tag in cached.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var segments = tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segments.Length == 0)
                    segments = new[] { tag };

                var node = tagRoot;
                foreach (var segment in segments)
                {
                    if (!node.Children.TryGetValue(segment, out var child))
                    {
                        child = new TagNode();
                        node.Children[segment] = child;
                    }
                    node = child;
                }

                // Two raw tags can normalise to the same path (e.g. "a/b" and "a / b") - don't list the design twice.
                if (!node.Designs.Contains(leaf))
                    node.Designs.Add(leaf);
            }
        }

        cachedCoverTagRoot = tagRoot;
        cachedCoverUntagged = untagged;
    }

    private void DrawCoverGroupedByTags()
    {
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        if (cachedCoverTagTreeVersion != galleryCacheVersion)
        {
            RebuildCoverTagTreeCache();
            cachedCoverTagTreeVersion = galleryCacheVersion;
        }

        foreach (var (name, child) in cachedCoverTagRoot.Children)
            DrawCoverTagNode(name, name, child);

        if (cachedCoverUntagged.Count > 0)
        {
            ImGui.Separator();
            if (DrawCoverTagSectionHeader($"Untagged ({cachedCoverUntagged.Count})", "##untagged"))
            {
                ImGui.Spacing();
                var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
                DrawCoverGridRange(cachedCoverUntagged, 0, cachedCoverUntagged.Count, columns, thumbWidth, thumbHeight);
                ImGui.Spacing();
            }
        }
    }

    private void DrawCoverTagNode(string name, string path, TagNode node)
    {
        ImGui.Separator();
        var label = $"{name} ({CountNodeDesigns(node)})";
        if (!DrawCoverTagSectionHeader(label, path))
            return;

        ImGui.Indent();
        ImGui.Spacing();

        foreach (var (childName, child) in node.Children)
            DrawCoverTagNode(childName, $"{path}/{childName}", child);

        if (node.Designs.Count > 0)
        {
            var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
            DrawCoverGridRange(node.Designs, 0, node.Designs.Count, columns, thumbWidth, thumbHeight);
            ImGui.Spacing();
        }

        ImGui.Unindent();
    }

    // Total designs anywhere in this branch (own + every descendant's), so a collapsed parent still
    // conveys how much is inside it - a bare "(0)" on a branch whose designs all sit in a child would
    // be misleading.
    private static int CountNodeDesigns(TagNode node)
    {
        var count = node.Designs.Count;
        foreach (var child in node.Children.Values)
            count += CountNodeDesigns(child);
        return count;
    }

    // DrawCollapsibleSubheader draws its label as raw text (no ImGui "##" parsing) and derives its
    // internal widget ID from that same label, so same-named nodes under different parents (e.g. a
    // "casual" tag nested under both "summer" and "winter") would otherwise collide. Pushing the path
    // onto the ID stack keeps them distinct without baking "##..." into the visible label.
    private bool DrawCoverTagSectionHeader(string label, string key)
    {
        if (!coverTagSectionOpen.TryGetValue(key, out var open))
            open = true;
        using var id = ImRaii.PushId(key);
        var result = Pills.DrawCollapsibleSubheader(label, ref open);
        coverTagSectionOpen[key] = open;
        return result;
    }
}
