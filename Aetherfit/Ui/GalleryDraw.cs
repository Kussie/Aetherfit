using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;

namespace Aetherfit.Ui;

// The parts of a thumbnail cell that the local and shared galleries draw identically. Anything the two do
// differently (favourite stars, click handling, etc.) stays in their own windows.
internal static class GalleryDraw
{
    private const float ThumbRounding = 4f;

    // Draws the thumbnail in whichever fit mode is set. Leaves the image (or, for letterbox, the hit-button) as the
    // last item so the caller can check IsItemHovered() right after.
    public static void DrawFittedImage(IDalamudTextureWrap tex, Vector2 thumbStart, Vector2 thumbVec,
        float thumbWidth, float thumbHeight, float containerAspect, GalleryFitMode fitMode)
    {
        switch (fitMode)
        {
            case GalleryFitMode.Letterbox:
            {
                // Letterbox the image to fit, painting bars in the placeholder colour.
                ImGui.InvisibleButton("##cellHit", thumbVec);
                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(thumbStart, thumbStart + thumbVec,
                    ImGui.ColorConvertFloat4ToU32(UiTheme.PlaceholderBg), ThumbRounding);

                var scale = Math.Min(thumbWidth / tex.Width, thumbHeight / tex.Height);
                var fitted = new Vector2(tex.Width * scale, tex.Height * scale);
                var offset = (thumbVec - fitted) * 0.5f;
                dl.AddImage(tex.Handle, thumbStart + offset, thumbStart + offset + fitted);
                break;
            }
            case GalleryFitMode.Stretch:
                // Distort the image to fill the entire cell; aspect ratio is not preserved.
                ImGui.Image(tex.Handle, thumbVec);
                break;
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

    // The grey "No Image" box. Invisible button stays the last item so IsItemHovered() still works afterwards.
    public static void DrawNoImagePlaceholder(Vector2 thumbStart, Vector2 thumbVec)
    {
        ImGui.InvisibleButton("##placeholder", thumbVec);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(thumbStart, thumbStart + thumbVec,
            ImGui.ColorConvertFloat4ToU32(UiTheme.PlaceholderBg), ThumbRounding);
        const string text = "No Image";
        var textSize = ImGui.CalcTextSize(text);
        var textPos = thumbStart + (thumbVec - textSize) * 0.5f;
        dl.AddText(textPos, ImGui.ColorConvertFloat4ToU32(UiTheme.PlaceholderText), text);
    }

    // Which carousel arrow (if any) the mouse is over, for a hovered cell.
    public readonly record struct ArrowHover(bool OverLeft, bool OverRight);

    // Computes the left/right arrow hit-zones for a thumbnail, draws the chevrons when the cell is hovered,
    // and reports which arrow the mouse is over. Shared by the local and shared gallery cells.
    public static ArrowHover DrawArrows(Vector2 thumbStart, float thumbWidth, float thumbHeight,
        bool hasArrows, bool canPrev, bool canNext, Vector2 mouse, bool hovered)
    {
        if (!hovered || !hasArrows)
            return default;

        var arrowZone = Math.Min(28f * ImGuiHelpers.GlobalScale, Math.Min(thumbWidth, thumbHeight) * 0.32f);
        var arrowMargin = 4f * ImGuiHelpers.GlobalScale;
        var leftMin = new Vector2(thumbStart.X + arrowMargin, thumbStart.Y + (thumbHeight - arrowZone) * 0.5f);
        var leftMax = new Vector2(leftMin.X + arrowZone, leftMin.Y + arrowZone);
        var rightMax = new Vector2(thumbStart.X + thumbWidth - arrowMargin, thumbStart.Y + (thumbHeight + arrowZone) * 0.5f);
        var rightMin = new Vector2(rightMax.X - arrowZone, rightMax.Y - arrowZone);

        var overLeft = canPrev
                       && mouse.X >= leftMin.X && mouse.X <= leftMax.X
                       && mouse.Y >= leftMin.Y && mouse.Y <= leftMax.Y;
        var overRight = canNext
                        && mouse.X >= rightMin.X && mouse.X <= rightMax.X
                        && mouse.Y >= rightMin.Y && mouse.Y <= rightMax.Y;

        var dl = ImGui.GetWindowDrawList();
        if (canPrev) DrawChevron(dl, leftMin, leftMax, isLeft: true, hovered: overLeft);
        if (canNext) DrawChevron(dl, rightMin, rightMax, isLeft: false, hovered: overRight);

        return new ArrowHover(overLeft, overRight);
    }

    public static void DrawChevron(ImDrawListPtr dl, Vector2 min, Vector2 max, bool isLeft, bool hovered)
    {
        var bgAlpha = hovered ? 0.85f : 0.55f;
        var bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, bgAlpha));
        dl.AddRectFilled(min, max, bg, ThumbRounding);

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

    // The cell's carousel images: cover first (when present), then the additional shots.
    public static List<string> BuildImageList(string? cover, IReadOnlyList<string> additional)
    {
        var images = new List<string>(additional.Count + 1);
        if (cover != null)
            images.Add(cover);
        images.AddRange(additional);
        return images;
    }

    // Which carousel image a cell is showing, snapping back to the first one if the saved index no longer fits.
    public static int ResolveImageIndex(Dictionary<Guid, int> indices, Guid id, int imageCount)
    {
        if (!indices.TryGetValue(id, out var idx) || idx < 0 || idx >= imageCount)
        {
            idx = 0;
            indices[id] = 0;
        }
        return idx;
    }
}
