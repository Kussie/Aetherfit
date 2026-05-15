using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private enum GallerySortField { Name, LastModified, Created }

    private const int CoverColumns = 4;
    private const float CoverMinThumbSize = 96f;
    // Portrait aspect to suit character screenshots.
    private const float CoverAspectRatio = 3f / 2f;

    private bool coverMode;
    private readonly Dictionary<Guid, int> galleryImageIndex = new();
    private GallerySortField gallerySortField = GallerySortField.Name;
    private bool gallerySortAscending = true;

    private void DrawCoverModePane()
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), "Glamourer Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("<< Edit Mode", new Vector2(-1, 0)))
            coverMode = false;

        if (ImGui.Button("Refresh"))
            RefreshDesigns();
        ImGui.SameLine();
        ImGui.TextDisabled($"{designsCount} design(s)");

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

        DrawFilterUi(defaultOpen: true);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGallerySortControls();
        ImGui.Spacing();

        using var gridChild = ImRaii.Child("CoverGridScroll", Vector2.Zero, false);
        if (gridChild.Success)
            DrawCoverGrid();
    }

    private void DrawGallerySortControls()
    {
        ImGui.TextDisabled("Sort by:");
        ImGui.SameLine();

        var dirLabel = gallerySortAscending ? "Asc" : "Desc";
        var dirWidth = ImGui.CalcTextSize(dirLabel).X + ImGui.GetStyle().FramePadding.X * 2 + 8 * ImGuiHelpers.GlobalScale;
        var comboWidth = Math.Max(120f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().X - dirWidth - ImGui.GetStyle().ItemSpacing.X);

        ImGui.PushItemWidth(comboWidth);
        var fieldIdx = (int)gallerySortField;
        var fieldOptions = new[] { "Name (alphabetical)", "Last modified", "Created" };
        if (ImGui.Combo("##gallerySortField", ref fieldIdx, fieldOptions, fieldOptions.Length))
            gallerySortField = (GallerySortField)fieldIdx;
        ImGui.PopItemWidth();

        ImGui.SameLine();
        if (ImGui.Button($"{dirLabel}##gallerySortDir", new Vector2(dirWidth, 0)))
            gallerySortAscending = !gallerySortAscending;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(gallerySortAscending ? "Ascending — click to switch to descending" : "Descending — click to switch to ascending");
    }

    private void DrawCoverGrid()
    {
        var visible = new List<DesignLeaf>();
        CollectVisibleDesigns(root, visible);
        SortGalleryDesigns(visible);

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No designs match the current filters.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var thumbWidth = Math.Max(CoverMinThumbSize, (avail - (CoverColumns - 1) * spacing) / CoverColumns);
        var thumbHeight = thumbWidth * CoverAspectRatio;

        for (var i = 0; i < visible.Count; i++)
        {
            if (i % CoverColumns != 0)
                ImGui.SameLine();
            DrawCoverCell(visible[i], thumbWidth, thumbHeight);
        }
    }

    private void SortGalleryDesigns(List<DesignLeaf> designs)
    {
        var asc = gallerySortAscending;
        switch (gallerySortField)
        {
            case GallerySortField.Name:
                designs.Sort((a, b) =>
                {
                    var cmp = NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
                    return asc ? cmp : -cmp;
                });
                break;
            case GallerySortField.LastModified:
                designs.Sort((a, b) => CompareDates(GetLastEdit(a.Id), GetLastEdit(b.Id), asc));
                break;
            case GallerySortField.Created:
                designs.Sort((a, b) => CompareDates(GetCreatedAt(a.Id), GetCreatedAt(b.Id), asc));
                break;
        }
    }

    private DateTimeOffset? GetLastEdit(Guid id) =>
        plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.LastEdit : null;

    private DateTimeOffset? GetCreatedAt(Guid id) =>
        plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.CreatedAt : null;

    // Missing dates always sink to the bottom, regardless of direction.
    private static int CompareDates(DateTimeOffset? a, DateTimeOffset? b, bool ascending)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        var cmp = a.Value.CompareTo(b.Value);
        return ascending ? cmp : -cmp;
    }

    private void DrawCoverCell(DesignLeaf design, float thumbWidth, float thumbHeight)
    {
        using var id = ImRaii.PushId(design.Id.ToString());
        using var group = ImRaii.Group();

        var thumbStart = ImGui.GetCursorScreenPos();
        var thumbVec = new Vector2(thumbWidth, thumbHeight);
        var containerAspect = thumbWidth / thumbHeight;

        var coverPath = plugin.ImageStorage.GetCoverPath(design.Id);
        var additionalPaths = plugin.ImageStorage.GetAdditionalPaths(design.Id);
        var images = new List<string>();
        if (coverPath != null) images.Add(coverPath);
        images.AddRange(additionalPaths);

        if (!galleryImageIndex.TryGetValue(design.Id, out var imgIdx) || imgIdx < 0 || imgIdx >= images.Count)
        {
            imgIdx = 0;
            galleryImageIndex[design.Id] = 0;
        }
        var currentImage = images.Count > 0 ? images[imgIdx] : null;

        var clicked = false;
        var shiftClicked = false;
        var doubleClicked = false;

        if (currentImage != null)
        {
            var tex = Plugin.TextureProvider.GetFromFile(currentImage).GetWrapOrEmpty();
            if (tex.Width > 0 && tex.Height > 0)
            {
                switch (plugin.Configuration.GalleryFitMode)
                {
                    case GalleryFitMode.Letterbox:
                    {
                        // Reserve the full cell footprint via InvisibleButton so hover/hit testing
                        // matches the cell, then draw a grey background for the bars and the image
                        // at its aspect-preserving fitted size via the draw list.
                        ImGui.InvisibleButton("##cellHit", thumbVec);
                        var dl = ImGui.GetWindowDrawList();
                        var bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f, 0.22f, 0.25f, 1f));
                        dl.AddRectFilled(thumbStart, thumbStart + thumbVec, bg, 4f);

                        var scale = Math.Min(thumbWidth / tex.Width, thumbHeight / tex.Height);
                        var fitted = new Vector2(tex.Width * scale, tex.Height * scale);
                        var offset = (thumbVec - fitted) * 0.5f;
                        dl.AddImage(tex.Handle, thumbStart + offset, thumbStart + offset + fitted);
                        break;
                    }
                    case GalleryFitMode.Stretch:
                    {
                        // Distort the image to fill the entire cell; aspect ratio is not preserved.
                        ImGui.Image(tex.Handle, thumbVec);
                        break;
                    }
                    default:
                    {
                        // Crop: preserve aspect ratio, trim the overflow via UVs.
                        float uMin = 0f, uMax = 1f, vMin = 0f, vMax = 1f;
                        var texAspect = tex.Width / (float)tex.Height;
                        if (texAspect > containerAspect)
                        {
                            var keep = containerAspect / texAspect;
                            uMin = (1f - keep) * 0.5f;
                            uMax = 1f - uMin;
                        }
                        else if (texAspect < containerAspect)
                        {
                            var keep = texAspect / containerAspect;
                            vMin = (1f - keep) * 0.5f;
                            vMax = 1f - vMin;
                        }
                        ImGui.Image(tex.Handle, thumbVec, new Vector2(uMin, vMin), new Vector2(uMax, vMax));
                        break;
                    }
                }
            }
            else
            {
                ImGui.Dummy(thumbVec);
            }
        }
        else
        {
            ImGui.InvisibleButton("##placeholder", thumbVec);
            var dl = ImGui.GetWindowDrawList();
            var fill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f, 0.22f, 0.25f, 1f));
            dl.AddRectFilled(thumbStart, thumbStart + thumbVec, fill, 4f);
            const string text = "No Image";
            var textSize = ImGui.CalcTextSize(text);
            var textPos = thumbStart + (thumbVec - textSize) * 0.5f;
            dl.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.65f, 0.65f, 0.68f, 1f)), text);
        }

        var imageHovered = ImGui.IsItemHovered();

        var hasArrows = additionalPaths.Count > 0;
        var canPrev = imgIdx > 0;
        var canNext = imgIdx < images.Count - 1;

        var arrowZone = Math.Min(28f * ImGuiHelpers.GlobalScale, Math.Min(thumbWidth, thumbHeight) * 0.32f);
        var arrowMargin = 4f * ImGuiHelpers.GlobalScale;
        var leftMin = new Vector2(thumbStart.X + arrowMargin, thumbStart.Y + (thumbHeight - arrowZone) * 0.5f);
        var leftMax = new Vector2(leftMin.X + arrowZone, leftMin.Y + arrowZone);
        var rightMax = new Vector2(thumbStart.X + thumbWidth - arrowMargin, thumbStart.Y + (thumbHeight + arrowZone) * 0.5f);
        var rightMin = new Vector2(rightMax.X - arrowZone, rightMax.Y - arrowZone);

        var mouse = ImGui.GetIO().MousePos;
        var overLeft = imageHovered && hasArrows && canPrev
                       && mouse.X >= leftMin.X && mouse.X <= leftMax.X
                       && mouse.Y >= leftMin.Y && mouse.Y <= leftMax.Y;
        var overRight = imageHovered && hasArrows && canNext
                        && mouse.X >= rightMin.X && mouse.X <= rightMax.X
                        && mouse.Y >= rightMin.Y && mouse.Y <= rightMax.Y;

        if (imageHovered && hasArrows)
        {
            var dl = ImGui.GetWindowDrawList();
            if (canPrev) DrawGalleryChevron(dl, leftMin, leftMax, isLeft: true, hovered: overLeft);
            if (canNext) DrawGalleryChevron(dl, rightMin, rightMax, isLeft: false, hovered: overRight);
        }

        if (imageHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (overLeft)
                    galleryImageIndex[design.Id] = imgIdx - 1;
                else if (overRight)
                    galleryImageIndex[design.Id] = imgIdx + 1;
                else if (ImGui.GetIO().KeyShift)
                    shiftClicked = true;
                else
                    clicked = true;
            }
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !overLeft && !overRight)
                doubleClicked = true;
            if (!overLeft && !overRight)
                DrawCoverCellTooltip(design);
        }

        if (selectedDesign == design.Id)
        {
            var dl = ImGui.GetWindowDrawList();
            var hl = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.85f, 0.4f, 1f));
            dl.AddRect(thumbStart, thumbStart + thumbVec, hl, 4f, ImDrawFlags.None, 2.5f);
        }

        var label = design.DisplayName;
        var labelWidth = ImGui.CalcTextSize(label).X;
        var indent = Math.Max(0f, (thumbWidth - labelWidth) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        var hasColor = design.Color != 0;
        if (hasColor)
            ImGui.PushStyleColor(ImGuiCol.Text, design.Color);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + thumbWidth);
        ImGui.TextUnformatted(label);
        ImGui.PopTextWrapPos();
        if (hasColor)
            ImGui.PopStyleColor();

        if (clicked)
            selectedDesign = design.Id;
        if (shiftClicked)
        {
            selectedDesign = design.Id;
            coverMode = false;
        }
        if (doubleClicked)
        {
            selectedDesign = design.Id;
            ApplyDesignById(design.Id);
        }
    }

    private static void DrawGalleryChevron(ImDrawListPtr dl, Vector2 min, Vector2 max, bool isLeft, bool hovered)
    {
        var bgAlpha = hovered ? 0.85f : 0.55f;
        var bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, bgAlpha));
        dl.AddRectFilled(min, max, bg, 4f);

        var center = (min + max) * 0.5f;
        var size = Math.Min(max.X - min.X, max.Y - min.Y);
        var halfH = size * 0.25f;
        var halfW = size * 0.18f;
        var color = ImGui.ColorConvertFloat4ToU32(Vector4.One);
        if (isLeft)
        {
            dl.AddTriangleFilled(
                new Vector2(center.X - halfW, center.Y),
                new Vector2(center.X + halfW, center.Y + halfH),
                new Vector2(center.X + halfW, center.Y - halfH),
                color);
        }
        else
        {
            dl.AddTriangleFilled(
                new Vector2(center.X + halfW, center.Y),
                new Vector2(center.X - halfW, center.Y - halfH),
                new Vector2(center.X - halfW, center.Y + halfH),
                color);
        }
    }

    private void DrawCoverCellTooltip(DesignLeaf design)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(design.DisplayName);
        if (!string.IsNullOrEmpty(design.FullPath))
            ImGui.TextDisabled(design.FullPath);
        ImGui.TextDisabled("Double-click to apply");
        ImGui.TextDisabled("Shift+click to open in edit view");
        ImGui.EndTooltip();
    }
}
