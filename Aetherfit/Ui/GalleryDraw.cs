using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace Aetherfit.Ui;

// Shared drawing primitives for the gallery thumbnail grids (local gallery and the read-only shared gallery).
// Only the pieces that are byte-for-byte identical between the two live here; per-window interaction stays put.
internal static class GalleryDraw
{
    private const float ThumbRounding = 4f;

    // Draws a texture into a thumbnail cell honouring the configured fit mode. The last submitted ImGui item is the
    // image (or the letterbox hit-button), so callers may use ImGui.IsItemHovered() immediately after.
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

    // Draws the "No Image" placeholder box. The invisible button is the last item, so IsItemHovered() works after.
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

    // Returns the current (clamped) carousel index for a cell, resetting to 0 when it is out of range.
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
