using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Aetherfit.Services;

internal static class ScreenshotCaptureService
{
    public static (byte[] Png, int Width, int Height) CaptureGameWindow()
        => D3D11CaptureService.CaptureFrame();

    // Shrinks an image to maxDimension on its longest side and saves it as JPEG. Used for shared-gallery previews,
    // where a full-size PNG screenshot is way more than anyone needs.
    public static byte[] EncodePreviewJpeg(string sourcePath, int maxDimension, int quality)
    {
        using var image = Image.Load(sourcePath);

        var longest = Math.Max(image.Width, image.Height);
        if (longest > maxDimension)
        {
            var scale = (double)maxDimension / longest;
            var w = Math.Max(1, (int)Math.Round(image.Width * scale));
            var h = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(w, h), Sampler = KnownResamplers.Bicubic }));
        }

        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    public static void CropAndSave(string sourcePath, string targetPath, int x, int y, int w, int h)
    {
        using var image = Image.Load(sourcePath);

        x = Math.Clamp(x, 0, Math.Max(0, image.Width  - 1));
        y = Math.Clamp(y, 0, Math.Max(0, image.Height - 1));
        w = Math.Clamp(w, 1, image.Width  - x);
        h = Math.Clamp(h, 1, image.Height - y);

        image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, w, h)));
        image.SaveAsPng(targetPath);
    }
}
