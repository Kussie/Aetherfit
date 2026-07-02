using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
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
    private const float AdditionalThumbSize = 72f;
    private const string ImageHelpText =
        "Click an image to view it full size. Hold Shift and right-click to remove. \"+\" picks a file; \"Snap\" captures from the game.";

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

        ImGui.Checkbox("Group by job association", ref groupByJob);
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
        var signature = FilterSignature;
        if (hasFilter && signature != filterSignature)
            expandTreesForFilter = true;
        filterSignature = signature;

        // Widen the vertical gap between rows so the mouse rarely sits on the seam between two items and reports both as hovered in the same frame.
        var spacing = ImGui.GetStyle().ItemSpacing;
        hoveredDesignForTooltip = null;
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, spacing.Y + 3)))
        {
            if (groupByJob)
                DrawJobTree(hasFilter);
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

            // Capture the row's logical left edge before drawing; a Selectable's item rect extends half an
            // ItemSpacing to the left of this, so we can't rely on GetItemRectMin for the tick's anchor.
            var rowX = ImGui.GetCursorScreenPos().X;
            var open = ImGui.TreeNodeEx(name, ImGuiTreeNodeFlags.SpanAvailWidth);
            // Connect this node to its parent's vertical guide with a short horizontal tick.
            DrawTreeItemTick(depth, rowX);

            if (open)
            {
                // Glamourer-style indent guide: a faint vertical line down the left of this folder's
                // children. We capture the top before drawing them and the bottom after, then draw the
                // line between - drawing happens after the children so its extent is known.
                var drawList = ImGui.GetWindowDrawList();
                // Line the guide up under the tip of this folder's expand arrow.
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

    // Horizontal distance from a tree node's left edge to the centre of its expand arrow, matching how
    // ImGui positions the arrow (FramePadding then a font-sized glyph box). The guides line up under it.
    private static float TreeArrowCenterOffset()
        => ImGui.GetStyle().FramePadding.X + (ImGui.GetFontSize() * 0.5f);

    // A short horizontal line from the parent folder's vertical guide to the item, at the item's vertical
    // centre. Only items nested under a folder (depth > 0) have a parent guide to connect to.
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

    private void DrawDesignLeaf(DesignLeaf design)
    {
        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);
        var hasColor = design.Color != 0;
        if (hasColor)
            ImGui.PushStyleColor(ImGuiCol.Text, design.Color);

        var selected = selectedDesign == design.Id;
        // Favourites keep the star glyph; other designs get a hand-drawn dot (after the Selectable) so we
        // can size it precisely. Either way leave a little room at the start of the label for the marker.
        var label = isFavourite
            ? $"★ {design.DisplayName}##{design.Id}"
            : $"   {design.DisplayName}##{design.Id}";
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
                var rowTopY = ImGui.GetCursorPosY();
                ImGui.SetWindowFontScale(1.5f);
                ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.GhostButtonHovered);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.GhostButtonActive);
                ImGui.PushStyleColor(ImGuiCol.Text, isFavourite
                    ? UiTheme.FavouriteStar
                    : UiTheme.FavouriteButtonOff);
                if (ImGui.Button(isFavourite ? "★##favStar" : "☆##favStar"))
                {
                    if (isFavourite)
                        plugin.Configuration.FavouriteDesigns.Remove(id);
                    else
                        plugin.Configuration.FavouriteDesigns.Add(id);
                    plugin.Configuration.Save();
                    favouriteVersion++;
                }
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isFavourite ? "Click to remove from favourites" : "Click to add to favourites");
                var rowHeight = ImGui.GetItemRectSize().Y;

                // Hidden toggle, sitting right next to the favourite star. The filled eye glyph reads larger than the
                // thin star outline at the same scale, so render it at 1.0. Give the button the star's full row height
                // and let ImGui vertically centre the glyph within it - that lines the eye up with the star exactly,
                // with no font-metric guesswork.
                var isHidden = plugin.Configuration.HiddenDesigns.Contains(id);
                ImGui.SameLine();
                ImGui.SetWindowFontScale(1.0f);
                ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.GhostButtonHovered);
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.GhostButtonActive);
                ImGui.PushStyleColor(ImGuiCol.Text, isHidden ? UiTheme.HiddenEyeOn : UiTheme.HiddenButtonOff);
                bool eyeClicked;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    var eyeGlyph = (isHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString();
                    var eyeWidth = ImGui.CalcTextSize(eyeGlyph).X + (ImGui.GetStyle().FramePadding.X * 2);
                    // The eye glyph's visual centre sits slightly above its line-box centre, so frame-centring leaves
                    // it a touch high - nudge the button down a hair to bring it level with the star.
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (2.25f * ImGuiHelpers.GlobalScale));
                    eyeClicked = ImGui.Button(eyeGlyph + "##hideEye", new Vector2(eyeWidth, rowHeight));
                }
                if (eyeClicked)
                {
                    if (isHidden)
                        plugin.Configuration.HiddenDesigns.Remove(id);
                    else
                        plugin.Configuration.HiddenDesigns.Add(id);
                    plugin.Configuration.Save();
                    hiddenVersion++;
                }
                ImGui.PopStyleColor(4);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isHidden
                        ? "Hidden — click to show in the gallery and exports"
                        : "Click to hide from the gallery and exports");

                ImGui.SameLine();
                ImGui.SetWindowFontScale(1.5f);
                ImGui.SetCursorPosY(rowTopY + ((rowHeight - ImGui.GetTextLineHeight()) * 0.5f));
                ImGui.TextColored(UiTheme.GoldAccent, details.Name);
                ImGui.SetWindowFontScale(1.0f);

                ImGui.SameLine();
                ImGui.SetCursorPosY(rowTopY + ((rowHeight - ImGui.GetFrameHeight()) * 0.5f));
                if (ImGuiComponents.IconButton(FontAwesomeIcon.ExternalLinkAlt))
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
                            ImGui.TextColored(UiTheme.ModLink, details.Tags[i]);
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

                if (DrawCollapsibleSubheader("Cover Image", ref coverImagePanelOpen, ImageHelpText))
                {
                    ImGui.Indent();
                    DrawOutfitImageBlock(id);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (DrawCollapsibleSubheader("Additional Images", ref additionalImagesPanelOpen, ImageHelpText))
                {
                    ImGui.Indent();
                    DrawAdditionalImagesBlock(id);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                DrawEquipmentPanel(id, details);
                DrawCustomizationsPanel(id, details);
                DrawDesignLinksPanel(details);
                if (plugin.Configuration.EnableRandomLayers)
                    DrawAdditionalLayersPanel(id);
                DrawModsPanel(details);
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

    private void DrawOutfitImageBlock(Guid id)
    {
        var imagePath = plugin.ImageStorage.GetCoverPath(id);
        var deleteRequested = false;
        if (imagePath != null)
        {
            if (DrawImageScaled(imagePath, RightPaneImageMax * ImGuiHelpers.GlobalScale, clickable: true))
                plugin.ImageViewer.Show(imagePath);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                deleteRequested = true;
        }
        else
        {
            ImGui.TextDisabled("No image set");
        }

        if (imagePath == null)
        {
            ImGui.Spacing();

            var thumb = AdditionalThumbSize * ImGuiHelpers.GlobalScale;
            if (ImGui.Button("+##cover", new Vector2(thumb, thumb)))
                OpenImagePicker(id);

            ImGui.SameLine();
            if (ImGui.Button("Snap##cover", new Vector2(thumb, thumb)))
                plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.SetCover(id, croppedPath));
        }

        if (deleteRequested)
            plugin.ImageStorage.RemoveCover(id);
    }

    private void DrawAdditionalImagesBlock(Guid id)
    {
        var paths = plugin.ImageStorage.GetAdditionalPaths(id);
        var thumb = AdditionalThumbSize * ImGuiHelpers.GlobalScale;
        var toRemoveIndex = -1;

        for (var i = 0; i < paths.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            using (ImRaii.PushId(i))
            {
                var clicked = DrawSquareThumbnail(paths[i], thumb, out var deleteRequested);
                if (clicked)
                    plugin.ImageViewer.Show(paths[i]);
                if (deleteRequested)
                    toRemoveIndex = i;
            }
        }

        if (paths.Count < ImageStorageService.MaxAdditionalImages)
        {
            if (paths.Count > 0)
                ImGui.SameLine();
            if (ImGui.Button("+", new Vector2(thumb, thumb)))
                OpenAdditionalImagePicker(id);

            ImGui.SameLine();
            if (ImGui.Button("Snap", new Vector2(thumb, thumb)))
                plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.AddAdditional(id, croppedPath));
        }

        if (toRemoveIndex >= 0)
            plugin.ImageStorage.RemoveAdditional(id, toRemoveIndex);
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

        var textY = rectMin.Y + (rectH - lineH) * 0.5f;
        draw.AddText(new Vector2(rectMin.X + style.FramePadding.X, textY),
            ImGui.GetColorU32(SectionHeader), label);

        if (helpText == null)
            return;

        const string marker = "(?)";
        var markerSize = ImGui.CalcTextSize(marker);
        var markerPos = new Vector2(rectMax.X - markerSize.X - style.FramePadding.X, textY);
        draw.AddText(markerPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), marker);

        var hoverMax = new Vector2(markerPos.X + markerSize.X, markerPos.Y + markerSize.Y);
        if (!ImGui.IsMouseHoveringRect(markerPos, hoverMax))
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static bool DrawImageScaled(string absolutePath, float maxSide, bool clickable = false)
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
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
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
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var leftClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
            deleteRequested = true;
        return leftClicked;
    }
}
