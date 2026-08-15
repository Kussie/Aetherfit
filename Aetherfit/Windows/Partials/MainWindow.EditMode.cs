using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Aetherfit.Services.Integrations;
using Aetherfit.Services.Screenshots;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    // Faint grey for the tree indent guide lines, mirroring Glamourer's design list.
    private static readonly Vector4 TreeGuideColor = UiTheme.TreeGuide;
    // Leaf dot radius as a fraction of the text line height (the bullet glyphs were either too big or too small).
    private const float LeafDotRadius = 0.16f;

    private const float RightPaneImageMax = 220f;
    private const float TooltipImageMax = 160f;
    // Sized to about half the cover's long side so a portrait cover fits roughly two thumbnails per column.
    private const float AdditionalThumbSize = 104f;
    private const string ImageHelpText =
        "The first image you add becomes the cover, shown large above the rest. Drag a thumbnail onto the cover (or the cover onto a thumbnail) to swap which one is the cover. "
        + "Click an image to view it full size. Hold Shift and right-click to remove. \"Browse\" picks a file; \"Snap\" captures from the game.";

    private const string ImageDragType = "AF_IMAGE";
    private const string CoverDragType = "AF_COVER";
    private int draggedImageIndex = -1;

    private const string PullDescriptionPopupId = "Pull Description from Glamourer?##pullDescConfirm";
    private const string ForceSyncPopupId = "Force Sync?##forceSyncConfirm";
    private const string ImportToGlamourerPopupId = "Import into Glamourer?##importGlamourerConfirm";
    private const string AddTagPopupId = "AddDesignTagPopup";
    private const string AddVariantPopupId = "AddVariantPopup";
    private string variantPickerFilter = string.Empty;
    private string addTagSearchText = string.Empty;
    private string importDesignName = string.Empty;
    private bool importReclaimFocus;
    private bool importDeleteFromGlamaholic;
    private bool addTagReclaimFocus;

    // Reset whenever the selected design changes so edit mode always starts fresh for the new selection.
    private Guid? descriptionEditId;
    private bool descriptionEditing;
    private string descriptionEditBuffer = string.Empty;
    private string? descriptionOriginalValue;

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
            if (groupByJob)
                DrawJobTree(hasFilter);
            else if (groupByTags)
                DrawTagTree(hasFilter);
            else if (groupBySource)
                DrawSourceTree(hasFilter);
            else
                DrawTree(root, hasFilter);
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
                DrawLeafDot(hasColor ? design.Color : ImGui.GetColorU32(ImGuiCol.Text));

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
        }

        if (ImGui.IsItemHovered())
            hoveredDesignForTooltip = design;

        return open;
    }

    // A filled dot at the start of a leaf row, sized from the line height and tinted to the design's colour.
    private static void DrawLeafDot(uint color)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineH = ImGui.GetTextLineHeight();
        var center = new Vector2(min.X + (lineH * 0.45f), (min.Y + max.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, lineH * LeafDotRadius, color, 16);
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

    private void DrawWelcomePlaceholder()
    {
        const string message = "Select a design on the left to see its details.";
        var avail = ImGui.GetContentRegionAvail();

        var iconDir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        var iconPath = iconDir != null ? Path.Combine(iconDir, "icon-square.png") : null;
        var tex = iconPath != null && File.Exists(iconPath)
            ? Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrEmpty()
            : null;

        Vector2 imageSize = Vector2.Zero;
        if (tex is { Width: > 0, Height: > 0 })
        {
            var maxSide = Math.Min(256f * ImGuiHelpers.GlobalScale, Math.Min(avail.X, avail.Y * 0.6f));
            var scale = Math.Min(maxSide / tex.Width, maxSide / tex.Height);
            if (scale > 0f)
                imageSize = new Vector2(tex.Width * scale, tex.Height * scale);
        }

        var textSize = ImGui.CalcTextSize(message);
        var spacing = imageSize.Y > 0 ? ImGui.GetStyle().ItemSpacing.Y : 0f;
        var totalHeight = imageSize.Y + spacing + textSize.Y;
        var startY = ImGui.GetCursorPosY() + Math.Max(0f, (avail.Y - totalHeight) * 0.5f);

        if (imageSize.Y > 0 && tex != null)
        {
            ImGui.SetCursorPosY(startY);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail.X - imageSize.X) * 0.5f));
            ImGui.Image(tex.Handle, imageSize);
            startY = ImGui.GetCursorPosY() + spacing;
        }

        ImGui.SetCursorPosY(startY);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail.X - textSize.X) * 0.5f));
        ImGui.TextDisabled(message);
    }

    private void DrawSelectedOutfitDetails()
    {
        if (selectedDesign is not { } id)
        {
            DrawWelcomePlaceholder();
            return;
        }

        if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var details))
        {
            ImGui.TextDisabled("No cached metadata for this design. Click Refresh.");
            return;
        }

        // Always exactly two rows now: Created/Last worn, then Last edited/Source - Created and Last
        // edited are omitted when absent, but Last worn and Source always occupy their row regardless.
        var datesBlockHeight = 2 * ImGui.GetTextLineHeightWithSpacing();

        var bodyHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - datesBlockHeight);

        using (var body = ImRaii.Child("DesignBody", new Vector2(0, bodyHeight), false))
        {
            if (body.Success)
            {
                var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(id);
                var isHidden = plugin.Configuration.HiddenDesigns.Contains(id);
                var isGlamourer = details.Source == DesignSource.Glamourer;
                var isGlamaholic = details.Source == DesignSource.Glamaholic;
                var isGlamourPlate = details.Source == DesignSource.GlamourPlate;
                var showForceSync = isGlamourer || isGlamaholic;
                var showImport = isGlamaholic || isGlamourPlate;
                var showBulkLayer = isGlamourer && plugin.Configuration.EnableRandomLayers;
                var style = ImGui.GetStyle();
                var inner = style.ItemInnerSpacing.X;

                // Measure the action cluster first so the title can be ellipsized to the space that remains.
                var frameH = ImGui.GetFrameHeight();
                float starW, eyeW, revealW, linkW, syncW, importW, bulkLayerW, variantW;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    starW = ImGui.CalcTextSize(FontAwesomeIcon.Star.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    eyeW = ImGui.CalcTextSize((isHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString()).X
                         + (style.FramePadding.X * 2);
                    revealW = ImGui.CalcTextSize(FontAwesomeIcon.Sitemap.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    linkW = ImGui.CalcTextSize(FontAwesomeIcon.ExternalLinkAlt.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    syncW = ImGui.CalcTextSize(FontAwesomeIcon.CloudUploadAlt.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    importW = ImGui.CalcTextSize(FontAwesomeIcon.FileImport.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    bulkLayerW = ImGui.CalcTextSize(FontAwesomeIcon.LayerGroup.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    variantW = ImGui.CalcTextSize(FontAwesomeIcon.CodeBranch.ToIconString()).X
                          + (style.FramePadding.X * 2);
                }
                // Glamourer gets both the open-in-native-UI link and the sync button; Glamaholic (no native UI
                // concept - see GlamaholicService.OpenInNativeUi) only gets the sync button. Glamaholic and
                // Glamour Plate both get the import-into-Glamourer button. Only Glamourer designs can be used
                // as a layer at all (AllDesignsSorted), so the bulk-assign button only ever shows for those,
                // and only while the layers feature itself is switched on.

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (6f * ImGuiHelpers.GlobalScale));
                var iconRowX = ImGui.GetCursorPosX();
                ImGui.SetWindowFontScale(1.5f);
                var titleAvail = Math.Max(50f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X);
                var title = TextFit.Ellipsize(details.Name, titleAvail);
                ImGui.TextColored(UiTheme.GoldAccent, title);
                ImGui.SetWindowFontScale(1.0f);
                if (title != details.Name && ImGui.IsItemHovered())
                    ImGui.SetTooltip(details.Name);

                // The action icons sit on their own row under the title now, rather than squeezed
                // alongside it, so a long design name never has to compete with them for space.
                ImGui.SetCursorPosX(iconRowX);
                if (HeaderIconButton("favStar", FontAwesomeIcon.Star,
                        isFavourite ? UiTheme.FavouriteStar : UiTheme.FavouriteButtonOff,
                        new Vector2(starW, frameH)))
                {
                    if (isFavourite)
                        plugin.Configuration.FavouriteDesigns.Remove(id);
                    else
                        plugin.Configuration.FavouriteDesigns.Add(id);
                    plugin.Configuration.Save();
                    favouriteVersion++;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isFavourite ? "Click to remove from favourites" : "Click to add to favourites");

                ImGui.SameLine(0, inner);
                if (HeaderIconButton("hideEye", isHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
                        isHidden ? UiTheme.HiddenEyeOn : UiTheme.HiddenButtonOff,
                        new Vector2(eyeW, frameH)))
                {
                    if (isHidden)
                        plugin.Configuration.HiddenDesigns.Remove(id);
                    else
                        plugin.Configuration.HiddenDesigns.Add(id);
                    plugin.Configuration.Save();
                    hiddenVersion++;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isHidden
                        ? "Hidden — click to show in the gallery and exports"
                        : "Click to hide from the gallery and exports");

                ImGui.SameLine(0, inner);
                if (HeaderIconButton("revealInTree", FontAwesomeIcon.Sitemap, null, new Vector2(revealW, frameH)))
                    RevealDesignInTree(id);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Show in the tree");

                ImGui.SameLine(0, inner);
                var currentVariant = plugin.Configuration.GetVariantInfo(id);
                if (HeaderIconButton("addVariant", FontAwesomeIcon.CodeBranch,
                        currentVariant != null ? UiTheme.GoldAccent : null, new Vector2(variantW, frameH)))
                {
                    variantPickerFilter = string.Empty;
                    ImGui.OpenPopup(AddVariantPopupId);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(currentVariant != null ? "Change this design's variant parent" : "Add as Variant");
                DrawAddVariantPopup(id);

                if (isGlamourer)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("openGlamourer", FontAwesomeIcon.ExternalLinkAlt, null, new Vector2(linkW, frameH)))
                        plugin.Glamourer.OpenInGlamourer(id, details.Name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Open in Glamourer");
                }

                if (showForceSync)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("forceSync", FontAwesomeIcon.CloudUploadAlt, null, new Vector2(syncW, frameH)))
                        ConfirmDialog.Open(ForceSyncPopupId);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(isGlamourer
                            ? "Force sync Tags and Description to the Glamourer design file"
                            : "Force sync Tags to the Glamaholic config file");

                    var forceSyncMessage = isGlamourer
                        ? $"This will overwrite the Description and Tags stored in the Glamourer design file for \"{details.Name}\" "
                          + "with what's shown here in Aetherfit, without touching anything else in the file.\n\n"
                          + "If this design is currently open in Glamourer's own editor, Glamourer may discard this change "
                          + "the moment you touch it there or on its next autosave. Close the design in Glamourer first."
                        : $"This will overwrite the Tags stored in the Glamaholic config file for \"{details.Name}\" "
                          + "with what's shown here in Aetherfit, without touching anything else in the file.";

                    if (ConfirmDialog.Draw(ForceSyncPopupId, forceSyncMessage, "Force Sync"))
                    {
                        if (isGlamourer)
                        {
                            var result = plugin.GlamourerDesignFile.PushMetadataToGlamourer(id, details.Description, details.Tags);
                            if (result.Success)
                                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Pushed Tags and Description to Glamourer for \"{details.Name}\"");
                            else
                                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
                        }
                        else
                        {
                            var result = plugin.Glamaholic.PushTagsToGlamaholic(details.ProviderDesignId, details.Tags);
                            if (result.Success)
                                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Pushed Tags to Glamaholic for \"{details.Name}\"");
                            else
                                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
                        }
                    }
                }

                if (showImport)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("importGlamourer", FontAwesomeIcon.FileImport, null, new Vector2(importW, frameH)))
                    {
                        importDesignName = isGlamaholic ? details.Name : string.Empty;
                        importDeleteFromGlamaholic = false;
                        importReclaimFocus = true;
                        ImGui.OpenPopup(ImportToGlamourerPopupId);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Import into Glamourer");
                }

                DrawImportToGlamourerPopup(details, isGlamaholic);

                if (showBulkLayer)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("bulkLayerAssign", FontAwesomeIcon.LayerGroup, null, new Vector2(bulkLayerW, frameH)))
                        OpenBulkLayerAssignPopup(id);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Apply as a layer to multiple designs");
                }

                DrawBulkLayerAssignPopup();

                ImGui.Spacing();

                DrawJobAssociations(id);

                if (plugin.Configuration.GetVariantInfo(id) is { } variant)
                    DrawVariantSection(id, variant);

                if (Pills.DrawCollapsibleSubheader("Tags", ref tagsPanelOpen))
                {
                    ImGui.Indent();
                    if (!plugin.Configuration.CompositeTagsHelpDismissed)
                        DrawCompositeTagsHelpNote();
                    if (details.Tags.Count == 0)
                        ImGui.TextDisabled("This design has no tags set.");
                    DrawTagsRow(id, details);
                    ImGui.Spacing();
                    DrawTagSuggestionsBlock(id, details);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (Pills.DrawCollapsibleSubheader("Description", ref descriptionPanelOpen))
                {
                    ImGui.Indent();
                    DrawDescriptionEditor(id, details);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (Pills.DrawCollapsibleSubheader("Images", ref imagesPanelOpen, ImageHelpText))
                {
                    ImGui.Indent();
                    DrawImagesBlock(id);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                DrawEquipmentPanel(id, details);
                DrawCustomizationsPanel(id, details);
                DrawDesignLinksPanel(details);
                if (details.Source is DesignSource.Glamourer or DesignSource.SimpleGlamourSwitcher)
                    DrawModsPanel(details);
                // Layers are applied via their own provider on top of whatever base was applied, so the
                // base design's source doesn't matter here - only Glamourer-sourced designs can be picked
                // as a layer (see AllDesignsSorted), not which designs can carry layers.
                if (plugin.Configuration.EnableRandomLayers)
                    DrawAdditionalLayersPanel(id);
            }
        }

        // Nudge the floating footer in one level so the dates line up with the indented content above.
        var sourceName = plugin.DesignProviders.FirstOrDefault(p => p.Source == details.Source)?.DisplayName
            ?? details.Source.ToString();
        var sourceText = $"Source: {sourceName}";

        // Last worn is always shown ("Never" rather than hidden - it's itself a meaningful, common
        // state), paired on a row with Created above Last edited/Source below, mirroring how Created
        // and Last worn both describe "when," same as Last edited and Source both sit on the bottom row.
        var lastWornText = details.LastAppliedAt is { } worn ? $"Last worn: {FormatFriendlyRelative(worn)}" : "Last worn: Never";
        var lastWornTooltip = details.LastAppliedAt is { } w ? FormatFullDate(w) : null;

        string? createdText = null, createdTooltip = null;
        if (details.CreatedAt is { } created)
        {
            createdText = $"Created: {FormatFriendlyRelative(created)}";
            createdTooltip = FormatFullDate(created);
        }

        string? lastEditedText = null, lastEditedTooltip = null;
        if (details.LastEdit is { } edited)
        {
            lastEditedText = $"Last edited: {FormatFriendlyRelative(edited)}";
            lastEditedTooltip = FormatFullDate(edited);
        }

        ImGui.Indent();
        DrawFooterRow(createdText, createdTooltip, lastWornText, lastWornTooltip);
        DrawFooterRow(lastEditedText, lastEditedTooltip, sourceText, null);
        ImGui.Unindent();
    }

    private void DrawImagesBlock(Guid id)
    {
        var coverPath = plugin.ImageStorage.GetCoverPath(id);
        var thumb = AdditionalThumbSize * ImGuiHelpers.GlobalScale;

        // With no cover there are no images at all, so the two tiles set the very first image as the cover.
        if (coverPath == null)
        {
            if (DrawImageActionTile("coverBrowse", FontAwesomeIcon.FolderOpen, "Browse", "Pick an image file", thumb))
                OpenImagePicker(id);
            ImGui.SameLine();
            if (DrawImageActionTile("coverSnap", FontAwesomeIcon.Camera, "Snap", "Capture from the game", thumb))
                plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.SetCover(id, croppedPath));
            return;
        }

        // A drop during the same frame also registers as a release-click; suppress the viewer then.
        var dragActive = !ImGui.GetDragDropPayload().IsNull;
        var style = ImGui.GetStyle();
        var fullAvail = ImGui.GetContentRegionAvail().X;

        var paths = plugin.ImageStorage.GetAdditionalPaths(id);
        var promoteIndex = -1;
        var toRemoveIndex = -1;
        var deleteCover = false;

        using (ImRaii.Group())
        {
            if (DrawImageScaled(coverPath, RightPaneImageMax * ImGuiHelpers.GlobalScale, clickable: true, title: "Cover Image") && !dragActive)
                plugin.ImageViewer.Show(coverPath);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                deleteCover = true;

            // The cover can be dragged onto a thumbnail to swap them.
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
            {
                ImGui.SetDragDropPayload(CoverDragType, ReadOnlySpan<byte>.Empty);
                DrawImageScaled(coverPath, thumb);
                ImGui.EndDragDropSource();
            }

            // Dropping a thumbnail onto the cover promotes it (the old cover drops into the wall).
            if (ImGui.BeginDragDropTarget())
            {
                if (AcceptDragPayload(ImageDragType) && draggedImageIndex >= 0)
                    promoteIndex = draggedImageIndex;
                ImGui.EndDragDropTarget();
            }
        }
        var coverSize = ImGui.GetItemRectSize();
        var availRight = fullAvail - coverSize.X - style.ItemSpacing.X;
        var placeRight = availRight >= thumb;
        if (placeRight)
            ImGui.SameLine();
        else
            ImGui.Spacing();

        using (ImRaii.Group())
        {
            var origin = ImGui.GetCursorScreenPos();
            var strideX = thumb + style.ItemSpacing.X;
            var strideY = thumb + style.ItemSpacing.Y;

            var underCap = paths.Count < ImageStorageService.MaxAdditionalImages;
            var tileCount = underCap ? 2 : 0;
            var totalItems = paths.Count + tileCount;

            int columns = 0, rows;
            var columnMajor = placeRight;
            if (columnMajor)
            {
                // As many rows as roughly fill the cover's height, then widen into columns for the rest.
                // Grow the row count if needed so the columns (allowing one spare cell for the tile pair)
                // stay within the width beside the cover.
                rows = Math.Max(1, Math.Min(totalItems, (int)Math.Round(coverSize.Y / strideY)));
                var maxColumns = Math.Max(1, (int)((availRight + style.ItemSpacing.X) / strideX));
                while ((totalItems + 1 + rows - 1) / rows > maxColumns && rows < totalItems + 1)
                    rows++;
            }
            else
            {
                columns = Math.Max(1, (int)((fullAvail + style.ItemSpacing.X) / strideX));
                rows = (totalItems + columns - 1) / columns;
            }

            // Keep the Browse/Snap pair adjacent: if the first tile would land on a column's bottom row (so
            // the second wraps to the next column), skip that cell and start the pair at the next column top.
            var tileGap = columnMajor && tileCount == 2 && rows > 1 && paths.Count % rows == rows - 1 ? 1 : 0;

            for (var k = 0; k < totalItems; k++)
            {
                int col, row;
                if (columnMajor)
                {
                    var slot = k < paths.Count ? k : k + tileGap;
                    col = slot / rows;
                    row = slot % rows;
                }
                else
                {
                    col = k % columns;
                    row = k / columns;
                }
                ImGui.SetCursorScreenPos(new Vector2(origin.X + col * strideX, origin.Y + row * strideY));

                if (k < paths.Count)
                {
                    using (ImRaii.PushId(k))
                    {
                        var clicked = DrawSquareThumbnail(paths[k], thumb, out var deleteRequested);

                        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
                        {
                            draggedImageIndex = k;
                            ImGui.SetDragDropPayload(ImageDragType, ReadOnlySpan<byte>.Empty);
                            DrawImageScaled(paths[k], thumb);
                            ImGui.EndDragDropSource();
                        }

                        // Dropping the cover here swaps them: this thumbnail becomes the cover, the old one takes its slot.
                        if (ImGui.BeginDragDropTarget())
                        {
                            if (AcceptDragPayload(CoverDragType))
                                promoteIndex = k;
                            ImGui.EndDragDropTarget();
                        }

                        if (clicked && !dragActive)
                            plugin.ImageViewer.Show(paths[k]);
                        if (deleteRequested)
                            toRemoveIndex = k;
                    }
                }
                else if (k == paths.Count)
                {
                    if (DrawImageActionTile("addBrowse", FontAwesomeIcon.FolderOpen, "Browse", "Pick an image file", thumb))
                        OpenAdditionalImagePicker(id);
                }
                else
                {
                    if (DrawImageActionTile("addSnap", FontAwesomeIcon.Camera, "Snap", "Capture from the game", thumb))
                        plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.AddAdditional(id, croppedPath));
                }
            }
        }

        // At most one of these fires per frame; promotion takes priority so a drop is never also read as a delete.
        if (promoteIndex >= 0)
            plugin.ImageStorage.PromoteToCover(id, promoteIndex);
        else if (toRemoveIndex >= 0)
            plugin.ImageStorage.RemoveAdditional(id, toRemoveIndex);
        else if (deleteCover)
            plugin.ImageStorage.RemoveCover(id);
    }

    // Ghost icon button for the detail header. All three actions render through here with the same
    // font and frame height so the glyphs line up; tint null keeps the normal text colour.
    private static bool HeaderIconButton(string id, FontAwesomeIcon icon, Vector4? tint, Vector2 size)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.ButtonHovered, UiTheme.GhostButtonHovered)
            .Push(ImGuiCol.ButtonActive, UiTheme.GhostButtonActive)
            .Push(ImGuiCol.Text, tint);
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
        return clicked;
    }

    // A square tile with an icon over a label, framed so the add/snap actions read as buttons
    // sitting next to the image thumbnails.
    private static bool DrawImageActionTile(string id, FontAwesomeIcon icon, string label, string tooltip, float size)
    {
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f)
            .Push(ImGuiStyleVar.FrameBorderSize, ImGuiHelpers.GlobalScale);
        using var colors = ImRaii.PushColor(ImGuiCol.Button, UiTheme.PlaceholderBg)
            .Push(ImGuiCol.Border, UiTheme.ImageTileBorder);
        var clicked = ImGui.Button($"##imgTile{id}", new Vector2(size, size));

        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(tooltip);

        var iconStr = icon.ToIconString();
        Vector2 iconSize;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconSize = ImGui.CalcTextSize(iconStr);
        var labelSize = ImGui.CalcTextSize(label);
        var gap = 4f * ImGuiHelpers.GlobalScale;

        var dl = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled);
        var min = ImGui.GetItemRectMin();
        var centerX = min.X + (size * 0.5f);
        var startY = min.Y + ((size - (iconSize.Y + gap + labelSize.Y)) * 0.5f);
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            dl.AddText(new Vector2(centerX - (iconSize.X * 0.5f), startY), color, iconStr);
        dl.AddText(new Vector2(centerX - (labelSize.X * 0.5f), startY + iconSize.Y + gap), color, label);

        return clicked;
    }

    private void OpenImagePicker(Guid id)
    {
        fileDialog.OpenFileDialog(
            "Pick an image for this design",
            ImageFilters,
            (success, paths) =>
            {
                if (!success || paths.Count == 0)
                    return;
                plugin.ImageStorage.SetCover(id, paths[0]);
            },
            1);
    }

    private void OpenAdditionalImagePicker(Guid id)
    {
        fileDialog.OpenFileDialog(
            "Pick an additional image",
            ImageFilters,
            (success, paths) =>
            {
                if (!success || paths.Count == 0)
                    return;
                plugin.ImageStorage.AddAdditional(id, paths[0]);
            },
            1);
    }

    // A footer row: an optional left-aligned label (omitted entirely when null, e.g. a source with no
    // Created/Last edited of its own) and an always-present right-aligned label.
    private static void DrawFooterRow(string? leftText, string? leftTooltip, string rightText, string? rightTooltip)
    {
        if (leftText != null)
        {
            ImGui.TextDisabled(leftText);
            if (leftTooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(leftTooltip);
        }

        var rightW = ImGui.CalcTextSize(rightText).X;
        if (leftText != null)
        {
            var style = ImGui.GetStyle();
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + style.ItemSpacing.X,
                ImGui.GetContentRegionMax().X - rightW));
        }
        else
        {
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - rightW));
        }
        ImGui.TextDisabled(rightText);
        if (rightTooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(rightTooltip);
    }

    private static string FormatFriendlyRelative(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.Now - dt;
        if (diff.TotalSeconds < 0) return FormatFullDate(dt);
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 2) return "a minute ago";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} minutes ago";
        if (diff.TotalHours < 2) return "an hour ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hours ago";
        if (diff.TotalDays < 2) return "yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 14) return "last week";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 60) return "last month";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        if (diff.TotalDays < 730) return "last year";
        return $"{(int)(diff.TotalDays / 365)} years ago";
    }

    private static string FormatFullDate(DateTimeOffset dt) =>
        dt.LocalDateTime.ToString("dddd, MMMM d, yyyy 'at' h:mm tt");

    private void DrawTagsRow(Guid id, CachedOutfit details)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;
        var first = true;

        string? tagToRemove = null;
        foreach (var tag in details.Tags)
        {
            var width = ImGui.CalcTextSize(tag).X;
            Pills.PlaceItem(width, ref first, ref lineRight, cursorStart, spacing, availRight);

            DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, tag);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"Show all designs tagged \"{tag}\"\nShift + right-click to remove");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    filterTags[tag] = true;
                else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                    tagToRemove = tag;
            }
        }

        var addWidth = ImGui.GetFrameHeight();
        Pills.PlaceItem(addWidth, ref first, ref lineRight, cursorStart, spacing, availRight);
        if (ImGuiComponents.IconButton("addTag", FontAwesomeIcon.Plus))
        {
            addTagSearchText = string.Empty;
            // Drop the popup below the button instead of centering on it, so it doesn't cover the tag(s) just added.
            var popupPos = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + ImGui.GetStyle().ItemSpacing.Y + (4f * ImGuiHelpers.GlobalScale));
            ImGui.SetNextWindowPos(popupPos);
            ImGui.OpenPopup(AddTagPopupId);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add tag");

        if (details.Source is DesignSource.Glamourer or DesignSource.Glamaholic)
        {
            var newTagCount = details.GlamourerTags.Count(t => !details.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
            var refreshWidth = ImGui.GetFrameHeight();
            Pills.PlaceItem(refreshWidth, ref first, ref lineRight, cursorStart, spacing, availRight);
            if (ImGuiComponents.IconButton("mergeTags", FontAwesomeIcon.Sync) && newTagCount > 0)
            {
                var added = plugin.Configuration.MergeTagsFromGlamourer(id, details);
                if (added > 0)
                    Plugin.ChatGui.Print($"{Plugin.ChatPrefix}+{added} tag{(added == 1 ? "" : "s")} added from {details.Source}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(newTagCount > 0
                    ? $"Merge {newTagCount} tag{(newTagCount == 1 ? "" : "s")} in from {details.Source}"
                    : $"No new tags to merge from {details.Source}");
        }

        DrawAddTagPopup(id, details);

        if (tagToRemove != null)
            plugin.Configuration.RemoveTag(id, details, tagToRemove);
    }

    // Single-level nesting only: a design that's already a variant of something can't itself be
    // picked as a parent, which transitively also excludes id's own existing variants (they already
    // carry a VariantInfo entry pointing at id).
    private void DrawAddVariantPopup(Guid id)
    {
        using var popup = ImRaii.Popup(AddVariantPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##variantFilter", "Filter by name...", ref variantPickerFilter, 64);
        ImGui.Separator();

        var matches = plugin.Configuration.CachedOutfits
            .Where(kv => kv.Key != id && plugin.Configuration.GetVariantInfo(kv.Key) == null
                        && (variantPickerFilter.Length == 0 || kv.Value.Name.Contains(variantPickerFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => (Id: kv.Key, kv.Value.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No matching designs.");
            return;
        }

        var listHeight = Math.Min(matches.Count, MaxVisibleDesignRows) * ImGui.GetTextLineHeightWithSpacing();
        using var scroll = ImRaii.Child("##variantPickerList", new Vector2(250 * ImGuiHelpers.GlobalScale, listHeight), false);
        foreach (var (parentId, name) in matches)
        {
            if (ImGui.Selectable($"{name}##variant{parentId}"))
            {
                var existing = plugin.Configuration.GetVariantInfo(id);
                plugin.Configuration.SetVariantParent(id, parentId,
                    existing?.InheritTagsAndDescription ?? true, existing?.InheritGear ?? false);
                plugin.Configuration.Save();
                variantVersion++;
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void DrawVariantSection(Guid id, VariantInfo variant)
    {
        if (!Pills.DrawCollapsibleSubheader("Variant", ref variantPanelOpen))
            return;
        ImGui.Indent();

        var parentName = ResolveLinkedDesignName(variant.ParentId);
        ImGui.TextDisabled("Variant of:");
        ImGui.SameLine();
        DesignDetailView.TextColoredUnformatted(ModLinkColor, parentName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Click to open in Aetherfit");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                OpenDesign(variant.ParentId);
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Unlink))
        {
            plugin.Configuration.RemoveVariant(id);
            plugin.Configuration.Save();
            variantVersion++;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove variant relationship");

        var inheritTags = variant.InheritTagsAndDescription;
        if (ImGui.Checkbox("Inherit tags and description from parent when unset", ref inheritTags))
        {
            variant.InheritTagsAndDescription = inheritTags;
            plugin.Configuration.ApplyVariantTagDescriptionFallback(id);
            plugin.Configuration.Save();
            variantVersion++;
        }

        var inheritGear = variant.InheritGear;
        if (ImGui.Checkbox("Inherit gear and customizations (applies parent first)", ref inheritGear))
        {
            variant.InheritGear = inheritGear;
            plugin.Configuration.Save();
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawImportToGlamourerPopup(CachedOutfit details, bool isGlamaholic)
    {
        // Height recomputed every frame (not just on Appearing) so the window grows to fit the warning
        // below once the delete checkbox is ticked, instead of clipping it behind a size fixed before
        // that text ever existed.
        ImGui.SetNextWindowSize(new Vector2(420, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(ImportToGlamourerPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return;

        ImGui.TextWrapped($"This will create a new design in Glamourer from \"{details.Name}\"'s equipment. "
            + "The wearer's current face/body won't be included - only gear.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Design name");
        if (ImGui.IsWindowAppearing() || importReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            importReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##importDesignName", "Design name (required)", ref importDesignName, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = importDesignName.Trim();

        if (isGlamaholic)
        {
            ImGui.Spacing();
            ImGui.Checkbox("Remove this design from Glamaholic after import", ref importDeleteFromGlamaholic);
            if (importDeleteFromGlamaholic)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, UiTheme.ErrorText);
                ImGui.TextWrapped(
                    "Glamaholic won't see this removal until it's restarted or you relog - it keeps its own "
                    + "plate list in memory and won't notice the change to its file until then.");
            }
        }

        ImGui.Spacing();

        var canConfirm = trimmed.Length > 0;
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Import") || (submitted && canConfirm))
            {
                ImGui.CloseCurrentPopup();
                DoImportToGlamourer(details, trimmed, isGlamaholic && importDeleteFromGlamaholic);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void DoImportToGlamourer(CachedOutfit details, string name, bool deleteFromGlamaholic)
    {
        var equipment = details.Source == DesignSource.Glamaholic
            ? plugin.Glamaholic.BuildImportPayload(details.ProviderDesignId)?.Equipment
            : plugin.GlamourPlate.BuildImportPayload(details.ProviderDesignId);

        if (equipment == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: design data not currently available - try refreshing.");
            return;
        }

        var (stateResult, state) = plugin.Glamourer.GetState();
        if (stateResult != GlamourerApiEc.Success || state == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: couldn't read current Glamourer state ({stateResult}).");
            return;
        }

        var designJson = GlamourerJsonSchema.BuildEquipmentOnlyDesign(state, equipment);
        var (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name);
        if (addResult != GlamourerApiEc.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: {addResult}");
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Imported \"{name}\" into Glamourer.");

        if (deleteFromGlamaholic)
        {
            var deleteResult = plugin.Glamaholic.DeletePlate(details.ProviderDesignId);
            if (!deleteResult.Success)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import succeeded, but couldn't remove \"{details.Name}\" from Glamaholic: {deleteResult.Error}");
            else
                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Removed \"{details.Name}\" from Glamaholic.");
        }

        selectedDesign = newId;
        RefreshDesigns();
    }

    private void DrawAddTagPopup(Guid id, CachedOutfit details)
    {
        using var popup = ImRaii.Popup(AddTagPopupId);
        if (!popup.Success)
            return;

        // Refocus after each add so Enter can chain straight into the next tag without a re-click.
        if (ImGui.IsWindowAppearing() || addTagReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            addTagReclaimFocus = false;
        }

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##addTagSearch", "Type or search a tag...", ref addTagSearchText, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        var trimmed = addTagSearchText.Trim();
        var existingTags = plugin.Configuration.DistinctSortedTags()
            .Where(t => !details.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Where(t => trimmed.Length == 0 || t.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var isNewTag = trimmed.Length > 0
            && !details.Tags.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            && !existingTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase);

        if (submitted)
        {
            if (trimmed.Length > 0)
            {
                plugin.Configuration.AddTag(id, details, trimmed);
                addTagSearchText = string.Empty;
                addTagReclaimFocus = true;
            }
            else
            {
                // A blank Enter is the "I'm done" signal - stop the add-tag loop.
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.Separator();

        if (isNewTag && ImGui.Selectable($"Add new tag \"{trimmed}\""))
        {
            plugin.Configuration.AddTag(id, details, trimmed);
            addTagSearchText = string.Empty;
            addTagReclaimFocus = true;
        }

        if (existingTags.Count == 0)
        {
            if (!isNewTag)
                ImGui.TextDisabled(trimmed.Length > 0 ? "No matching tags." : "All tags are already applied.");
            return;
        }

        if (isNewTag)
            ImGui.Separator();

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var listHeight = Math.Min(existingTags.Count, 8) * rowHeight;
        using var scroll = ImRaii.Child("AddTagList", new Vector2(220 * ImGuiHelpers.GlobalScale, listHeight), false);
        if (!scroll.Success)
            return;

        foreach (var tag in existingTags)
        {
            if (ImGui.Selectable(tag))
            {
                plugin.Configuration.AddTag(id, details, tag);
                addTagSearchText = string.Empty;
                addTagReclaimFocus = true;
            }
        }
    }

    private void DrawCompositeTagsHelpNote()
    {
        const string helpText =
            "Tags can be written as category/type, e.g. swimsuit/bikini or colour/blue. A design tagged this "
            + "way matches filters for the full tag or either half on its own, so it shows up whether you "
            + "filter by swimsuit/bikini, just swimsuit, or just bikini. When designs are grouped by tags "
            + "instead of folders, composite tags also form a nested tree instead of one flat entry — "
            + "swimsuit/bikini shows up as a swimsuit branch containing a bikini branch.";

        var style = ImGui.GetStyle();
        var pad = 8f * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var closeSize = ImGui.GetFrameHeight();
        var wrapW = availW - (pad * 2) - closeSize - style.ItemSpacing.X;
        var textH = ImGui.CalcTextSize(helpText, false, wrapW).Y;
        var boxH = Math.Max(textH, closeSize) + (pad * 2);

        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(availW, boxH),
            ImGui.ColorConvertFloat4ToU32(UiTheme.ToggleOffBg), 4f);

        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapW);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(new Vector2(
            start.X + availW - closeSize - (pad * 0.5f), start.Y + (pad * 0.5f)));
        if (HeaderIconButton("compositeTagsHelpClose", FontAwesomeIcon.Times, UiTheme.PlaceholderText,
                new Vector2(closeSize, closeSize)))
        {
            plugin.Configuration.CompositeTagsHelpDismissed = true;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dismiss (won't be shown again)");

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + boxH));
        ImGui.Spacing();
    }

    private void DrawDescriptionEditor(Guid id, CachedOutfit details)
    {
        if (descriptionEditId != id)
        {
            descriptionEditId = id;
            descriptionEditing = false;
        }

        if (descriptionEditing)
        {
            ImGui.SetNextItemWidth(-1);
            var boxHeight = 4 * ImGui.GetTextLineHeightWithSpacing();
            if (ImGui.InputTextMultiline("##description", ref descriptionEditBuffer, 2000, new Vector2(-1, boxHeight)))
            {
                var trimmed = descriptionEditBuffer.Trim();
                plugin.Configuration.SetDescription(id, details, trimmed.Length == 0 ? null : trimmed);
            }

            if (ImGuiComponents.IconButton("descDone", FontAwesomeIcon.Check))
                descriptionEditing = false;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Done");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton("descCancel", FontAwesomeIcon.Times))
            {
                plugin.Configuration.SetDescription(id, details, descriptionOriginalValue);
                descriptionEditing = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancel — restore the previous value");

            return;
        }

        if (!string.IsNullOrWhiteSpace(details.Description))
            ImGui.TextWrapped(details.Description);
        else
            ImGui.TextDisabled("This design has no description set.");

        if (ImGuiComponents.IconButton("descEdit", FontAwesomeIcon.Pen))
        {
            descriptionOriginalValue = details.Description;
            descriptionEditBuffer = details.Description ?? string.Empty;
            descriptionEditing = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Edit description");

        if (details.Source == DesignSource.Glamourer)
        {
            ImGui.SameLine();
            var hasGlamourerDescription = !string.IsNullOrWhiteSpace(details.GlamourerDescription);
            if (ImGuiComponents.IconButton("pullDescription", FontAwesomeIcon.Sync) && hasGlamourerDescription)
                ConfirmDialog.Open(PullDescriptionPopupId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(hasGlamourerDescription
                    ? "Replace the description above with the one currently set in Glamourer"
                    : "Glamourer has no description set for this design");

            if (ConfirmDialog.Draw(PullDescriptionPopupId,
                    $"This will replace your saved description for \"{details.Name}\" with the one currently "
                    + "set on the design in Glamourer. This can't be undone.",
                    "Pull Description"))
            {
                plugin.Configuration.PullDescriptionFromGlamourer(id, details);
            }
        }
    }

    private static void DrawSubheader(string label, string? helpText = null)
    {
        // Mirrors Pills.DrawCollapsibleSubheader's framed look but is static (no chevron, no toggle).
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();

        var avail = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var rectH = lineH + style.FramePadding.Y * 2f;

        var rectMin = ImGui.GetCursorScreenPos();
        var rectMax = new Vector2(rectMin.X + avail, rectMin.Y + rectH);

        ImGui.Dummy(new Vector2(avail, rectH));
        draw.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(ImGuiCol.Header), style.FrameRounding);

        Pills.DrawSubheaderChrome(rectMin, rectMax, label, helpText);
    }

    private static bool DrawImageScaled(string absolutePath, float maxSide, bool clickable = false, string? title = null)
    {
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading image...");
            return false;
        }

        var (size, _) = GalleryDraw.ComputeFitSize(new Vector2(maxSide, maxSide), tex.Width, tex.Height);
        ImGui.Image(tex.Handle, size);

        if (!clickable)
            return false;

        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (title != null)
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(UiTheme.GoldAccent, title);
                ImGui.TextUnformatted("Left-click to view full size");
                ImGui.TextUnformatted("Shift + right-click to remove");
                ImGui.EndTooltip();
            }
            else
            {
                ImGui.SetTooltip("Left-click to view full size\nShift + right-click to remove");
            }
        }
        // Fire on release rather than press so grabbing the cover to drag it doesn't also open the viewer.
        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }

    private static bool DrawSquareThumbnail(string absolutePath, float size, out bool deleteRequested)
    {
        deleteRequested = false;
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return false;
        }

        float uMin = 0f, uMax = 1f, vMin = 0f, vMax = 1f;
        if (tex.Width > tex.Height)
        {
            var keep = tex.Height / (float)tex.Width;
            uMin = (1f - keep) * 0.5f;
            uMax = 1f - uMin;
        }
        else if (tex.Height > tex.Width)
        {
            var keep = tex.Width / (float)tex.Height;
            vMin = (1f - keep) * 0.5f;
            vMax = 1f - vMin;
        }

        ImGui.Image(tex.Handle, new Vector2(size, size), new Vector2(uMin, vMin), new Vector2(uMax, vMax));

        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Left-click to view full size\nShift + right-click to remove");
        }

        // Fire on release rather than press so grabbing the thumbnail to drag it doesn't also open the viewer.
        var leftClicked = hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
            deleteRequested = true;
        return leftClicked;
    }
}
