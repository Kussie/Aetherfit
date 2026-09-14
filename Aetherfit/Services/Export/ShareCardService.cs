using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Aetherfit.Services.Export;

// Renders a single shareable PNG "card" for one design - cover, name, tags, description - sized for
// pasting straight into a chat message. Unlike LookBookExportService's multi-page contact sheet or
// GallerySharingService's bundle export, this carries no applyable design data, just a picture of it.
public sealed class ShareCardService
{
    private const int CardWidth = 820;
    private const int Margin = 36;
    private const int MinImageHeight = 200;
    private const int MaxImageHeight = 1000;
    private const int PlaceholderImageHeight = 420;
    private const int DescriptionMaxChars = 480;

    private static readonly Color BackgroundColor = Color.FromRgb(24, 24, 27);
    private static readonly Color PlaceholderColor = Color.FromRgb(56, 56, 61);
    private static readonly Color GoldAccent = Color.FromRgb(255, 217, 102);
    private static readonly Color GoldRule = Color.FromRgba(255, 217, 102, 120);
    private static readonly Color BodyTextColor = Color.FromRgb(214, 214, 218);
    private static readonly Color MutedTextColor = Color.FromRgb(158, 158, 165);

    private static readonly Font NameFont = ResolveFont("Georgia", "Segoe UI", 32, FontStyle.Bold);
    private static readonly Font TagsFont = ResolveFont("Segoe UI", "Verdana", 15, FontStyle.Italic);
    private static readonly Font BodyFont = ResolveFont("Segoe UI", "Verdana", 16, FontStyle.Regular);
    private static readonly Font FooterFont = ResolveFont("Segoe UI", "Verdana", 13, FontStyle.Regular);
    private static readonly Font PlaceholderFont = ResolveFont("Segoe UI", "Verdana", 16, FontStyle.Italic);

    private static Font ResolveFont(string preferred, string fallback, int size, FontStyle style)
    {
        if (SystemFonts.TryGet(preferred, out var family) || SystemFonts.TryGet(fallback, out family))
            return family.CreateFont(size, style);
        return SystemFonts.Families.First().CreateFont(size, style);
    }

    // Tags/description are optional and simply omitted - same "no row if absent" convention as the
    // design detail pane's own footer (MainWindow.EditMode.cs).
    public Image<Rgba32> RenderCard(string name, IReadOnlyList<string> tags, string? description, string? coverPath)
    {
        var contentWidth = CardWidth - Margin * 2;

        // The image box is sized to the cover's own aspect ratio (clamped to a sane range) rather than
        // a fixed 16:9 crop - a crop was cutting off most of a tall/portrait screenshot (e.g. a cropped
        // full-body shot) to force it into a landscape box. Sized this way, the whole thumbnail always
        // fits with nothing trimmed off.
        Image<Rgba32>? cover = null;
        var imageHeight = PlaceholderImageHeight;
        if (coverPath != null && File.Exists(coverPath))
        {
            try
            {
                cover = Image.Load<Rgba32>(coverPath);
                var aspect = (double)cover.Width / cover.Height;
                imageHeight = Math.Clamp((int)Math.Round(contentWidth / aspect), MinImageHeight, MaxImageHeight);
                // Pad (not a plain Resize) so an aspect ratio extreme enough to hit the clamp above
                // still fits without distortion - it just picks up thin letterbox bars instead.
                cover.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(contentWidth, imageHeight),
                    Mode = ResizeMode.Pad,
                    PadColor = BackgroundColor,
                }));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to load share-card cover image {Path}", coverPath);
                cover?.Dispose();
                cover = null;
                imageHeight = PlaceholderImageHeight;
            }
        }

        try
        {
            return RenderCardWithCover(name, tags, description, cover, imageHeight, contentWidth);
        }
        finally
        {
            cover?.Dispose();
        }
    }

    private Image<Rgba32> RenderCardWithCover(string name, IReadOnlyList<string> tags, string? description,
        Image<Rgba32>? cover, int imageHeight, int contentWidth)
    {
        var fittedName = FitSingleLine(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name, NameFont, contentWidth);

        var tagsText = tags.Count > 0 ? string.Join("   •   ", tags) : null;
        var tagsHeight = tagsText != null
            ? (int)Math.Ceiling(TextMeasurer.MeasureSize(tagsText, new RichTextOptions(TagsFont) { WrappingLength = contentWidth }).Height)
            : 0;

        var descText = string.IsNullOrWhiteSpace(description) ? null : CapLength(description!, DescriptionMaxChars);
        var descHeight = descText != null
            ? (int)Math.Ceiling(TextMeasurer.MeasureSize(descText, new RichTextOptions(BodyFont) { WrappingLength = contentWidth }).Height)
            : 0;

        var nameHeight = (int)Math.Ceiling(TextMeasurer.MeasureSize(fittedName, new RichTextOptions(NameFont)).Height);
        const int footerHeight = 20;

        var y = Margin;
        y += imageHeight;
        y += 20; // gap + gold rule
        var nameY = y;
        y += nameHeight + 10;
        var tagsY = y;
        if (tagsText != null)
            y += tagsHeight + 14;
        var descY = y;
        if (descText != null)
            y += descHeight + 14;
        var footerY = y;
        y += footerHeight + Margin;

        var canvas = new Image<Rgba32>(CardWidth, y, BackgroundColor.ToPixel<Rgba32>());

        DrawCoverImage(canvas, cover, Margin, Margin, contentWidth, imageHeight);
        canvas.Mutate(ctx => ctx.Fill(GoldRule, new RectangularPolygon(Margin, Margin + imageHeight + 9, contentWidth, 2)));

        var nameOptions = new RichTextOptions(NameFont) { Origin = new PointF(Margin, nameY) };
        canvas.Mutate(ctx => ctx.DrawText(nameOptions, fittedName, GoldAccent));

        if (tagsText != null)
        {
            var tagsDrawOptions = new RichTextOptions(TagsFont) { WrappingLength = contentWidth, Origin = new PointF(Margin, tagsY) };
            canvas.Mutate(ctx => ctx.DrawText(tagsDrawOptions, tagsText, MutedTextColor));
        }

        if (descText != null)
        {
            var descDrawOptions = new RichTextOptions(BodyFont) { WrappingLength = contentWidth, Origin = new PointF(Margin, descY) };
            canvas.Mutate(ctx => ctx.DrawText(descDrawOptions, descText, BodyTextColor));
        }

        var footerOptions = new RichTextOptions(FooterFont)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Origin = new PointF(Margin + contentWidth, footerY),
        };
        canvas.Mutate(ctx => ctx.DrawText(footerOptions, "AETHERFIT", MutedTextColor));

        return canvas;
    }

    // cover, when given, is already resized to exactly w x h (see RenderCard) - full thumbnail, no crop.
    private static void DrawCoverImage(Image<Rgba32> canvas, Image<Rgba32>? cover, int x, int y, int w, int h)
    {
        if (cover != null)
        {
            canvas.Mutate(ctx => ctx.DrawImage(cover, new Point(x, y), 1f));
            return;
        }

        canvas.Mutate(ctx => ctx.Fill(PlaceholderColor, new RectangularPolygon(x, y, w, h)));
        var placeholderOptions = new RichTextOptions(PlaceholderFont)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Origin = new PointF(x + w / 2f, y + h / 2f),
        };
        canvas.Mutate(ctx => ctx.DrawText(placeholderOptions, "No Image", MutedTextColor));
    }

    private static string FitSingleLine(string text, Font font, int maxWidth)
    {
        var options = new RichTextOptions(font);
        if (TextMeasurer.MeasureSize(text, options).Width <= maxWidth)
            return text;

        var trimmed = text;
        while (trimmed.Length > 1)
        {
            trimmed = trimmed[..^1];
            if (TextMeasurer.MeasureSize(trimmed + "…", options).Width <= maxWidth)
                return trimmed + "…";
        }
        return "…";
    }

    private static string CapLength(string text, int maxChars)
    {
        text = text.Trim();
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";
    }
}
