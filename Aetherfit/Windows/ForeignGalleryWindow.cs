using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// Look-but-don't-touch viewer for an imported gallery: just images and the basic info. There's no apply, favourite,
// edit, or job button anywhere in here, so someone else's gallery can't be applied or changed by accident.
public sealed class ForeignGalleryWindow : Window, IDisposable
{
    private const int CoverColumns = 4;
    private const float CoverMinThumbSize = 96f;
    private const float CoverAspectRatio = 3f / 2f;
    private const string AddFilterPopupId = "ForeignAddFilterPopup";
    private const string DetailsPopupId = "ForeignDesignDetails";

    private readonly Plugin plugin;

    private ForeignGallery? gallery;
    private readonly Dictionary<Guid, int> imageIndex = new();

    private ForeignDesign? detailsDesign;
    private bool openDetailsThisFrame;

    private string filterName = string.Empty;
    // true = must have the tag/job, false = must not have it; a key absent from the map is left alone.
    private readonly Dictionary<string, bool> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, bool> filterJobs = new();
    private string filterSearchText = string.Empty;

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
        filterSearchText = string.Empty;
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

        using (var gridChild = ImRaii.Child("ForeignGridScroll", Vector2.Zero, false))
        {
            if (gridChild.Success)
            {
                var visible = GetVisibleDesigns();
                if (visible.Count == 0)
                    ImGui.TextDisabled(gallery.Designs.Count == 0
                        ? "This shared gallery is empty."
                        : "No designs match the current filters.");
                else
                    DrawGrid(visible);
            }
        }

        // Open and draw the details popup at the window root (not inside the scroll child) so its id and
        // placement stay stable.
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

        return query
            .OrderBy(d => d.Name, NaturalStringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool HasAnyFilter => filterName.Length > 0 || filterTags.Count > 0 || filterJobs.Count > 0;

    private void DrawFilters()
    {
        if (!ImGui.CollapsingHeader("Filters", ImGuiTreeNodeFlags.DefaultOpen))
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

        using (ImRaii.Disabled(!HasAnyFilter))
        {
            if (ImGui.SmallButton("Clear filters"))
            {
                filterName = string.Empty;
                filterTags.Clear();
                filterJobs.Clear();
            }
        }
    }

    private bool DrawTagJobPickerButton()
    {
        var count = filterTags.Count + filterJobs.Count;
        var label = count == 0 ? "Filter by tag(s) or job..." : count == 1 ? "1 tag/job filter active" : $"{count} tag/job filters active";
        return ImGui.Button(label, new Vector2(-1, 0));
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
        ImGui.InputTextWithHint("##foreignFilterSearch", "Search tags or jobs...", ref filterSearchText, 64);
        ImGui.Separator();

        var availableTags = TagMatching.WithSegments(gallery!.Designs.SelectMany(d => d.Tags))
            .Where(t => filterSearchText.Length == 0 || t.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var availableJobs = gallery.Designs
            .SelectMany(d => d.Jobs)
            .Distinct()
            .Select(j => (RowId: j, Name: plugin.GameData.ResolveJobName(j)))
            .Where(j => filterSearchText.Length == 0 || j.Name.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (availableTags.Count == 0 && availableJobs.Count == 0)
        {
            ImGui.TextDisabled(filterSearchText.Length > 0 ? "No matching tags or jobs." : "Nothing to filter by.");
            ImGui.Separator();
            if (ImGui.Button("Done", new Vector2(-1, 0)))
                ImGui.CloseCurrentPopup();
            return;
        }

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var totalRows = (availableTags.Count > 0 ? availableTags.Count + 1 : 0)
                      + (availableJobs.Count > 0 ? availableJobs.Count + 1 : 0);
        var listHeight = Math.Min(totalRows, 12) * rowHeight;

        using (var scroll = ImRaii.Child("ForeignTagJobList", new Vector2(260 * ImGuiHelpers.GlobalScale, listHeight), false))
        {
            if (scroll.Success)
            {
                if (availableTags.Count > 0)
                {
                    ImGui.TextColored(UiTheme.SectionHeader, "Tags");
                    foreach (var tag in availableTags)
                        if (Pills.DrawFilterCheckbox(tag, filterTags.GetFilterState(tag), $"foreignTagCb{tag}"))
                            filterTags.CycleFilterState(tag);
                }

                if (availableJobs.Count > 0)
                {
                    if (availableTags.Count > 0)
                        ImGui.Spacing();
                    ImGui.TextColored(UiTheme.SectionHeader, "Jobs");

                    var lineH = ImGui.GetTextLineHeight();
                    foreach (var job in availableJobs)
                    {
                        var icon = plugin.GameData.GetJobIcon(job.RowId);
                        if (Pills.DrawJobFilterCheckbox(job.Name, filterJobs.GetFilterState(job.RowId), icon, lineH, $"foreignJobCb{job.RowId}"))
                            filterJobs.CycleFilterState(job.RowId);
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 0)))
            ImGui.CloseCurrentPopup();
    }

    private void DrawGrid(List<ForeignDesign> visible)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var thumbWidth = Math.Max(CoverMinThumbSize, (avail - (CoverColumns - 1) * spacing) / CoverColumns);
        var thumbHeight = thumbWidth * CoverAspectRatio;

        for (var i = 0; i < visible.Count; i++)
        {
            if (i % CoverColumns != 0)
                ImGui.SameLine();
            DrawCell(visible[i], thumbWidth, thumbHeight);
        }
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
        var labelWidth = ImGui.CalcTextSize(label).X;
        var indent = Math.Max(0f, (thumbWidth - labelWidth) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + thumbWidth);
        ImGui.TextUnformatted(label);
        ImGui.PopTextWrapPos();
    }

    private void DrawCellTooltip(ForeignDesign design)
    {
        var panelWidth = 300f * ImGuiHelpers.GlobalScale;

        ImGui.BeginTooltip();

        ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.GoldAccent);
        ImGui.PushTextWrapPos(panelWidth);
        ImGui.TextUnformatted(design.Name);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

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
