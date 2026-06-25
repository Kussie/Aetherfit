using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private enum ImageFilterMode { All, HasImage, NoImage }

    private const string FilterTagsPopupId = "FilterTagsPopup";

    private string filterName = string.Empty;
    private bool searchDesignName = true;   // preserves current default behaviour
    private bool searchModName;
    private bool searchEquipmentName;
    private readonly HashSet<string> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> filterJobs = new();
    private ImageFilterMode filterImage = ImageFilterMode.All;
    private bool filterFavourites;
    // Vanilla = no mods attached, Modded = has mods. Only one can be on at a time (see DrawFilterControls).
    private bool filterVanillaOnly;
    private bool filterModdedOnly;
    private List<string> availableTagsForFilter = new();
    private string tagSearchText = string.Empty;

    private bool HasAnyFilter => filterName.Length > 0
                              || filterTags.Count > 0
                              || filterJobs.Count > 0
                              || filterImage != ImageFilterMode.All
                              || filterFavourites
                              || filterVanillaOnly
                              || filterModdedOnly;

    // A cheap stamp that changes whenever any filter input does. The design-view tree uses it to re-expand
    // matches only when the filter actually changes, instead of forcing everything open every frame.
    private string FilterSignature => string.Join('|',
        filterName,
        searchDesignName, searchModName, searchEquipmentName,
        (int)filterImage, filterFavourites, filterVanillaOnly, filterModdedOnly,
        string.Join(',', filterTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)),
        string.Join(',', filterJobs.OrderBy(j => j)));

    private void DrawFilterUi(bool defaultOpen = false, bool inlineModFilters = false)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader("Filters", flags))
            return;

        DrawFilterControls(inlineModFilters);
        DrawFilterTagsPopup();
    }

    private void DrawFilterControls(bool inlineModFilters)
    {
        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##nameFilter", "Filter by name...", ref filterName, 64);
        ImGui.PopItemWidth();

        ImGui.TextDisabled("Search In:");
        ImGui.SameLine();
        DrawSearchScopeToggle("D", "Search design name", ref searchDesignName);
        ImGui.SameLine();
        DrawSearchScopeToggle("M", "Search mod names", ref searchModName);
        ImGui.SameLine();
        DrawSearchScopeToggle("E", "Search equipment names", ref searchEquipmentName);

        DrawSelectedTagPills();
        DrawSelectedJobPills();

        var hasTagOrJob = filterTags.Count > 0 || filterJobs.Count > 0;
        var tagsLabel = hasTagOrJob ? "Add tag or job..." : "Filter by tag(s) or job...";
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

        ImGui.Checkbox("Show favourites only", ref filterFavourites);

        // Vanilla and Modded are mutually exclusive - ticking one unticks the other (both can be off).
        // Side by side in the gallery's wider filter pane, stacked in the narrower design view.
        if (inlineModFilters)
            ImGui.SameLine();
        if (ImGui.Checkbox("Vanilla only", ref filterVanillaOnly) && filterVanillaOnly)
            filterModdedOnly = false;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only designs with no mod associations");

        if (inlineModFilters)
            ImGui.SameLine();
        if (ImGui.Checkbox("Modded only", ref filterModdedOnly) && filterModdedOnly)
            filterVanillaOnly = false;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only designs with mod associations");

        using (ImRaii.Disabled(!HasAnyFilter))
        {
            if (ImGui.SmallButton("Clear filters"))
            {
                filterName = string.Empty;
                searchDesignName = true;
                searchModName = false;
                searchEquipmentName = false;
                filterTags.Clear();
                filterJobs.Clear();
                filterImage = ImageFilterMode.All;
                filterFavourites = false;
                filterVanillaOnly = false;
                filterModdedOnly = false;
            }
        }
    }

    // Compact letter toggle (D/M/E) standing in for a long checkbox label; the full meaning lives in the tooltip.
    private static void DrawSearchScopeToggle(string letter, string tooltip, ref bool enabled)
    {
        var size = ImGui.GetFrameHeight();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding);
        ImGui.PushStyleColor(ImGuiCol.Button,
            enabled ? UiTheme.PillBase : new Vector4(0.20f, 0.20f, 0.22f, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.Text,
            enabled ? UiTheme.GoldAccent : UiTheme.PlaceholderText);
        if (ImGui.Button($"{letter}##scope{letter}", new Vector2(size, size)))
            enabled = !enabled;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawSelectedTagPills()
    {
        if (filterTags.Count == 0) return;

        Pills.DrawRemovableRow(
            filterTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
            tag => tag,
            tag => filterTags.Remove(tag));
    }

    private void DrawSelectedJobPills()
    {
        if (filterJobs.Count == 0) return;

        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;
        var first = true;

        uint? toRemove = null;
        foreach (var job in filterJobs.OrderBy(j => j))
        {
            var width = MeasureJobPill(job);
            Pills.PlaceItem(width, ref first, ref lineRight, cursorStart, spacing, availRight);
            if (DrawJobPill(job))
                toRemove = job;
        }

        if (toRemove is { } remove)
            filterJobs.Remove(remove);
    }

    private void RebuildAvailableFilterTags()
    {
        availableTagsForFilter = plugin.Configuration.DistinctSortedTags();
    }

    private void DrawFilterTagsPopup()
    {
        using var popup = ImRaii.Popup(FilterTagsPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##tagSearch", "Search tags or jobs...", ref tagSearchText, 64);
        ImGui.Separator();

        var unselectedTags = availableTagsForFilter
            .Where(t => !filterTags.Contains(t) &&
                        (tagSearchText.Length == 0 ||
                         t.Contains(tagSearchText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var unselectedJobs = plugin.GameData.GetSelectableJobs()
            .Where(j => !filterJobs.Contains(j.RowId) &&
                        (tagSearchText.Length == 0 ||
                         j.Name.Contains(tagSearchText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unselectedTags.Count == 0 && unselectedJobs.Count == 0)
        {
            ImGui.TextDisabled(tagSearchText.Length > 0 ? "No matching tags or jobs." : "All tags and jobs are selected.");
        }
        else
        {
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            // Account for the role headings interleaved with the job rows when sizing the list.
            var jobRows = unselectedJobs.Count + unselectedJobs.Select(j => j.Role).Distinct().Count();
            var totalRows = unselectedTags.Count + jobRows;
            var listHeight = Math.Min(totalRows, 10) * rowHeight;
            using var scroll = ImRaii.Child("TagJobList", new Vector2(240 * ImGuiHelpers.GlobalScale, listHeight), false);
            if (scroll.Success)
            {
                foreach (var tag in unselectedTags)
                {
                    if (ImGui.Selectable(tag))
                    {
                        filterTags.Add(tag);
                        tagSearchText = string.Empty;
                    }
                }

                var lineH = ImGui.GetTextLineHeight();
                JobRole? lastRole = null;
                foreach (var job in unselectedJobs)
                {
                    if (lastRole != job.Role)
                    {
                        ImGui.TextDisabled(GameDataService.RoleLabel(job.Role));
                        lastRole = job.Role;
                    }

                    var icon = plugin.GameData.GetJobIcon(job.RowId);
                    if (icon != null)
                    {
                        ImGui.Image(icon.Handle, new Vector2(lineH, lineH));
                        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    }

                    if (ImGui.Selectable($"{job.Name}##filterjob{job.RowId}"))
                    {
                        filterJobs.Add(job.RowId);
                        tagSearchText = string.Empty;
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 0)))
            ImGui.CloseCurrentPopup();
    }

    private bool NameFilterMatches(DesignLeaf design, CachedOutfit? cached)
    {
        // No scope enabled => the name filter is inert rather than matching nothing.
        if (!searchDesignName && !searchModName && !searchEquipmentName)
            return true;

        if (searchDesignName
            && design.DisplayName.Contains(filterName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (searchModName && cached != null
            && cached.Mods.Any(m => m.Name.Contains(filterName, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (searchEquipmentName && cached != null)
        {
            if (cached.Equipment.Any(s =>
                    plugin.GameData.ResolveItemName(s.ItemId)
                        .Contains(filterName, StringComparison.OrdinalIgnoreCase)))
                return true;

            if (cached.BonusItems.Any(b =>
                    plugin.GameData.ResolveBonusItemName(b.Slot, b.ItemId)
                        .Contains(filterName, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private bool DesignMatchesFilters(DesignLeaf design, CachedOutfit? cached)
    {
        if (filterName.Length > 0 && !NameFilterMatches(design, cached))
            return false;

        if (filterTags.Count > 0)
        {
            if (cached == null || cached.Tags.Count == 0) return false;
            if (!filterTags.All(t => cached.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        }

        if (filterJobs.Count > 0)
        {
            var jobs = plugin.Configuration.GetJobAssociations(design.Id);
            if (!filterJobs.Any(jobs.Contains)) return false;
        }

        if (filterImage != ImageFilterMode.All)
        {
            var hasImage = plugin.ImageStorage.HasCover(design.Id);
            if (filterImage == ImageFilterMode.HasImage && !hasImage) return false;
            if (filterImage == ImageFilterMode.NoImage && hasImage) return false;
        }

        if (filterFavourites && !plugin.Configuration.FavouriteDesigns.Contains(design.Id))
            return false;

        if (filterVanillaOnly || filterModdedOnly)
        {
            var hasMods = cached != null && cached.Mods.Count > 0;
            if (filterVanillaOnly && hasMods) return false;
            if (filterModdedOnly && !hasMods) return false;
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
