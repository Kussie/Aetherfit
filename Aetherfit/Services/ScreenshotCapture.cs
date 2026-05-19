using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace Aetherfit.Services;

internal static class ScreenshotCapture
{
    public static (byte[] Png, int Width, int Height) CaptureGameWindow()
        => D3D11CaptureService.CaptureFrame();

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
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
        }
        dst.Save(targetPath, ImageFormat.Png);
    }
}
