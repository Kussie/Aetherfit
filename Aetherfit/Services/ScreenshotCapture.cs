using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aetherfit.Services;

// GDI-based screen capture against the running FFXIV client window, plus a small helper for cropping a PNG file. Runs on Windows; the plugin only targets Windows so SupportedOSPlatform("windows") is only needed to silence analyzers.
[SupportedOSPlatform("windows")]
internal static class ScreenshotCapture
{
    public static (byte[] Png, int Width, int Height) CaptureGameWindow()
    {
        var hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not locate the game window.");

        if (!GetClientRect(hwnd, out var clientRect))
            throw new InvalidOperationException("GetClientRect failed.");

        var topLeft = new POINT { X = clientRect.Left, Y = clientRect.Top };
        if (!ClientToScreen(hwnd, ref topLeft))
            throw new InvalidOperationException("ClientToScreen failed.");

        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Invalid window size {width}x{height}.");

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(topLeft.X, topLeft.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return (ms.ToArray(), width, height);
    }

    public static void CropAndSave(string sourcePath, string targetPath, int x, int y, int w, int h)
    {
        using var src = new Bitmap(sourcePath);

        x = Math.Clamp(x, 0, Math.Max(0, src.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, src.Height - 1));
        w = Math.Clamp(w, 1, src.Width - x);
        h = Math.Clamp(h, 1, src.Height - y);

        var srcRect = new Rectangle(x, y, w, h);
        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h), srcRect, GraphicsUnit.Pixel);
        }
        dst.Save(targetPath, ImageFormat.Png);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
