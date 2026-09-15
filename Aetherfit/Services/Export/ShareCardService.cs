using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfit.Utils;
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

    // None of the fonts above cover CJK, so an unmodded design name/tag/mod name in Japanese, Chinese
    // or Korean would otherwise draw as tofu boxes - these are standard Windows fonts, tried in order.
    private static readonly IReadOnlyList<FontFamily> FallbackFamilies = BuildFallbackFamilies();

    private static IReadOnlyList<FontFamily> BuildFallbackFamilies()
    {
        var names = new[] { "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Segoe UI Symbol" };
        var families = new List<FontFamily>();
        foreach (var name in names)
            if (SystemFonts.TryGet(name, out var family))
                families.Add(family);
        return families;
    }

    private static RichTextOptions NewOptions(Font font, float? wrappingLength = null, PointF? origin = null,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Left, VerticalAlignment verticalAlignment = VerticalAlignment.Top)
    {
        var options = new RichTextOptions(font) { FallbackFontFamilies = FallbackFamilies };
        if (wrappingLength is { } w)
            options.WrappingLength = w;
        if (origin is { } o)
            options.Origin = o;
        options.HorizontalAlignment = horizontalAlignment;
        options.VerticalAlignment = verticalAlignment;
        return options;
    }

    // Tags/mods/description are optional and simply omitted, like the design detail pane's own footer.
    public Image<Rgba32> RenderCard(string name, IReadOnlyList<string> tags, IReadOnlyList<string> mods,
        string? description, string? coverPath)
    {
        var contentWidth = CardWidth - Margin * 2;

        // Box height follows the cover's own aspect ratio (clamped) rather than a fixed crop, so the
        // whole thumbnail always fits.
        Image<Rgba32>? cover = null;
        var imageHeight = PlaceholderImageHeight;
        var cardColor = BackgroundColor;
        if (coverPath != null && File.Exists(coverPath))
        {
            try
            {
                cover = Image.Load<Rgba32>(coverPath);
                // Sampled before Resize touches the pixels - see cardColor's use below for where it lands.
                if (ImageBackgroundColor.DetectSolidEdgeColor(cover) is { } detected)
                    cardColor = new Color(detected);

                var aspect = (double)cover.Width / cover.Height;
                imageHeight = Math.Clamp((int)Math.Round(contentWidth / aspect), MinImageHeight, MaxImageHeight);
                // Pad, not a plain Resize - avoids distortion if the clamp above kicks in.
                cover.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(contentWidth, imageHeight),
                    Mode = ResizeMode.Pad,
                    PadColor = cardColor,
                }));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to load share-card cover image {Path}", coverPath);
                cover?.Dispose();
                cover = null;
                imageHeight = PlaceholderImageHeight;
                cardColor = BackgroundColor;
            }
        }

        try
        {
            return RenderCardWithCover(name, tags, mods, description, cover, imageHeight, contentWidth, cardColor);
        }
        finally
        {
            cover?.Dispose();
        }
    }

    private Image<Rgba32> RenderCardWithCover(string name, IReadOnlyList<string> tags, IReadOnlyList<string> mods,
        string? description, Image<Rgba32>? cover, int imageHeight, int contentWidth, Color cardColor)
    {
        var fittedName = FitSingleLine(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name, NameFont, contentWidth);

        var tagsText = tags.Count > 0 ? string.Join("   •   ", tags) : null;
        var tagsHeight = tagsText != null
            ? (int)Math.Ceiling(TextMeasurer.MeasureSize(tagsText, NewOptions(TagsFont, wrappingLength: contentWidth)).Height)
            : 0;

        var modsText = mods.Count > 0 ? "Mods: " + string.Join("   •   ", mods) : null;
        var modsHeight = modsText != null
            ? (int)Math.Ceiling(TextMeasurer.MeasureSize(modsText, NewOptions(TagsFont, wrappingLength: contentWidth)).Height)
            : 0;

        var descText = string.IsNullOrWhiteSpace(description) ? null : CapLength(description!, DescriptionMaxChars);
        var descHeight = descText != null
            ? (int)Math.Ceiling(TextMeasurer.MeasureSize(descText, NewOptions(BodyFont, wrappingLength: contentWidth)).Height)
            : 0;

        var nameHeight = (int)Math.Ceiling(TextMeasurer.MeasureSize(fittedName, NewOptions(NameFont)).Height);
        const int footerHeight = 20;

        var y = Margin;
        y += imageHeight;
        y += 20; // gap + gold rule
        var nameY = y;
        y += nameHeight + 10;
        var tagsY = y;
        if (tagsText != null)
            y += tagsHeight + 14;
        var modsY = y;
        if (modsText != null)
            y += modsHeight + 14;
        var descY = y;
        if (descText != null)
            y += descHeight + 14;
        var footerY = y;
        y += footerHeight + Margin;

        var canvas = new Image<Rgba32>(CardWidth, y, BackgroundColor.ToPixel<Rgba32>());

        // Only the image band gets the detected backdrop colour - everything below the rule (name
        // onward) keeps the card's own dark background regardless.
        canvas.Mutate(ctx => ctx.Fill(cardColor, new RectangularPolygon(0, 0, CardWidth, Margin + imageHeight + 9)));

        DrawCoverImage(canvas, cover, Margin, Margin, contentWidth, imageHeight);
        canvas.Mutate(ctx => ctx.Fill(GoldRule, new RectangularPolygon(Margin, Margin + imageHeight + 9, contentWidth, 2)));

        var nameOptions = NewOptions(NameFont, origin: new PointF(Margin, nameY));
        canvas.Mutate(ctx => ctx.DrawText(nameOptions, fittedName, GoldAccent));

        if (tagsText != null)
        {
            var tagsDrawOptions = NewOptions(TagsFont, wrappingLength: contentWidth, origin: new PointF(Margin, tagsY));
            canvas.Mutate(ctx => ctx.DrawText(tagsDrawOptions, tagsText, MutedTextColor));
        }

        if (modsText != null)
        {
            var modsDrawOptions = NewOptions(TagsFont, wrappingLength: contentWidth, origin: new PointF(Margin, modsY));
            canvas.Mutate(ctx => ctx.DrawText(modsDrawOptions, modsText, MutedTextColor));
        }

        if (descText != null)
        {
            var descDrawOptions = NewOptions(BodyFont, wrappingLength: contentWidth, origin: new PointF(Margin, descY));
            canvas.Mutate(ctx => ctx.DrawText(descDrawOptions, descText, BodyTextColor));
        }

        var footerOptions = NewOptions(FooterFont, origin: new PointF(Margin + contentWidth, footerY),
            horizontalAlignment: HorizontalAlignment.Right);
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
        var placeholderOptions = NewOptions(PlaceholderFont, origin: new PointF(x + w / 2f, y + h / 2f),
            horizontalAlignment: HorizontalAlignment.Center, verticalAlignment: VerticalAlignment.Center);
        canvas.Mutate(ctx => ctx.DrawText(placeholderOptions, "No Image", MutedTextColor));
    }

    private static string FitSingleLine(string text, Font font, int maxWidth)
    {
        var options = NewOptions(font);
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
