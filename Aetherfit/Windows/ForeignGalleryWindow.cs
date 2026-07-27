using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Models;
using Aetherfit.Services;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// Look-but-don't-touch viewer for an imported gallery: just images and the basic info. There's no apply, favourite,
// edit, or job button anywhere in here, so someone else's gallery can't be applied or changed by accident.
public sealed partial class ForeignGalleryWindow : Window, IDisposable
{
    private const string AddFilterPopupId = "ForeignAddFilterPopup";
    private const string DetailsPopupId = "ForeignDesignDetails";

    private readonly Plugin plugin;

    private ForeignGallery? gallery;
    private readonly Dictionary<Guid, int> imageIndex = new();

    private ForeignDesign? detailsDesign;
    private bool openDetailsThisFrame;

    private string filterName = string.Empty;
    // true = must have the tag/job/mod, false = must not have it; a key absent from the map is left alone.
    private readonly Dictionary<string, bool> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, bool> filterJobs = new();
    private readonly Dictionary<string, bool> filterMods = new(StringComparer.OrdinalIgnoreCase);
    private string filterSearchText = string.Empty;
    // Vanilla = no mods attached, Modded = has mods. Only one can be on at a time, matching the local
    // gallery's own quick toggles (MainWindow.Filters.cs).
    private bool filterVanillaOnly;
    private bool filterModdedOnly;

    // Mutually exclusive, matching Cover Mode's own grouping toggles (both can be off).
    private bool groupByTags;
    private bool groupByJob;

    // Only sorting by name is meaningful here - unlike the local gallery, a shared bundle carries no
    // last-modified/created timestamps to sort by.
    private bool sortAscending = true;

    public ForeignGalleryWindow(Plugin plugin)
        : base("Aetherfit — Shared Gallery##AetherfitForeignGallery", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Show(ForeignGallery foreign)
    {
        // Throw away the last import's cached images before we bring in the new one.
        if (gallery != null)
            plugin.ImageStorage.ClearForeign(gallery.OriginKey);

        gallery = foreign;
        imageIndex.Clear();
        detailsDesign = null;
        openDetailsThisFrame = false;
        filterName = string.Empty;
        filterTags.Clear();
        filterJobs.Clear();
        filterMods.Clear();
        filterSearchText = string.Empty;
        filterVanillaOnly = false;
        filterModdedOnly = false;
        groupByTags = false;
        groupByJob = false;
        sortAscending = true;
        tagSectionOpen.Clear();
        jobSectionOpen.Clear();
        IsOpen = true;
    }

    public override void OnClose()
    {
        if (gallery != null)
        {
            plugin.ImageStorage.ClearForeign(gallery.OriginKey);
            gallery = null;
        }
        detailsDesign = null;
    }

    public void Dispose()
    {
        if (gallery != null)
            plugin.ImageStorage.ClearForeign(gallery.OriginKey);
    }

    public override void Draw()
    {
        if (gallery == null)
        {
            ImGui.TextDisabled("No shared gallery loaded.");
            return;
        }

        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, gallery.SharerLabel);
        ImGui.SetWindowFontScale(1.0f);
        ImGui.SameLine();
        ImGui.TextDisabled($"({gallery.Designs.Count} design(s), read-only)");
        ImGui.Separator();

        DrawFilters();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var visible = GetVisibleDesigns();
        DrawSortControls(visible.Count);
        ImGui.Spacing();

        using (var gridChild = ImRaii.Child("ForeignGridScroll", Vector2.Zero, false))
        {
            if (gridChild.Success)
            {
                if (visible.Count == 0)
                    ImGui.TextDisabled(gallery.Designs.Count == 0
                        ? "This shared gallery is empty."
                        : "No designs match the current filters.");
                else if (groupByTags)
                    DrawGroupedByTags(visible);
                else if (groupByJob)
                    DrawGroupedByJob(visible);
                else
                    DrawGrid(visible);
            }
        }

        if (openDetailsThisFrame)
        {
            ImGui.OpenPopup(DetailsPopupId);
            openDetailsThisFrame = false;
        }
        DrawDetailsPopup();
    }

    private List<ForeignDesign> GetVisibleDesigns()
    {
        IEnumerable<ForeignDesign> query = gallery!.Designs;

        if (filterName.Length > 0)
            query = query.Where(d => d.Name.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) >= 0);

        if (filterTags.Count > 0)
            query = query.Where(d => filterTags.MatchesFilter(d.Tags));

        if (filterJobs.Count > 0)
            query = query.Where(d => filterJobs.MatchesFilter(d.Jobs));

        if (filterMods.Count > 0)
            query = query.Where(d => filterMods.MatchesFilter<string>(d.Mods.Select(m => m.Name).ToList()));

        if (filterVanillaOnly || filterModdedOnly)
        {
            query = query.Where(d =>
            {
                var hasMods = d.Mods.Count > 0;
                if (filterVanillaOnly && hasMods) return false;
                if (filterModdedOnly && !hasMods) return false;
                return true;
            });
        }

        var ordered = sortAscending
            ? query.OrderBy(d => d.Name, NaturalStringComparer.OrdinalIgnoreCase)
            : query.OrderByDescending(d => d.Name, NaturalStringComparer.OrdinalIgnoreCase);
        return ordered.ToList();
    }

    private bool HasAnyFilter => filterName.Length > 0 || filterTags.Count > 0 || filterJobs.Count > 0 || filterMods.Count > 0
                               || filterVanillaOnly || filterModdedOnly;

    private void DrawFilters()
    {
        using var header = ImRaii.Header("Filters", ImGuiTreeNodeFlags.DefaultOpen);
        if (!header.Success)
            return;

        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##foreignNameFilter", "Filter by name...", ref filterName, 64);
        ImGui.PopItemWidth();

        if (DrawTagJobPickerButton())
        {
            filterSearchText = string.Empty;
            ImGui.OpenPopup(AddFilterPopupId);
        }
        DrawTagJobPopup();

        ImGui.Spacing();
        DrawQuickToggles();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawGroupByControls();

        using (ImRaii.Disabled(!HasAnyFilter))
        {
            if (ImGui.SmallButton("Clear filters"))
            {
                filterName = string.Empty;
                filterTags.Clear();
                filterJobs.Clear();
                filterMods.Clear();
                filterVanillaOnly = false;
                filterModdedOnly = false;
            }
        }
    }

    private void DrawQuickToggles()
    {
        Pills.DrawVanillaToggle(ref filterVanillaOnly, ref filterModdedOnly, "foreign");
        ImGui.SameLine();
        Pills.DrawModdedToggle(ref filterVanillaOnly, ref filterModdedOnly, "foreign");
    }

    // Mutually exclusive, matching Cover Mode's own grouping checkboxes (both can be off).
    private void DrawGroupByControls()
    {
        if (ImGui.Checkbox("Group by tags", ref groupByTags) && groupByTags)
            groupByJob = false;
        ImGui.SameLine();
        if (ImGui.Checkbox("Group by job association", ref groupByJob) && groupByJob)
            groupByTags = false;
    }

    // Mirrors Cover Mode's DrawGallerySortControls, minus the sort-field dropdown (a shared bundle carries
    // no last-modified/created timestamps to offer) and the "pin favourites" toggle (nothing to favourite
    // in a read-only gallery).
    private void DrawSortControls(int visibleCount)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Sort by: Name");

        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        GalleryDraw.DrawSortDirectionToggle(ref sortAscending);

        var countText = HasAnyFilter
            ? $"{visibleCount} of {gallery!.Designs.Count} designs"
            : visibleCount == 1 ? "1 design" : $"{visibleCount} designs";
        var thumbSize = plugin.Configuration.ForeignGalleryThumbTargetWidth;
        GalleryDraw.DrawThumbSizeAndCount("##foreignThumbSize", ref thumbSize, countText, () => plugin.Configuration.Save());
        plugin.Configuration.ForeignGalleryThumbTargetWidth = thumbSize;
    }

    private bool DrawTagJobPickerButton()
    {
        var count = filterTags.Count + filterJobs.Count + filterMods.Count;
        return ImGui.Button(Pills.TagJobFilterLabel(count), new Vector2(-1, 0));
    }

    // Every tag and job in the shared gallery as its own tri-state checkbox row. Tags/jobs stay listed with
    // their current state rather than disappearing once picked (there are no pills once the popup is closed -
    // reopening it is how you check what's currently filtered).
    private void DrawTagJobPopup()
    {
        using var popup = ImRaii.Popup(AddFilterPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##foreignFilterSearch", "Search tags, jobs or mods...", ref filterSearchText, 64);
        ImGui.Separator();

        var availableTags = TagMatching.WithSegments(gallery!.Designs.SelectMany(d => d.Tags))
            .Where(t => filterSearchText.Length == 0 || t.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Only the jobs actually used in this gallery, but grouped/ordered by role like the local
        // gallery's own job filter (see MainWindow.Filters.cs) instead of a flat alphabetical list.
        var usedJobs = new HashSet<uint>(gallery.Designs.SelectMany(d => d.Jobs));
        var availableJobs = plugin.GameData.GetSelectableJobs()
            .Where(j => usedJobs.Contains(j.RowId))
            .Where(j => filterSearchText.Length == 0 || j.Name.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase))
            .Select(j => (j.RowId, j.Name, (JobRole?)j.Role))
            .ToList();

        // Shared bundles only carry each mod's display name (no directory), so that doubles as the filter key.
        var availableMods = gallery.Designs
            .SelectMany(d => d.Mods)
            .Select(m => m.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => filterSearchText.Length == 0 || n.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => (Directory: n, DisplayName: n))
            .ToList();

        var emptyMessage = filterSearchText.Length > 0 ? "No matching tags, jobs or mods." : "Nothing to filter by.";
        Pills.DrawTagJobFilterList(availableTags, availableJobs, availableMods, filterTags, filterJobs, filterMods,
            plugin.GameData.GetJobIcon, "foreign", 260 * ImGuiHelpers.GlobalScale, emptyMessage);
    }

    private void DrawGrid(List<ForeignDesign> visible)
    {
        var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
        DrawGridRange(visible, 0, visible.Count, columns, thumbWidth, thumbHeight);
    }

    private (int Columns, float ThumbWidth, float ThumbHeight) ComputeGridLayout()
        => GalleryDraw.ComputeGridLayout(plugin.Configuration.ForeignGalleryThumbTargetWidth);

    // Used both for the plain grid and for each section's slice when grouped by tag/job.
    // Row-clipped so a freshly-imported gallery (every image a guaranteed cold texture-cache miss)
    // doesn't request a decode for every design at once - only rows scrolled into view get drawn.
    private void DrawGridRange(List<ForeignDesign> designs, int start, int end, int columns, float thumbWidth, float thumbHeight)
    {
        var count = end - start;
        if (count <= 0)
            return;

        var rowHeight = thumbHeight + ImGui.GetStyle().ItemSpacing.Y * 2 + ImGui.GetTextLineHeight();
        var rowCount = (count + columns - 1) / columns;

        var clipper = new ImGuiListClipper();
        clipper.Begin(rowCount, rowHeight);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var i = start + row * columns + col;
                    if (i >= end) break;
                    if (col != 0) ImGui.SameLine();
                    DrawCell(designs[i], thumbWidth, thumbHeight);
                }
            }
        }
        clipper.End();
    }

    // Shared open/closed state for both the tag and job grouping headers - separate dictionaries so a
    // tag path and a job/role key can never collide (mirrors MainWindow's coverTagSectionOpen/coverJobSectionOpen).
    private readonly Dictionary<string, bool> tagSectionOpen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> jobSectionOpen = new(StringComparer.OrdinalIgnoreCase);

    private bool DrawTagSectionHeader(string label, string key) => DrawGroupSectionHeader(tagSectionOpen, label, key);
    private bool DrawJobSectionHeader(string label, string key) => DrawGroupSectionHeader(jobSectionOpen, label, key);

    private static bool DrawGroupSectionHeader(Dictionary<string, bool> state, string label, string key)
    {
        if (!state.TryGetValue(key, out var open))
            open = true;
        using var id = ImRaii.PushId(key);
        var result = Pills.DrawCollapsibleSubheader(label, ref open);
        state[key] = open;
        return result;
    }

    private void DrawCell(ForeignDesign design, float thumbWidth, float thumbHeight)
    {
        using var id = ImRaii.PushId(design.SourceId.ToString());
        using var group = ImRaii.Group();

        var thumbStart = ImGui.GetCursorScreenPos();
        var thumbVec = new Vector2(thumbWidth, thumbHeight);
        var containerAspect = thumbWidth / thumbHeight;

        var images = GalleryDraw.BuildImageList(design.CoverPath, design.AdditionalPaths);

        var imgIdx = GalleryDraw.ResolveImageIndex(imageIndex, design.SourceId, images.Count);
        var currentImage = images.Count > 0 ? images[imgIdx] : null;

        if (currentImage != null)
        {
            var tex = Plugin.TextureProvider.GetFromFile(currentImage).GetWrapOrEmpty();
            if (tex.Width > 0 && tex.Height > 0)
                GalleryDraw.DrawFittedImage(tex, thumbStart, thumbVec, thumbWidth, thumbHeight, containerAspect,
                    plugin.Configuration.GalleryFitMode);
            else
                ImGui.Dummy(thumbVec);
        }
        else
        {
            GalleryDraw.DrawNoImagePlaceholder(thumbStart, thumbVec);
        }

        var imageHovered = ImGui.IsItemHovered();

        var hasArrows = design.AdditionalPaths.Count > 0;
        var canPrev = imgIdx > 0;
        var canNext = imgIdx < images.Count - 1;

        var arrows = GalleryDraw.DrawArrows(thumbStart, thumbWidth, thumbHeight,
            hasArrows, canPrev, canNext, ImGui.GetIO().MousePos, imageHovered);
        var overLeft = arrows.OverLeft;
        var overRight = arrows.OverRight;

        if (imageHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (overLeft) imageIndex[design.SourceId] = imgIdx - 1;
                else if (overRight) imageIndex[design.SourceId] = imgIdx + 1;
                else
                {
                    detailsDesign = design;
                    openDetailsThisFrame = true;
                }
            }
            if (!overLeft && !overRight)
                DrawCellTooltip(design);
        }

        var label = design.Name;
        GalleryDraw.IndentForCenteredLabel(thumbWidth, label);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + thumbWidth);
        ImGui.TextUnformatted(label);
        ImGui.PopTextWrapPos();
    }

    private void DrawCellTooltip(ForeignDesign design)
    {
        var panelWidth = 300f * ImGuiHelpers.GlobalScale;

        ImGui.BeginTooltip();

        using (ImRaii.PushColor(ImGuiCol.Text, UiTheme.GoldAccent))
        {
            ImGui.PushTextWrapPos(panelWidth);
            ImGui.TextUnformatted(design.Name);
            ImGui.PopTextWrapPos();
        }

        var hasDetails = !string.IsNullOrWhiteSpace(design.Description)
                         || design.Tags.Count > 0
                         || design.Jobs.Count > 0;

        if (!string.IsNullOrWhiteSpace(design.Description))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Description");
            ImGui.PushTextWrapPos(panelWidth);
            ImGui.TextUnformatted(design.Description);
            ImGui.PopTextWrapPos();
        }

        if (design.Tags.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Tags");
            DrawTagPills(design.Tags, panelWidth);
        }

        if (design.Jobs.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Job Associations");
            DrawJobAssociations(design.Jobs, panelWidth);
        }

        if (!hasDetails)
            ImGui.TextDisabled("No additional details.");

        ImGui.Spacing();
        ImGui.TextDisabled("Click to view equipment & mods");

        ImGui.EndTooltip();
    }

    // Tags as plain coloured chips (no clicking), wrapping inside maxWidth.
    private static void DrawTagPills(IReadOnlyList<string> tags, float maxWidth)
    {
        var dl = ImGui.GetWindowDrawList();
        var padX = 6f * ImGuiHelpers.GlobalScale;
        var padY = 2f * ImGuiHelpers.GlobalScale;
        var spacing = 4f * ImGuiHelpers.GlobalScale;
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        var lineH = ImGui.GetTextLineHeight();
        var pillH = lineH + padY * 2;

        var origin = ImGui.GetCursorScreenPos();
        var x = origin.X;
        var y = origin.Y;
        var pillColor = ImGui.ColorConvertFloat4ToU32(UiTheme.PillBase);
        var textColor = ImGui.ColorConvertFloat4ToU32(Vector4.One);

        foreach (var tag in tags)
        {
            var textSize = ImGui.CalcTextSize(tag);
            var pillW = textSize.X + padX * 2;
            if (x + pillW > origin.X + maxWidth && x > origin.X)
            {
                x = origin.X;
                y += pillH + spacing;
            }
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + pillW, y + pillH), pillColor, rounding);
            dl.AddText(new Vector2(x + padX, y + padY), textColor, tag);
            x += pillW + spacing;
        }

        ImGui.Dummy(new Vector2(maxWidth, (y - origin.Y) + pillH));
    }

    // Job icons with their names beside them, wrapping inside maxWidth.
    private void DrawJobAssociations(IReadOnlyList<uint> jobs, float maxWidth)
    {
        var lineH = ImGui.GetTextLineHeight();
        var iconSize = new Vector2(lineH, lineH);
        var iconGap = ImGui.GetStyle().ItemInnerSpacing.X;
        var itemGap = 10f * ImGuiHelpers.GlobalScale;

        var first = true;
        var lineWidth = 0f;
        foreach (var job in jobs)
        {
            var name = plugin.GameData.ResolveJobName(job);
            var icon = plugin.GameData.GetJobIcon(job);
            var itemW = iconSize.X + iconGap + ImGui.CalcTextSize(name).X;

            if (first)
                lineWidth = itemW;
            else if (lineWidth + itemGap + itemW <= maxWidth)
            {
                ImGui.SameLine(0, itemGap);
                lineWidth += itemGap + itemW;
            }
            else
                lineWidth = itemW;
            first = false;

            if (icon != null)
            {
                ImGui.Image(icon.Handle, iconSize);
                ImGui.SameLine(0, iconGap);
            }
            ImGui.TextUnformatted(name);
        }
    }

    // Read-only equipment + mod-association panel for the clicked design, mirroring the local detail view.
    // All the make-up was baked into the bundle, so nothing here needs Glamourer or Penumbra.
    private void DrawDetailsPopup()
    {
        if (detailsDesign is { } pending)
        {
            var viewport = ImGui.GetMainViewport();
            var width = ComputeDetailsWidth(pending, viewport);
            var height = Math.Min(480f * ImGuiHelpers.GlobalScale, viewport.WorkSize.Y * 0.85f);
            ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Appearing);
        }

        using var popup = ImRaii.Popup(DetailsPopupId);
        if (!popup.Success || detailsDesign is not { } d)
            return;

        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        DesignDetailView.TextColoredUnformatted(UiTheme.GoldAccent, d.Name);
        ImGui.SetWindowFontScale(1.0f);

        if (!string.IsNullOrWhiteSpace(d.Description))
        {
            ImGui.PushTextWrapPos(0);
            DesignDetailView.TextColoredUnformatted(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], d.Description);
            ImGui.PopTextWrapPos();
        }
        ImGui.Separator();

        using var scroll = ImRaii.Child("ForeignDetailsScroll", Vector2.Zero, false);
        if (!scroll.Success)
            return;

        DrawForeignEquipment(d);
        DrawForeignMods(d);
    }

    // Sizes the popup to its widest row (equipment value + dyes + "affected by" suffix, or a mod name, or
    // the title), clamped to a minimum and to most of the screen so it never runs off the viewport.
    private float ComputeDetailsWidth(ForeignDesign d, ImGuiViewportPtr viewport)
    {
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;
        var lineH = ImGui.GetTextLineHeight();
        var stainW = style.ItemSpacing.X + lineH;        // one dye swatch plus its leading SameLine spacing
        var notInDesign = ImGui.CalcTextSize("(not in design)").X;

        var labelWidth = 0f;
        foreach (var (_, label) in DesignDetailView.SlotDisplay)
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(label).X);
        foreach (var (_, label) in DesignDetailView.BonusSlotDisplay)
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(label).X);
        labelWidth += 16f * scale;

        var bySlot = new Dictionary<EquipmentSlot, SharedEquipment>();
        foreach (var e in d.Equipment)
            bySlot[e.Slot] = e;
        var byBonus = new Dictionary<string, SharedBonusItem>(StringComparer.Ordinal);
        foreach (var b in d.BonusItems)
            byBonus[b.Slot] = b;

        // Title is drawn at the header font scale and is not indented.
        var content = ImGui.CalcTextSize(d.Name).X * UiTheme.HeaderFontScale;

        foreach (var (slot, _) in DesignDetailView.SlotDisplay)
        {
            var w = labelWidth;
            if (bySlot.TryGetValue(slot, out var entry))
            {
                var name = plugin.GameData.ResolveItemName(entry.ItemId);
                w += ImGui.CalcTextSize(name).X;
                if (entry.Stain != 0) w += stainW;
                if (entry.Stain2 != 0) w += stainW;
                w += AffectedSuffixWidth(entry.Apply, name, d.AffectedItems, style.ItemSpacing.X);
            }
            else
            {
                w += notInDesign;
            }
            content = Math.Max(content, style.IndentSpacing + w);
        }

        foreach (var (slotKey, _) in DesignDetailView.BonusSlotDisplay)
        {
            var w = labelWidth;
            if (byBonus.TryGetValue(slotKey, out var entry))
            {
                var name = plugin.GameData.ResolveBonusItemName(entry.Slot, entry.ItemId);
                w += ImGui.CalcTextSize(name).X;
                w += AffectedSuffixWidth(entry.Apply, name, d.AffectedItems, style.ItemSpacing.X);
            }
            else
            {
                w += notInDesign;
            }
            content = Math.Max(content, style.IndentSpacing + w);
        }

        foreach (var mod in d.Mods)
        {
            var name = string.IsNullOrWhiteSpace(mod.Name) ? "(unnamed mod)" : mod.Name;
            var w = style.IndentSpacing + lineH + style.ItemInnerSpacing.X + ImGui.CalcTextSize(name).X;
            content = Math.Max(content, w);
        }

        var desired = content + (style.WindowPadding.X * 2) + style.ScrollbarSize + (8f * scale);
        var min = 360f * scale;
        var max = Math.Min(viewport.WorkSize.X * 0.9f, 1100f * scale);
        return Math.Clamp(desired, min, max);
    }

    private static float AffectedSuffixWidth(bool applied, string itemName,
        IReadOnlyDictionary<string, string> affected, float itemSpacingX)
    {
        if (!applied || itemName == GameDataService.NothingItemName)
            return 0f;
        if (!affected.TryGetValue(itemName, out var modName))
            return 0f;

        return itemSpacingX
               + ImGui.CalcTextSize("(Appearance affected by ").X
               + ImGui.CalcTextSize(modName).X
               + ImGui.CalcTextSize(")").X;
    }

    private void DrawForeignEquipment(ForeignDesign d)
    {
        ImGui.TextColored(UiTheme.SectionHeader, "Equipment");
        ImGui.Spacing();
        ImGui.Indent();

        var bySlot = new Dictionary<EquipmentSlot, SharedEquipment>();
        foreach (var e in d.Equipment)
            bySlot[e.Slot] = e;

        var byBonus = new Dictionary<string, SharedBonusItem>(StringComparer.Ordinal);
        foreach (var b in d.BonusItems)
            byBonus[b.Slot] = b;

        var labelWidth = 0f;
        foreach (var (_, label) in DesignDetailView.SlotDisplay)
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(label).X);
        foreach (var (_, label) in DesignDetailView.BonusSlotDisplay)
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(label).X);
        labelWidth += 16f * ImGuiHelpers.GlobalScale;

        foreach (var (slot, label) in DesignDetailView.SlotDisplay)
        {
            bySlot.TryGetValue(slot, out var entry);
            var itemName = entry == null ? null : plugin.GameData.ResolveItemName(entry.ItemId);
            DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
                entry?.Stain ?? 0, entry?.Stain2 ?? 0, entry?.ApplyStain ?? false, entry?.Apply == true, d.AffectedItems);
        }
        foreach (var (slotKey, label) in DesignDetailView.BonusSlotDisplay)
        {
            byBonus.TryGetValue(slotKey, out var entry);
            var itemName = entry == null ? null : plugin.GameData.ResolveBonusItemName(entry.Slot, entry.ItemId);
            DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
                stain: 0, stain2: 0, applyStain: false, entry?.Apply == true, d.AffectedItems);
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawForeignMods(ForeignDesign d)
    {
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(UiTheme.SectionHeader, "Mod Associations");
        ImGui.Spacing();
        ImGui.Indent();

        if (d.Mods.Count == 0)
        {
            ImGui.TextDisabled("No mods associated with this design");
        }
        else
        {
            foreach (var mod in d.Mods)
            {
                DesignDetailView.DrawModStateIcon(mod.State);
                ImGui.SameLine();
                DesignDetailView.TextColoredUnformatted(UiTheme.ModLink,
                    string.IsNullOrWhiteSpace(mod.Name) ? "(unnamed mod)" : mod.Name);
            }
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }
}
