using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Aetherfit.Services.Screenshots;
using Aetherfit.Utils;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Aetherfit.Services.Export;

// Renders a visual-only PDF "look book" of design covers - a fashion-magazine-style contact sheet
// for showing off or browsing outside the plugin, unlike GallerySharingService's bundle export which
// carries the actual applyable design data.
public sealed class LookBookExportService
{
    public const string FileExtension = ".pdf";
    private const string Masthead = "Look Book";

    private static readonly XColor GoldAccent = XColor.FromArgb(255, 255, 217, 102);

    private const int Columns = 3;
    private const int Rows = 3;
    private const double MarginPt = 36; // 0.5"
    private const double CellGapPt = 14;
    private const double NameHeightPt = 18;
    private const double NameFontSize = 10;
    private const double HeaderH = 26;
    private const double FooterH = 20;

    // Cells are only a couple of inches wide, but source screenshots can be full game-window
    // resolution - embedding those raw balloons the PDF to hundreds of MB, so every cover is
    // downscaled and re-encoded before it goes in, same as GallerySharingService's bundle export.
    private const int CoverMaxDimension = 700;
    private const int HeroMaxDimension = 1600;
    private const int CoverJpegQuality = 82;

    private readonly Configuration configuration;
    private readonly ImageStorageService imageStorage;

    static LookBookExportService()
    {
        GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
    }

    public LookBookExportService(Configuration configuration, ImageStorageService imageStorage)
    {
        this.configuration = configuration;
        this.imageStorage = imageStorage;
    }

    // True while an export runs on its background thread. Set and cleared on the framework thread.
    public bool IsBusy { get; private set; }

    // onlyIds, when given, limits the export to those designs (e.g. the currently filtered list); null exports all.
    public Task ExportToFileAsync(string sharerLabel, string path, IReadOnlySet<Guid>? onlyIds = null)
    {
        if (IsBusy)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}An export is already running.");
            return Task.CompletedTask;
        }

        if (!path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            path += FileExtension;

        var items = new List<(Guid Id, string Name, string? CoverPath)>();
        foreach (var (id, outfit) in configuration.CachedOutfits)
        {
            if (onlyIds != null && !onlyIds.Contains(id))
                continue;
            if (configuration.HiddenDesigns.Contains(id))
                continue;
            if (!configuration.IsProviderEnabled(outfit.Source))
                continue;

            items.Add((id, outfit.Name, imageStorage.GetCoverPath(id)));
        }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        if (items.Count == 0)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}No designs to export.");
            return Task.CompletedTask;
        }

        // Cover hero: favourited design's cover if there is one, else just the first in the list.
        var heroPath = items.FirstOrDefault(i => configuration.FavouriteDesigns.Contains(i.Id)).CoverPath
            ?? items[0].CoverPath;
        var subtitle = $"{sharerLabel} • {items.Count} look{(items.Count == 1 ? "" : "s")}";

        IsBusy = true;
        var finalPath = path;
        return Task.Run(async () =>
        {
            try
            {
                using var document = BuildDocument(items, subtitle, heroPath);
                document.Save(finalPath);
                FrameworkChat.Print($"{Plugin.ChatPrefix}Exported look-book with {items.Count} design(s) to {Path.GetFileName(finalPath)}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to export look-book to {Path}", finalPath);
                FrameworkChat.PrintError($"{Plugin.ChatPrefix}Failed to export look-book: {ex.Message}");
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() => IsBusy = false);
            }
        });
    }

    private static PdfDocument BuildDocument(List<(Guid Id, string Name, string? CoverPath)> items, string subtitle, string? heroPath)
    {
        var document = new PdfDocument();
        document.Info.Title = $"Aetherfit {Masthead}";

        DrawCoverPage(document, subtitle, heroPath);

        var nameFont = new XFont("Georgia", NameFontSize, XFontStyleEx.Regular);
        var placeholderFont = new XFont("Verdana", NameFontSize, XFontStyleEx.Italic);

        var perPage = Columns * Rows;
        var totalPages = (int)Math.Ceiling((double)items.Count / perPage);

        XGraphics? gfx = null;
        double cellW = 0, imageH = 0, gridTop = 0;
        var pageNumber = 0;

        for (var i = 0; i < items.Count; i++)
        {
            var cellIndex = i % perPage;
            if (cellIndex == 0)
            {
                var page = document.AddPage();
                page.Size = PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                pageNumber++;

                DrawMasthead(gfx, page.Width.Point, subtitle);
                DrawFooter(gfx, page.Width.Point, page.Height.Point, pageNumber, totalPages);

                gridTop = MarginPt + HeaderH;
                cellW = (page.Width.Point - MarginPt * 2 - CellGapPt * (Columns - 1)) / Columns;
                imageH = (page.Height.Point - gridTop - MarginPt - FooterH - CellGapPt * (Rows - 1)) / Rows - NameHeightPt;
            }

            var col = cellIndex % Columns;
            var row = cellIndex / Columns;
            var x = MarginPt + col * (cellW + CellGapPt);
            var y = gridTop + row * (imageH + NameHeightPt + CellGapPt);

            var (_, name, coverPath) = items[i];
            DrawCell(gfx!, i + 1, name, coverPath, nameFont, placeholderFont, x, y, cellW, imageH);
        }

        return document;
    }

    private static void DrawCoverPage(PdfDocument document, string subtitle, string? heroPath)
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);

        var pageW = page.Width.Point;
        var pageH = page.Height.Point;

        // Masthead sits at the top of the page like an actual magazine nameplate, with the hero shot
        // presented large beneath it on the plain page background - screenshots already carry their
        // own (usually light) backdrop, so a boxed color band around them just fights the photo instead
        // of extending it.
        var eyebrowFont = new XFont("Verdana", 10, XFontStyleEx.Bold);
        var titleFont = new XFont("Georgia", 46, XFontStyleEx.Bold);
        var subtitleFont = new XFont("Verdana", 12, XFontStyleEx.Regular);

        gfx.DrawString(LetterSpaced("AETHERFIT"), eyebrowFont, new XSolidBrush(GoldAccent),
            new XRect(0, 44, pageW, 16), XStringFormats.TopCenter);
        gfx.DrawString(Masthead.ToUpperInvariant(), titleFont, XBrushes.Black,
            new XRect(0, 60, pageW, 60), XStringFormats.TopCenter);

        const double ruleY = 132;
        gfx.DrawLine(new XPen(GoldAccent, 1.6), pageW / 2 - 60, ruleY, pageW / 2 + 60, ruleY);

        var heroTop = ruleY + 26;
        var heroBottom = pageH - 92;

        if (heroPath != null && File.Exists(heroPath))
        {
            try
            {
                var jpeg = ScreenshotCaptureService.EncodePreviewJpeg(heroPath, HeroMaxDimension, CoverJpegQuality);
                using var stream = new MemoryStream(jpeg);
                using var image = XImage.FromStream(stream);
                var (x, y, w, h) = FitCentered(MarginPt, heroTop, pageW - MarginPt * 2, heroBottom - heroTop, image.PixelWidth, image.PixelHeight);
                gfx.DrawImage(image, x, y, w, h);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to draw look-book cover hero image {Path}", heroPath);
            }
        }

        gfx.DrawLine(new XPen(XColor.FromArgb(255, 224, 224, 224), 1), MarginPt, pageH - 74, pageW - MarginPt, pageH - 74);
        gfx.DrawString(subtitle, subtitleFont, XBrushes.Gray,
            new XRect(0, pageH - 62, pageW, 20), XStringFormats.TopCenter);
    }

    private static void DrawMasthead(XGraphics gfx, double pageWidth, string subtitle)
    {
        var titleFont = new XFont("Georgia", 13, XFontStyleEx.Bold);
        var subtitleFont = new XFont("Verdana", 8.5, XFontStyleEx.Regular);
        var rect = new XRect(MarginPt, MarginPt - 8, pageWidth - MarginPt * 2, 18);
        gfx.DrawString(Masthead.ToUpperInvariant(), titleFont, XBrushes.Black, rect, XStringFormats.CenterLeft);
        gfx.DrawString(subtitle, subtitleFont, XBrushes.Gray, rect, XStringFormats.CenterRight);
        gfx.DrawLine(new XPen(GoldAccent, 1.2), MarginPt, MarginPt + HeaderH - 8, pageWidth - MarginPt, MarginPt + HeaderH - 8);
    }

    private static void DrawFooter(XGraphics gfx, double pageWidth, double pageHeight, int pageNumber, int totalPages)
    {
        var font = new XFont("Verdana", 8, XFontStyleEx.Regular);
        gfx.DrawString($"Page {pageNumber} of {totalPages}", font, XBrushes.Gray,
            new XRect(MarginPt, pageHeight - MarginPt, pageWidth - MarginPt * 2, 14), XStringFormats.BottomCenter);
    }

    private static void DrawCell(XGraphics gfx, int index, string name, string? coverPath, XFont nameFont,
        XFont placeholderFont, double x, double y, double w, double h)
    {
        var frame = new XRect(x, y, w, h);
        gfx.DrawRectangle(XPens.LightGray, frame);

        var drawn = false;
        if (coverPath != null && File.Exists(coverPath))
        {
            try
            {
                var jpeg = ScreenshotCaptureService.EncodePreviewJpeg(coverPath, CoverMaxDimension, CoverJpegQuality);
                using var stream = new MemoryStream(jpeg);
                using var image = XImage.FromStream(stream);
                var (drawX, drawY, drawW, drawH) = FitCentered(x, y, w, h, image.PixelWidth, image.PixelHeight);
                gfx.DrawImage(image, drawX, drawY, drawW, drawH);
                drawn = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to draw look-book cover {Path}", coverPath);
            }
        }

        if (!drawn)
            gfx.DrawString("No Image", placeholderFont, XBrushes.Gray, frame, XStringFormats.Center);

        // Runway-style look number, top-left of the frame.
        var badgeFont = new XFont("Verdana", 8, XFontStyleEx.Bold);
        var badgeText = index.ToString("00");
        var badgeSize = gfx.MeasureString(badgeText, badgeFont);
        var badgeRect = new XRect(x + 5, y + 5, badgeSize.Width + 10, badgeSize.Height + 5);
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(200, 0, 0, 0)), badgeRect);
        gfx.DrawString(badgeText, badgeFont, XBrushes.White, badgeRect, XStringFormats.Center);

        gfx.DrawString(name, nameFont, XBrushes.Black, new XRect(x, y + h + 2, w, NameHeightPt - 2), XStringFormats.TopCenter);
    }

    // Fits pixelW x pixelH within the given frame, preserving aspect ratio and centering - screenshots
    // rarely carry reliable DPI metadata, so this works off pixel aspect ratio only.
    private static (double X, double Y, double W, double H) FitCentered(double frameX, double frameY,
        double frameW, double frameH, int pixelW, int pixelH)
    {
        var aspect = (double)pixelW / pixelH;
        var w = frameW;
        var h = frameW / aspect;
        if (h > frameH)
        {
            h = frameH;
            w = frameH * aspect;
        }
        return (frameX + (frameW - w) / 2, frameY + (frameH - h) / 2, w, h);
    }

    private static string LetterSpaced(string s) => string.Join(" ", s.ToCharArray());

}
