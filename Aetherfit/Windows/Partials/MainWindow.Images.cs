using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Screenshots;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// The design detail pane's Images section: cover + additional-image wall, drag/drop swapping between
// them, and the Browse/Snap action tiles that add new ones.
public partial class MainWindow
{
    private const float RightPaneImageMax = 220f;
    // Sized to about half the cover's long side so a portrait cover fits roughly two thumbnails per column.
    private const float AdditionalThumbSize = 104f;
    private const string ImageHelpText =
        "The first image you add becomes the cover, shown large above the rest. Drag a thumbnail onto the cover (or the cover onto a thumbnail) to swap which one is the cover. "
        + "Click an image to view it full size. Hold Shift and right-click to remove. \"Browse\" picks a file; \"Snap\" captures from the game.";

    private const string ImageDragType = "AF_IMAGE";
    private const string CoverDragType = "AF_COVER";
    private int draggedImageIndex = -1;

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
