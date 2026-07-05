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
    // Vanilla = no mods attached, Modded = has mods. Only one can be on at a time (see DrawVanillaToggle/DrawModdedToggle).
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

    private int ActiveFilterCount => (filterName.Length > 0 ? 1 : 0)
                                   + filterTags.Count
                                   + filterJobs.Count
                                   + (filterImage != ImageFilterMode.All ? 1 : 0)
                                   + (filterFavourites ? 1 : 0)
                                   + (filterVanillaOnly ? 1 : 0)
                                   + (filterModdedOnly ? 1 : 0);

    // A cheap stamp that changes whenever any filter input does. The design-view tree uses it to re-expand
    // matches only when the filter actually changes, instead of forcing everything open every frame.
    private string FilterSignature => string.Join('|',
        filterName,
        searchDesignName, searchModName, searchEquipmentName,
        (int)filterImage, filterFavourites, filterVanillaOnly, filterModdedOnly,
        string.Join(',', filterTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)),
        string.Join(',', filterJobs.OrderBy(j => j)));

    private void DrawFilterUi(bool defaultOpen = false, bool wide = false)
    {
        // AllowOverlap lets the count badge and Clear button sit on the header row itself.
        var flags = (defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)
                  | ImGuiTreeNodeFlags.AllowItemOverlap;
        var open = ImGui.CollapsingHeader("Filters", flags);
        DrawFilterHeaderOverlay();
        if (!open)
            return;

        ImGui.Spacing();
        DrawFilterControls(wide);
        DrawFilterTagsPopup();
    }

    // "N active" plus a ghost Clear button, right-aligned on the collapsing header's own row so
    // filters can be seen and cleared even while the section is collapsed.
    private void DrawFilterHeaderOverlay()
    {
        if (!HasAnyFilter)
            return;

        var style = ImGui.GetStyle();
        var count = ActiveFilterCount;
        var countText = count == 1 ? "1 active" : $"{count} active";
        const string clearLabel = "Clear";
        var clearW = ImGui.CalcTextSize(clearLabel).X + (style.FramePadding.X * 2);
        var countW = ImGui.CalcTextSize(countText).X;

        ImGui.SameLine(ImGui.GetContentRegionMax().X - clearW - countW - style.ItemSpacing.X - style.FramePadding.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(UiTheme.GoldAccent, countText);
        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.GhostButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.GhostButtonActive);
        var clear = ImGui.Button($"{clearLabel}##clearFilters");
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear all filters");
        if (clear)
            ClearAllFilters();
    }

    private void ClearAllFilters()
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

    // Narrow (design tree pane): one control per row. Wide (gallery): two rows -
    // name + scopes + tag/job picker, then cover image + quick toggles.
    private void DrawFilterControls(bool wide)
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var inner = style.ItemInnerSpacing.X;
        var scopeClusterW = (3 * ImGui.GetFrameHeight()) + (2 * inner);
        var tagBtnW = 220f * scale;

        var nameW = wide
            ? ImGui.GetContentRegionAvail().X - scopeClusterW - inner - style.ItemSpacing.X - tagBtnW
            : ImGui.GetContentRegionAvail().X - scopeClusterW - inner;
        ImGui.SetNextItemWidth(Math.Max(120f * scale, nameW));
        ImGui.InputTextWithHint("##nameFilter", "Filter by name...", ref filterName, 64);

        ImGui.SameLine(0, inner);
        DrawSearchScopeToggle("D", "Search design names", ref searchDesignName);
        ImGui.SameLine(0, inner);
        DrawSearchScopeToggle("M", "Search mod names", ref searchModName);
        ImGui.SameLine(0, inner);
        DrawSearchScopeToggle("E", "Search equipment names", ref searchEquipmentName);

        if (wide)
            ImGui.SameLine();
        if (DrawTagJobPickerButton(wide ? tagBtnW : -1f))
        {
            RebuildAvailableFilterTags();
            tagSearchText = string.Empty;
            ImGui.OpenPopup(FilterTagsPopupId);
        }

        DrawSelectedTagPills();
        DrawSelectedJobPills();

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Cover Image:");
        ImGui.SameLine(0, inner);
        ImGui.SetNextItemWidth(wide ? 170f * scale : -1f);
        var imageIdx = (int)filterImage;
        var imageOptions = new[] { "All", "Has a cover image", "Missing cover image" };
        if (ImGui.Combo("##imgFilter", ref imageIdx, imageOptions, imageOptions.Length))
            filterImage = (ImageFilterMode)imageIdx;

        if (wide)
            ImGui.SameLine();
        DrawQuickToggles(wide);
    }

    private const string FavouritesToggleLabel = "★ Favourites";
    private const string VanillaToggleLabel = "Vanilla";
    private const string ModdedToggleLabel = "Modded";

    // Favourites / Vanilla / Modded as pill toggles, matching the D/M/E scope style. In the narrow
    // pane they wrap like the tag pills instead of overflowing.
    private void DrawQuickToggles(bool wide)
    {
        if (!wide)
        {
            var style = ImGui.GetStyle();
            var spacing = style.ItemSpacing.X;
            var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
            var cursorStart = ImGui.GetCursorScreenPos().X;
            var lineRight = cursorStart;
            var first = true;

            float PillWidth(string label) => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2);

            Pills.PlaceItem(PillWidth(FavouritesToggleLabel), ref first, ref lineRight, cursorStart, spacing, availRight);
            DrawFavouritesToggle();
            Pills.PlaceItem(PillWidth(VanillaToggleLabel), ref first, ref lineRight, cursorStart, spacing, availRight);
            DrawVanillaToggle();
            Pills.PlaceItem(PillWidth(ModdedToggleLabel), ref first, ref lineRight, cursorStart, spacing, availRight);
            DrawModdedToggle();
            return;
        }

        DrawFavouritesToggle();
        ImGui.SameLine();
        DrawVanillaToggle();
        ImGui.SameLine();
        DrawModdedToggle();
    }

    private void DrawFavouritesToggle()
    {
        if (Pills.DrawToggle(FavouritesToggleLabel, "favFilter", filterFavourites))
            filterFavourites = !filterFavourites;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only favourite designs");
    }

    // Vanilla and Modded are mutually exclusive - turning one on turns the other off (both can be off).
    private void DrawVanillaToggle()
    {
        if (Pills.DrawToggle(VanillaToggleLabel, "vanillaFilter", filterVanillaOnly))
        {
            filterVanillaOnly = !filterVanillaOnly;
            if (filterVanillaOnly)
                filterModdedOnly = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only designs with no mod associations");
    }

    private void DrawModdedToggle()
    {
        if (Pills.DrawToggle(ModdedToggleLabel, "moddedFilter", filterModdedOnly))
        {
            filterModdedOnly = !filterModdedOnly;
            if (filterModdedOnly)
                filterVanillaOnly = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only designs with mod associations");
    }

    // Styled like a combo (frame background, left-aligned hint text, dropdown arrow) so it reads
    // as an input instead of a stray centered label. Returns true when clicked.
    private bool DrawTagJobPickerButton(float width)
    {
        var hasAny = filterTags.Count > 0 || filterJobs.Count > 0;
        var label = hasAny ? "Add tag or job..." : "Filter by tag(s) or job...";

        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.FrameBg));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.FrameBgHovered));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(ImGuiCol.FrameBgActive));
        ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.PlaceholderText);
        var clicked = ImGui.Button($"{label}##tagJobPicker", new Vector2(width, 0));
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var sz = ImGui.GetFontSize() * 0.35f;
        var center = new Vector2(max.X - ImGui.GetStyle().FramePadding.X - sz, (min.Y + max.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddTriangleFilled(
            center + new Vector2(-sz, -sz * 0.5f),
            center + new Vector2(sz, -sz * 0.5f),
            center + new Vector2(0f, sz * 0.75f),
            ImGui.ColorConvertFloat4ToU32(UiTheme.PlaceholderText));
        return clicked;
    }

    // Compact letter toggle (D/M/E) standing in for a long checkbox label; the full meaning lives in the tooltip.
    private static void DrawSearchScopeToggle(string letter, string tooltip, ref bool enabled)
    {
        var size = ImGui.GetFrameHeight();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding);
        ImGui.PushStyleColor(ImGuiCol.Button,
            enabled ? UiTheme.PillBase : UiTheme.ToggleOffBg);
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
            // Hidden designs are kept out of the gallery and exports entirely (the design tree still shows them).
            if (plugin.Configuration.HiddenDesigns.Contains(design.Id))
                continue;
            plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var cached);
            if (DesignMatchesFilters(design, cached))
                result.Add(design);
        }
        foreach (var folder in node.Folders.Values)
            CollectVisibleDesigns(folder, result);
    }

    // The ids of every design that currently passes the active filters - used to export only the visible list.
    private HashSet<Guid> CollectVisibleDesignIds()
    {
        var visible = new List<DesignLeaf>();
        CollectVisibleDesigns(root, visible);
        return visible.Select(d => d.Id).ToHashSet();
    }
}
