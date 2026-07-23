using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    // Tags containing '/' nest like folders: "tag1/tag2/tag3" shows as tag1 > tag2 > tag3.
    private sealed class TagNode
    {
        public SortedDictionary<string, TagNode> Children { get; } = new(NaturalStringComparer.OrdinalIgnoreCase);
        public List<DesignLeaf> Designs { get; } = new();
    }

    private TagNode cachedTagRoot = new();
    private FolderNode cachedUntaggedFolder = new();
    private int cachedTagTreeGeneration = -1;
    private FilterSnapshot cachedTagTreeFilterSnapshot;
    private Dictionary<string, bool> cachedTagTreeFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, bool> cachedTagTreeFilterJobs = new();

    private bool IsTagTreeCacheStale() =>
        cachedTagTreeGeneration != designListGeneration ||
        cachedTagTreeFilterSnapshot != CaptureFilterSnapshot() ||
        !FiltersEqual(cachedTagTreeFilterTags, filterTags) ||
        !FiltersEqual(cachedTagTreeFilterJobs, filterJobs);

    private void RebuildTagTreeCache()
    {
        var allLeaves = new List<DesignLeaf>();
        CollectAllLeaves(root, allLeaves);

        // A design appears under every tag it carries, so it can show up multiple times.
        var tagRoot = new TagNode();
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

        SortTagNodeDesigns(tagRoot);

        cachedTagRoot = tagRoot;
        cachedUntaggedFolder = BuildFolderTree(untagged);

        cachedTagTreeGeneration = designListGeneration;
        cachedTagTreeFilterSnapshot = CaptureFilterSnapshot();
        cachedTagTreeFilterTags = new(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedTagTreeFilterJobs = new(filterJobs);
    }

    private void DrawTagTree(bool hasFilter)
    {
        if (IsTagTreeCacheStale())
            RebuildTagTreeCache();

        foreach (var (name, child) in cachedTagRoot.Children)
            DrawTagNode(name, name, child, hasFilter);

        if ((cachedUntaggedFolder.Designs.Count > 0 || cachedUntaggedFolder.Folders.Count > 0)
            && DrawJobGroupHeader("Untagged##untaggedDesigns", hasFilter))
        {
            DrawTree(cachedUntaggedFolder, hasFilter);
            ImGui.TreePop();
        }
    }

    private static void SortTagNodeDesigns(TagNode node)
    {
        node.Designs.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
        foreach (var child in node.Children.Values)
            SortTagNodeDesigns(child);
    }

    private void DrawTagNode(string name, string path, TagNode node, bool hasFilter)
    {
        // The full path in the id keeps same-named subtags apart (e.g. summer/casual vs winter/casual).
        if (!DrawJobGroupHeader($"{name}##tag{path}", hasFilter))
            return;

        foreach (var (childName, child) in node.Children)
            DrawTagNode(childName, $"{path}/{childName}", child, hasFilter);

        foreach (var leaf in node.Designs)
            DrawDesignLeaf(leaf);

        ImGui.TreePop();
    }
}
