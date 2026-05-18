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
    private string tagSearchText = string.Empty;

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

        DrawSelectedTagPills();

        var tagsLabel = filterTags.Count == 0 ? "Filter by tag(s)..." : "Add tag...";
        if (ImGui.Button(tagsLabel, new Vector2(-1, 0)))
        {
            RebuildAvailableFilterTags();
            tagSearchText = string.Empty;
            ImGui.OpenPopup(FilterTagsPopupId);
        }

        ImGui.TextDisabled("Cover Image:");
        ImGui.SameLine();
        ImGui.PushItemWidth(-1);
        var imageIdx = (int)filterImage;
        var imageOptions = new[] { "All", "Has a cover image", "Missing cover image" };
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

    private void DrawSelectedTagPills()
    {
        if (filterTags.Count == 0) return;

        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var framePadX = style.FramePadding.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;

        bool first = true;
        string? toRemove = null;

        foreach (var tag in filterTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var label = $"{tag} ×";
            var btnWidth = ImGui.CalcTextSize(label).X + framePadX * 2;

            if (first)
            {
                lineRight = cursorStart + btnWidth;
                first = false;
            }
            else if (lineRight + spacing + btnWidth <= availRight)
            {
                ImGui.SameLine();
                lineRight += spacing + btnWidth;
            }
            else
            {
                lineRight = cursorStart + btnWidth;
            }

            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f * ImGuiHelpers.GlobalScale);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.38f, 0.60f, 0.72f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.20f, 0.20f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.65f, 0.14f, 0.14f, 1.00f));
            if (ImGui.Button($"{label}##{tag}"))
                toRemove = tag;
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Remove \"{tag}\"");
        }

        if (toRemove != null)
            filterTags.Remove(toRemove);
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

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##tagSearch", "Search tags...", ref tagSearchText, 64);
        ImGui.Separator();

        var unselected = availableTagsForFilter
            .Where(t => !filterTags.Contains(t) &&
                        (tagSearchText.Length == 0 ||
                         t.Contains(tagSearchText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unselected.Count == 0)
        {
            ImGui.TextDisabled(tagSearchText.Length > 0 ? "No matching tags." : "All tags are selected.");
        }
        else
        {
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            var listHeight = Math.Min(unselected.Count, 8) * rowHeight;
            using var scroll = ImRaii.Child("TagList", new Vector2(220 * ImGuiHelpers.GlobalScale, listHeight), false);
            if (scroll.Success)
            {
                foreach (var tag in unselected)
                {
                    if (ImGui.Selectable(tag))
                    {
                        filterTags.Add(tag);
                        tagSearchText = string.Empty;
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 0)))
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
