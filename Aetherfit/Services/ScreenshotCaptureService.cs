using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Aetherfit.Services;

internal static class ScreenshotCaptureService
{
    public static (byte[] Png, int Width, int Height) CaptureGameWindow()
        => D3D11CaptureService.CaptureFrame();

    // Shrinks an image to maxDimension on its longest side and saves it as JPEG. Used for shared-gallery previews,
    // where a full-size PNG screenshot is way more than anyone needs.
    public static byte[] EncodePreviewJpeg(string sourcePath, int maxDimension, long quality)
    {
        using var src = new Bitmap(sourcePath);

        var longest = Math.Max(src.Width, src.Height);
        var scale = longest > maxDimension ? (double)maxDimension / longest : 1.0;

        Bitmap image = src;
        var resized = false;
        if (scale < 1.0)
        {
            var w = Math.Max(1, (int)Math.Round(src.Width * scale));
            var h = Math.Max(1, (int)Math.Round(src.Height * scale));
            image = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(image))
            {
                ApplyHighQuality(g);
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            resized = true;
        }

        try
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            using var ms = new MemoryStream();
            image.Save(ms, codec, encParams);
            return ms.ToArray();
        }
        finally
        {
            if (resized)
                image.Dispose();
        }
    }

    public static void CropAndSave(string sourcePath, string targetPath, int x, int y, int w, int h)
    {
        using var src = new Bitmap(sourcePath);

        x = Math.Clamp(x, 0, Math.Max(0, src.Width  - 1));
        y = Math.Clamp(y, 0, Math.Max(0, src.Height - 1));
        w = Math.Clamp(w, 1, src.Width  - x);
        h = Math.Clamp(h, 1, src.Height - y);

        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            ApplyHighQuality(g);
            g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
        }
        dst.Save(targetPath, ImageFormat.Png);
    }

    // Bicubic + high-quality pixel offset, so down-scaled previews and crops stay sharp.
    private static void ApplyHighQuality(Graphics g)
    {
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
    }
}
