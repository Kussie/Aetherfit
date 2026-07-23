using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services;
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

    private void DrawLeftPane()
    {
        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, "Glamourer Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("Gallery Mode >>", new Vector2(-1, 0)))
            coverMode = true;
        ImGui.Separator();

        if (designsError != null)
        {
            ImGui.TextWrapped("Glamourer is not available. Make sure it is installed and enabled.");
            ImGui.TextDisabled(designsError);
            return;
        }

        if (designsCount == 0)
        {
            ImGui.Text("No Glamourer designs found.");
            return;
        }

        // The two groupings are mutually exclusive - ticking one unticks the other (both can be off).
        if (ImGui.Checkbox("Group by job association", ref groupByJob) && groupByJob)
            groupByTags = false;
        if (ImGui.Checkbox("Group by tags", ref groupByTags) && groupByTags)
            groupByJob = false;
        ImGui.Spacing();

        DrawFilterUi();
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
            || !FiltersEqual(filterJobs, filterJobsSnapshot);
        if (hasFilter && filterChanged)
            expandTreesForFilter = true;
        if (filterChanged)
        {
            filterSnapshot = snapshot;
            filterTagsSnapshot = new(filterTags, StringComparer.OrdinalIgnoreCase);
            filterJobsSnapshot = new(filterJobs);
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
            else
                DrawTree(root, hasFilter);
        }

        // Tree's drawn, so we're done with the one-shot expand request - clear it for next frame.
        expandTreesForFilter = false;

        if (hoveredDesignForTooltip is { } hovered)
            DrawDesignLeafTooltip(hovered);
    }

    private void DrawTree(FolderNode node, bool hasFilter, int depth = 0)
    {
        foreach (var (name, folder) in node.Folders)
        {
            if (!FolderHasMatch(folder)) continue;

            ForceOpenIfFiltering(name, hasFilter);

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
            var rowX = ImGui.GetCursorScreenPos().X;
            DrawDesignLeaf(design);
            DrawTreeItemTick(depth, rowX);
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
    private readonly Dictionary<Guid, (bool IsFavourite, string Name, string Label)> leafLabelCache = new();

    private string GetLeafLabel(DesignLeaf design, bool isFavourite)
    {
        if (leafLabelCache.TryGetValue(design.Id, out var cached)
            && cached.IsFavourite == isFavourite && cached.Name == design.DisplayName)
            return cached.Label;

        var label = isFavourite
            ? $"★ {design.DisplayName}##{design.Id}"
            : $"   {design.DisplayName}##{design.Id}";
        leafLabelCache[design.Id] = (isFavourite, design.DisplayName, label);
        return label;
    }

    private void DrawDesignLeaf(DesignLeaf design)
    {
        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);
        var hasColor = design.Color != 0;
        if (hasColor)
            ImGui.PushStyleColor(ImGuiCol.Text, design.Color);

        var selected = selectedDesign == design.Id;

        var label = GetLeafLabel(design, isFavourite);
        if (ImGui.Selectable(label, selected))
            selectedDesign = design.Id;

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
            plugin.Glamourer.OpenInGlamourer(design.Id, design.DisplayName);
        }

        if (hasColor)
            ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            hoveredDesignForTooltip = design;
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
        var iconPath = iconDir != null ? Path.Combine(iconDir, "icon.png") : null;
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

        var datesLineCount = (details.CreatedAt.HasValue ? 1 : 0) + (details.LastEdit.HasValue ? 1 : 0);
        var datesBlockHeight = datesLineCount > 0
            ? datesLineCount * ImGui.GetTextLineHeightWithSpacing()
            : 0;

        var bodyHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - datesBlockHeight);

        using (var body = ImRaii.Child("DesignBody", new Vector2(0, bodyHeight), false))
        {
            if (body.Success)
            {
                var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(id);
                var isHidden = plugin.Configuration.HiddenDesigns.Contains(id);
                var style = ImGui.GetStyle();
                var inner = style.ItemInnerSpacing.X;

                // Measure the action cluster first so the title can be ellipsized to the space that remains.
                var frameH = ImGui.GetFrameHeight();
                float starW, eyeW, linkW;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    starW = ImGui.CalcTextSize(FontAwesomeIcon.Star.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    eyeW = ImGui.CalcTextSize((isHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString()).X
                         + (style.FramePadding.X * 2);
                    linkW = ImGui.CalcTextSize(FontAwesomeIcon.ExternalLinkAlt.ToIconString()).X
                          + (style.FramePadding.X * 2);
                }
                var actionsW = starW + eyeW + linkW + (inner * 3);

                var rowTopY = ImGui.GetCursorPosY();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (6f * ImGuiHelpers.GlobalScale));
                ImGui.SetWindowFontScale(1.5f);
                var titleAvail = Math.Max(50f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X - actionsW);
                var title = TextFit.Ellipsize(details.Name, titleAvail);
                var titleH = ImGui.GetTextLineHeight();
                ImGui.TextColored(UiTheme.GoldAccent, title);
                ImGui.SetWindowFontScale(1.0f);
                if (title != details.Name && ImGui.IsItemHovered())
                    ImGui.SetTooltip(details.Name);

                var actionY = rowTopY + Math.Max(0f, (titleH - frameH) * 0.5f);

                ImGui.SameLine(0, inner);
                ImGui.SetCursorPosY(actionY);
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
                ImGui.SetCursorPosY(actionY);
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
                ImGui.SetCursorPosY(actionY);
                if (HeaderIconButton("openGlamourer", FontAwesomeIcon.ExternalLinkAlt, null, new Vector2(linkW, frameH)))
                    plugin.Glamourer.OpenInGlamourer(id, details.Name);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Open in Glamourer");

                ImGui.Spacing();

                DrawJobAssociations(id);

                if (DrawCollapsibleSubheader("Tags", ref tagsPanelOpen))
                {
                    ImGui.Indent();
                    if (details.Tags.Count > 0)
                    {
                        for (var i = 0; i < details.Tags.Count; i++)
                        {
                            if (i > 0) ImGui.SameLine();
                            var tag = details.Tags[i];
                            DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, tag);
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                                ImGui.SetTooltip($"Show all designs tagged \"{tag}\"");
                                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                                    filterTags[tag] = true;
                            }
                        }
                    }
                    else
                    {
                        DrawSetInGlamourerNotice(
                            "This design has no tags yet — tags are added to the design in Glamourer.",
                            id, details.Name);
                    }
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (DrawCollapsibleSubheader("Description", ref descriptionPanelOpen))
                {
                    ImGui.Indent();
                    if (!string.IsNullOrWhiteSpace(details.Description))
                        ImGui.TextWrapped(details.Description);
                    else
                        DrawSetInGlamourerNotice(
                            "This design has no description yet — the description is edited on the design in Glamourer.",
                            id, details.Name);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (DrawCollapsibleSubheader("Images", ref imagesPanelOpen, ImageHelpText))
                {
                    ImGui.Indent();
                    DrawImagesBlock(id);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                DrawEquipmentPanel(id, details);
                DrawCustomizationsPanel(id, details);
                DrawDesignLinksPanel(details);
                DrawModsPanel(details);
                if (plugin.Configuration.EnableRandomLayers)
                    DrawAdditionalLayersPanel(id);
            }
        }

        // Nudge the floating footer in one level so the dates line up with the indented content above.
        if (details.CreatedAt is not null || details.LastEdit is not null)
        {
            ImGui.Indent();
            if (details.CreatedAt is { } created)
                DrawDateLine("Created", created);
            if (details.LastEdit is { } edited)
                DrawDateLine("Last edited", edited);
            ImGui.Unindent();
        }
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

        // Fill the space to the right of the cover with a compact grid of thumbnails (the two add tiles
        // trailing the images). Beside the cover the cells flow top-to-bottom down to about the cover's
        // height before starting a new column, so the wall stays as narrow as possible; when the pane is
        // too narrow to sit beside the cover, the grid drops below it and wraps by width instead. Cells are
        // positioned absolutely so each row/column lines up with the grid rather than the window margin.
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
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.GhostButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.GhostButtonActive);
        if (tint is { } t)
            ImGui.PushStyleColor(ImGuiCol.Text, t);
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
        ImGui.PopStyleColor(tint.HasValue ? 4 : 3);
        return clicked;
    }

    // A square tile with an icon over a label, framed so the add/snap actions read as buttons
    // sitting next to the image thumbnails.
    private static bool DrawImageActionTile(string id, FontAwesomeIcon icon, string label, string tooltip, float size)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, ImGuiHelpers.GlobalScale);
        ImGui.PushStyleColor(ImGuiCol.Button, UiTheme.PlaceholderBg);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.65f, 0.65f, 0.68f, 0.45f));
        var clicked = ImGui.Button($"##imgTile{id}", new Vector2(size, size));
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);

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

    private static void DrawDateLine(string label, DateTimeOffset dt)
    {
        ImGui.TextDisabled($"{label}: {FormatFriendlyRelative(dt)}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FormatFullDate(dt));
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

    // Empty-state line for fields that live on the Glamourer design (tags, description): explains
    // where they are edited and links straight to the design in Glamourer.
    private void DrawSetInGlamourerNotice(string message, Guid id, string designName)
    {
        ImGui.TextDisabled(message);
        DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, "Open this design in Glamourer");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip($"Open \"{designName}\" in Glamourer");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                plugin.Glamourer.OpenInGlamourer(id, designName);
        }
    }

    private static void DrawSubheader(string label, string? helpText = null)
    {
        // Mirrors DrawCollapsibleSubheader's framed look but is static (no chevron, no toggle).
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();

        var avail = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var rectH = lineH + style.FramePadding.Y * 2f;

        var rectMin = ImGui.GetCursorScreenPos();
        var rectMax = new Vector2(rectMin.X + avail, rectMin.Y + rectH);

        ImGui.Dummy(new Vector2(avail, rectH));
        draw.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(ImGuiCol.Header), style.FrameRounding);

        DrawSubheaderChrome(rectMin, rectMax, label, helpText);
    }

    private static bool DrawImageScaled(string absolutePath, float maxSide, bool clickable = false, string? title = null)
    {
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading image...");
            return false;
        }

        var scale = Math.Min(maxSide / tex.Width, maxSide / tex.Height);
        ImGui.Image(tex.Handle, new Vector2(tex.Width * scale, tex.Height * scale));

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
