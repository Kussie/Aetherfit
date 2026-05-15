using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private enum ImageFilterMode { All, HasImage, NoImage }

    private const string FilterTagsPopupId = "FilterTagsPopup";

    private string filterName = string.Empty;
    private readonly HashSet<string> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private ImageFilterMode filterImage = ImageFilterMode.All;
    private List<string> availableTagsForFilter = new();

    private bool HasAnyFilter => filterName.Length > 0
                              || filterTags.Count > 0
                              || filterImage != ImageFilterMode.All;

    private void DrawFilterUi(bool defaultOpen = false)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader("Filters", flags))
            return;

        DrawFilterControls();
        DrawFilterTagsPopup();
    }

    private void DrawFilterControls()
    {
        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##nameFilter", "Filter by name...", ref filterName, 64);
        ImGui.PopItemWidth();

        var tagsLabel = filterTags.Count == 0
            ? "Filter by tags..."
            : $"Tags: {filterTags.Count} selected";
        if (ImGui.Button(tagsLabel, new Vector2(-1, 0)))
        {
            RebuildAvailableFilterTags();
            ImGui.OpenPopup(FilterTagsPopupId);
        }

        ImGui.TextDisabled("Cover Image:");
        ImGui.SameLine();
        ImGui.PushItemWidth(-1);
        var imageIdx = (int)filterImage;
        var imageOptions = new[] { "All", "With image", "Without image" };
        if (ImGui.Combo("##imgFilter", ref imageIdx, imageOptions, imageOptions.Length))
            filterImage = (ImageFilterMode)imageIdx;
        ImGui.PopItemWidth();

        using (ImRaii.Disabled(!HasAnyFilter))
        {
            if (ImGui.SmallButton("Clear filters"))
            {
                filterName = string.Empty;
                filterTags.Clear();
                filterImage = ImageFilterMode.All;
            }
        }
    }

    private void RebuildAvailableFilterTags()
    {
        availableTagsForFilter = plugin.Configuration.CachedOutfits.Values
            .SelectMany(o => o.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawFilterTagsPopup()
    {
        using var popup = ImRaii.Popup(FilterTagsPopupId);
        if (!popup.Success)
            return;

        if (availableTagsForFilter.Count == 0)
        {
            ImGui.Text("No tags available.");
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.Text("Show designs matching all of:");
        ImGui.Separator();

        var size = new Vector2(220 * ImGuiHelpers.GlobalScale, 200 * ImGuiHelpers.GlobalScale);
        using (var scroll = ImRaii.Child("FilterTagsScroll", size, true))
        {
            if (scroll.Success)
            {
                foreach (var tag in availableTagsForFilter)
                {
                    var sel = filterTags.Contains(tag);
                    if (ImGui.Checkbox(tag, ref sel))
                    {
                        if (sel) filterTags.Add(tag);
                        else filterTags.Remove(tag);
                    }
                }
            }
        }

        if (ImGui.Button("Clear"))
            filterTags.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Done"))
            ImGui.CloseCurrentPopup();
    }

    private bool DesignMatchesFilters(DesignLeaf design, CachedOutfit? cached)
    {
        if (filterName.Length > 0
            && design.DisplayName.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        if (filterTags.Count > 0)
        {
            if (cached == null || cached.Tags.Count == 0) return false;
            if (!filterTags.All(t => cached.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        }

        if (filterImage != ImageFilterMode.All)
        {
            var hasImage = plugin.ImageStorage.HasCover(design.Id);
            if (filterImage == ImageFilterMode.HasImage && !hasImage) return false;
            if (filterImage == ImageFilterMode.NoImage && hasImage) return false;
        }

        return true;
    }

    private bool FolderHasMatch(FolderNode node)
    {
        foreach (var d in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(d.Id, out var c);
            if (DesignMatchesFilters(d, c)) return true;
        }
        foreach (var f in node.Folders.Values)
            if (FolderHasMatch(f)) return true;
        return false;
    }

    private void CollectVisibleDesigns(FolderNode node, List<DesignLeaf> result)
    {
        foreach (var design in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var cached);
            if (DesignMatchesFilters(design, cached))
                result.Add(design);
        }
        foreach (var folder in node.Folders.Values)
            CollectVisibleDesigns(folder, result);
    }
}
