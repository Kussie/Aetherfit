using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// The left-pane design tree: folder/leaf drawing, the tree's own guide-line decorations, and the
// per-leaf tooltip. "Create new design" leaf itself lives in MainWindow.CreateDesign.cs, but the plain
// "+"-dot glyph it (and the Personas tree's own "create new persona" leaf) draws stays here alongside
// the other leaf-row drawing helpers.
public partial class MainWindow
{
    // Faint grey for the tree indent guide lines, mirroring Glamourer's design list.
    private static readonly Vector4 TreeGuideColor = UiTheme.TreeGuide;
    // Leaf dot radius as a fraction of the text line height (the bullet glyphs were either too big or too small).
    private const float LeafDotRadius = 0.16f;

    private void DrawLeftPane()
    {
        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, "Your Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("Gallery Mode >>", new Vector2(-1, 0)))
            coverMode = true;
        ImGui.Separator();

        if (DrawDesignsUnavailableBanner())
            return;

        DrawFilterUi(extraControls: DrawEditModeGroupByControls);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using var treeChild = ImRaii.Child("OutfitTreeScroll", Vector2.Zero, false);
        if (!treeChild.Success)
            return;

        var hasFilter = HasAnyFilter;
        // Whenthe filter clears, restore every tree node we forced open back to its pre-filter state
        if (!hasFilter && wasFilterActive && treeOpenSnapshot.Count > 0)
        {
            var storage = ImGui.GetStateStorage();
            foreach (var (id, wasOpen) in treeOpenSnapshot)
                storage.SetInt(id, wasOpen ? 1 : 0);
            treeOpenSnapshot.Clear();
        }
        wasFilterActive = hasFilter;

        // Only auto-expand on the frame the filter actually changes - that way the user can still collapse
        // folders while a filter just sits there unchanged.
        var snapshot = CaptureFilterSnapshot();
        var filterChanged = snapshot != filterSnapshot
            || !FiltersEqual(filterTags, filterTagsSnapshot)
            || !FiltersEqual(filterJobs, filterJobsSnapshot)
            || !filterEquipmentSlotsSnapshot.SetEquals(filterEquipmentSlots);
        if (hasFilter && filterChanged)
            expandTreesForFilter = true;
        if (filterChanged)
        {
            filterSnapshot = snapshot;
            filterTagsSnapshot = new(filterTags, StringComparer.OrdinalIgnoreCase);
            filterJobsSnapshot = new(filterJobs);
            filterEquipmentSlotsSnapshot = new(filterEquipmentSlots);
        }

        // Cleared each frame - see FolderHasMatch.
        folderMatchCache.Clear();

        // Widen the vertical gap between rows so the mouse rarely sits on the seam between two items and reports both as hovered in the same frame.
        var spacing = ImGui.GetStyle().ItemSpacing;
        hoveredDesignForTooltip = null;
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, spacing.Y + 3)))
        {
            if (plugin.Configuration.ShowDesignsSection)
            {
                var designsOpen = ImGui.TreeNodeEx($"Designs ({designsCount})##designsRoot",
                    ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
                if (designsOpen)
                {
                    if (groupByJob)
                        DrawJobTree(hasFilter);
                    else if (groupByTags)
                        DrawTagTree(hasFilter);
                    else if (groupBySource)
                        DrawSourceTree(hasFilter);
                    else
                        DrawTree(root, hasFilter);

                    DrawCreateNewDesignLeaf();
                    ImGui.TreePop();
                }
            }

            if (plugin.Configuration.ShowPersonasSection)
                DrawPersonasSection();
        }

        // Tree's drawn, so we're done with the one-shot expand request - clear it for next frame.
        expandTreesForFilter = false;

        // Safety net: if the reveal target's leaf was never actually reached this frame (e.g. it's
        // hidden by an active filter, or the grouped view got switched back on before this ran), don't
        // leave the request dangling - it would otherwise keep forcing the same folders open forever.
        revealDesignInTree = null;
        revealDesignFolderPath = null;
        revealDesignVariantParent = null;

        if (hoveredDesignForTooltip is { } hovered)
            DrawDesignLeafTooltip(hovered);
    }

    // The groupings are mutually exclusive - ticking one unticks the others (all three can be off).
    private void DrawEditModeGroupByControls()
    {
        if (ImGui.Checkbox("Group by job association", ref groupByJob) && groupByJob)
        {
            groupByTags = false;
            groupBySource = false;
        }
        if (ImGui.Checkbox("Group by tags", ref groupByTags) && groupByTags)
        {
            groupByJob = false;
            groupBySource = false;
        }
        if (ImGui.Checkbox("Group by source", ref groupBySource) && groupBySource)
        {
            groupByJob = false;
            groupByTags = false;
        }

        ImGui.Spacing();
        var showDesigns = plugin.Configuration.ShowDesignsSection;
        if (ImGui.Checkbox("Show Designs section", ref showDesigns))
        {
            plugin.Configuration.ShowDesignsSection = showDesigns;
            plugin.Configuration.Save();
        }
        var showPersonas = plugin.Configuration.ShowPersonasSection;
        if (ImGui.Checkbox("Show Personas section", ref showPersonas))
        {
            plugin.Configuration.ShowPersonasSection = showPersonas;
            plugin.Configuration.Save();
        }
    }

    private void DrawTree(FolderNode node, bool hasFilter, int depth = 0)
    {
        foreach (var (name, folder) in node.Folders)
        {
            if (!FolderHasMatch(folder)) continue;

            ForceOpenIfFiltering(name, hasFilter);
            if (revealDesignFolderPath is { } revealPath && depth < revealPath.Count && revealPath[depth] == name)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            var rowX = ImGui.GetCursorScreenPos().X;
            var open = ImGui.TreeNodeEx(name, ImGuiTreeNodeFlags.SpanAvailWidth);
            // Connect this node to its parent's vertical guide with a short horizontal tick.
            DrawTreeItemTick(depth, rowX);

            if (open)
            {
                var drawList = ImGui.GetWindowDrawList();
                var guideX = rowX + TreeArrowCenterOffset();
                var guideTop = ImGui.GetCursorScreenPos().Y;

                DrawTree(folder, hasFilter, depth + 1);

                // Stop the line at the vertical centre of the last child row so it reads as connecting to it.
                var guideBottom = ImGui.GetCursorScreenPos().Y
                                  - ImGui.GetStyle().ItemSpacing.Y
                                  - (ImGui.GetTextLineHeight() * 0.5f);
                if (guideBottom > guideTop)
                    drawList.AddLine(new Vector2(guideX, guideTop), new Vector2(guideX, guideBottom),
                        ImGui.ColorConvertFloat4ToU32(TreeGuideColor), ImGuiHelpers.GlobalScale);

                ImGui.TreePop();
            }
        }

        foreach (var design in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var cached);
            if (!DesignMatchesFilters(design, cached)) continue;
            if (plugin.Configuration.GetVariantInfo(design.Id) != null) continue; // drawn nested under its parent below

            var variantIds = plugin.Configuration.GetVariantsOf(design.Id).Select(kv => kv.Key).ToList();
            var hasVariants = variantIds.Count > 0;

            if (hasVariants && revealDesignVariantParent == design.Id)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            var rowX = ImGui.GetCursorScreenPos().X;
            var open = DrawDesignLeaf(design, hasVariants);
            DrawTreeItemTick(depth, rowX);

            if (revealDesignInTree == design.Id)
            {
                ImGui.SetScrollHereY(0.3f);
                revealDesignInTree = null;
                revealDesignFolderPath = null;
                revealDesignVariantParent = null;
            }

            if (!hasVariants) continue;

            if (open)
            {
                var drawList = ImGui.GetWindowDrawList();
                var guideX = rowX + TreeArrowCenterOffset();
                var guideTop = ImGui.GetCursorScreenPos().Y;

                foreach (var variantId in variantIds)
                {
                    if (!designLeafById.TryGetValue(variantId, out var variantLeaf)) continue;
                    plugin.Configuration.CachedOutfits.TryGetValue(variantId, out var variantCached);
                    if (!DesignMatchesFilters(variantLeaf, variantCached)) continue;

                    var childRowX = ImGui.GetCursorScreenPos().X;
                    DrawDesignLeaf(variantLeaf, false);
                    DrawTreeItemTick(depth + 1, childRowX);

                    if (revealDesignInTree == variantId)
                    {
                        ImGui.SetScrollHereY(0.3f);
                        revealDesignInTree = null;
                        revealDesignFolderPath = null;
                        revealDesignVariantParent = null;
                    }
                }

                var guideBottom = ImGui.GetCursorScreenPos().Y
                                  - ImGui.GetStyle().ItemSpacing.Y
                                  - (ImGui.GetTextLineHeight() * 0.5f);
                if (guideBottom > guideTop)
                    drawList.AddLine(new Vector2(guideX, guideTop), new Vector2(guideX, guideBottom),
                        ImGui.ColorConvertFloat4ToU32(TreeGuideColor), ImGuiHelpers.GlobalScale);

                ImGui.TreePop();
            }
        }
    }

    private static float TreeArrowCenterOffset()
        => ImGui.GetStyle().FramePadding.X + (ImGui.GetFontSize() * 0.5f);

    private static void DrawTreeItemTick(int depth, float rowX)
    {
        if (depth <= 0)
            return;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var centerY = (min.Y + max.Y) * 0.5f;
        // The parent folder sits one indent level to the left; its arrow tip is where the guide runs.
        var guideX = rowX - ImGui.GetStyle().IndentSpacing + TreeArrowCenterOffset();
        var tickEndX = rowX - (2f * ImGuiHelpers.GlobalScale);
        if (tickEndX <= guideX)
            return;

        ImGui.GetWindowDrawList().AddLine(new Vector2(guideX, centerY), new Vector2(tickEndX, centerY),
            ImGui.ColorConvertFloat4ToU32(TreeGuideColor), ImGuiHelpers.GlobalScale);
    }

    // Keyed by design id; invalidated per-entry when favourite state or the display name changes,
    // so a frequently-redrawn tree of leaves isn't rebuilding this string every frame.
    private readonly Dictionary<Guid, (bool IsFavourite, bool HasVariants, string Name, string Label)> leafLabelCache = new();

    // The leading spaces on a non-favourite leaf just reserve room for the manually-drawn dot on a
    // plain Selectable row. A TreeNodeEx row (hasVariants) already gets that same breathing room for
    // free from its own arrow glyph - which is also where the dot itself ends up sitting - so adding
    // the same padding again on top of it just pushes the name further right than it needs to be.
    private string GetLeafLabel(DesignLeaf design, bool isFavourite, bool hasVariants)
    {
        if (leafLabelCache.TryGetValue(design.Id, out var cached)
            && cached.IsFavourite == isFavourite && cached.HasVariants == hasVariants && cached.Name == design.DisplayName)
            return cached.Label;

        var label = isFavourite
            ? $"★ {design.DisplayName}##{design.Id}"
            : hasVariants
                ? $"{design.DisplayName}##{design.Id}"
                : $"   {design.DisplayName}##{design.Id}";
        leafLabelCache[design.Id] = (isFavourite, hasVariants, design.DisplayName, label);
        return label;
    }

    // Returns whether the row is expanded (only meaningful when hasVariants - a plain leaf always
    // returns false, since it has nothing to expand into).
    private bool DrawDesignLeaf(DesignLeaf design, bool hasVariants)
    {
        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);
        var hasColor = design.Color != 0;
        var selected = selectedDesign == design.Id;
        var label = GetLeafLabel(design, isFavourite, hasVariants);
        var open = false;

        using (ImRaii.PushColor(ImGuiCol.Text, design.Color, hasColor))
        {
            if (hasVariants)
            {
                var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow
                    | (selected ? ImGuiTreeNodeFlags.Selected : 0);
                open = ImGui.TreeNodeEx(label, flags);
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    selectedDesign = design.Id;
            }
            else if (ImGui.Selectable(label, selected))
            {
                selectedDesign = design.Id;
            }

            if (!isFavourite)
                DrawLeafDot(hasColor ? design.Color : ImGui.GetColorU32(ImGuiCol.Text), hasVariants);

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                selectedDesign = design.Id;
                ApplyDesignById(design.Id);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
            {
                selectedDesign = design.Id;
                if (plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var quickOpenOutfit)
                    && quickOpenOutfit.Source == DesignSource.Glamourer)
                    plugin.Glamourer.OpenInGlamourer(design.Id, design.DisplayName);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !ImGui.GetIO().KeyShift)
                ImGui.OpenPopup($"##treeDesignContextMenu_{design.Id}");
        }

        using (var contextMenu = ImRaii.Popup($"##treeDesignContextMenu_{design.Id}"))
        {
            if (contextMenu.Success)
            {
                DrawAssignToPersonaSubmenu(design.Id);
                DrawSetAsDefaultDesignSubmenu(design.Id);
            }
        }

        if (ImGui.IsItemHovered())
            hoveredDesignForTooltip = design;

        return open;
    }

    // A filled dot at the start of a leaf row, sized from the line height and tinted to the design's colour.
    // A TreeNodeEx row (hasVariants) draws its own arrow glyph inset by FramePadding.X from the row's
    // start, so the dot needs the same leftward nudge to land on it instead of drifting right of a plain
    // sibling row's dot.
    private static void DrawLeafDot(uint color, bool hasVariants = false)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineH = ImGui.GetTextLineHeight();
        var x = min.X + (lineH * 0.45f) - (hasVariants ? ImGui.GetStyle().FramePadding.X : 0f);
        var center = new Vector2(x, (min.Y + max.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, lineH * LeafDotRadius, color, 16);
    }

    // Draws a hand-drawn + (not the icon font, so it's sized to match the leaf dot's own footprint
    // rather than a full icon glyph reading far too large next to it) over the previously-drawn item -
    // call this immediately after the Selectable it belongs to. Shared by every "Create new X" leaf row.
    private static void DrawCreateLeafPlusIcon()
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineH = ImGui.GetTextLineHeight();
        var center = new Vector2(min.X + (lineH * 0.45f), (min.Y + max.Y) * 0.5f);
        var half = lineH * LeafDotRadius;
        var thickness = 1.5f * ImGuiHelpers.GlobalScale;
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddLine(center - new Vector2(half, 0f), center + new Vector2(half, 0f), color, thickness);
        drawList.AddLine(center - new Vector2(0f, half), center + new Vector2(0f, half), color, thickness);
    }

    private void DrawDesignLeafTooltip(DesignLeaf design)
    {
        var imagePath = plugin.Configuration.ShowThumbnailOnHover ? plugin.ImageStorage.GetCoverPath(design.Id) : null;
        var hasPath = !string.IsNullOrEmpty(design.FullPath);

        ImGui.BeginTooltip();
        if (hasPath)
            ImGui.TextUnformatted(design.FullPath);
        if (imagePath != null)
            DrawImageScaled(imagePath, TooltipImageMax * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled("Double-click to apply");
        ImGui.TextDisabled("Shift + right-click to open in Glamourer");
        ImGui.EndTooltip();
    }
}
