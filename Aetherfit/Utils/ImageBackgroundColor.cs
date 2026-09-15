using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace Aetherfit.Utils;

// Shared by the gallery letterbox bars (GalleryDraw) and the Discord share card (ShareCardService) -
// detects whether an image's edges are close enough to one colour to treat it as a solid backdrop.
internal static class ImageBackgroundColor
{
    // Samples the border ring (downscaled first, so this stays cheap) - median-based and outlier-tolerant,
    // so a character's arm/hair grazing the frame edge doesn't sink an otherwise-solid backdrop.
    public static Vector4? DetectSolidEdgeColor(string imagePath)
    {
        var options = new DecoderOptions { TargetSize = new Size(48, 48) };
        using var image = Image.Load<Rgba32>(options, imagePath);
        return DetectSolidEdgeColor(image);
    }

    public static Vector4? DetectSolidEdgeColor(Image<Rgba32> image)
    {
        var w = image.Width;
        var h = image.Height;
        if (w < 4 || h < 4)
            return null;

        var samples = new List<Rgba32>(2 * (w + h));
        image.ProcessPixelRows(accessor =>
        {
            var top = accessor.GetRowSpan(0);
            var bottom = accessor.GetRowSpan(h - 1);
            for (var x = 0; x < w; x++)
            {
                samples.Add(top[x]);
                samples.Add(bottom[x]);
            }
            for (var y = 1; y < h - 1; y++)
            {
                var row = accessor.GetRowSpan(y);
                samples.Add(row[0]);
                samples.Add(row[w - 1]);
            }
        });

        var medianR = Median(samples, p => p.R);
        var medianG = Median(samples, p => p.G);
        var medianB = Median(samples, p => p.B);

        const int tolerance = 40;
        var inliers = 0;
        long sumR = 0, sumG = 0, sumB = 0;
        foreach (var p in samples)
        {
            if (Math.Abs(p.R - medianR) > tolerance || Math.Abs(p.G - medianG) > tolerance || Math.Abs(p.B - medianB) > tolerance)
                continue;
            inliers++;
            sumR += p.R;
            sumG += p.G;
            sumB += p.B;
        }

        // A genuine gradient/scene still fails this easily - it's only forgiving isolated intrusions.
        if (inliers < samples.Count * 0.8)
            return null;

        return new Vector4(sumR / 255f / inliers, sumG / 255f / inliers, sumB / 255f / inliers, 1f);
    }

    private static byte Median(List<Rgba32> samples, Func<Rgba32, byte> selector)
    {
        var sorted = samples.Select(selector).OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
