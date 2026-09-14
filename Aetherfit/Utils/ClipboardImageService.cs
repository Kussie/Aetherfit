using System;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Aetherfit.Utils;

// No ImGui/Dalamud API puts an image on the clipboard, so this goes straight to Win32. Sets both the
// registered "PNG" format (Chromium apps like Discord read this first) and legacy CF_DIB as a fallback.
internal static class ClipboardImageService
{
    private const uint CfDib = 8;
    private const uint GMemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static bool TryCopyToClipboard(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        var pngBytes = ms.ToArray();

        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            EmptyClipboard();

            var pngFormat = RegisterClipboardFormat("PNG");
            SetOrFree(pngFormat, CopyToGlobal(pngBytes));
            SetOrFree(CfDib, CopyToGlobal(BuildDib(image)));

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    // SetClipboardData takes ownership of the handle on success; on failure it's ours to free again.
    private static void SetOrFree(uint format, IntPtr hMem)
    {
        if (hMem == IntPtr.Zero)
            return;
        if (SetClipboardData(format, hMem) == IntPtr.Zero)
            GlobalFree(hMem);
    }

    private static IntPtr CopyToGlobal(byte[] data)
    {
        var hMem = GlobalAlloc(GMemMoveable, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero)
            return IntPtr.Zero;

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return IntPtr.Zero;
        }

        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(hMem);
        return hMem;
    }

    // Packed BITMAPINFOHEADER + bottom-up 32bpp BGRA pixel data - the traditional CF_DIB shape every
    // Win32 clipboard consumer (old and new) understands, unlike a top-down negative-height DIB.
    private static byte[] BuildDib(Image<Rgba32> image)
    {
        var width = image.Width;
        var height = image.Height;
        const int headerSize = 40;
        var rowSize = width * 4;
        var buffer = new byte[headerSize + rowSize * height];

        using (var ms = new MemoryStream(buffer, 0, headerSize, true))
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(headerSize);
            writer.Write(width);
            writer.Write(height); // positive = bottom-up
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0); // BI_RGB
            writer.Write(rowSize * height);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
        }

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(height - 1 - y);
                var destOffset = headerSize + y * rowSize;
                for (var x = 0; x < width; x++)
                {
                    var px = row[x];
                    buffer[destOffset + x * 4 + 0] = px.B;
                    buffer[destOffset + x * 4 + 1] = px.G;
                    buffer[destOffset + x * 4 + 2] = px.R;
                    buffer[destOffset + x * 4 + 3] = 255;
                }
            }
        });

        return buffer;
    }
}
