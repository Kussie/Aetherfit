using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Aetherfit.Services;

// Reads the game's backbuffer directly via D3D11, bypassing GDI so it works on Wine/DXVK.
// I cant remember where i found this solution but it helped me figure out how to do it without wgoing through the GDI
internal static unsafe class D3D11CaptureService
{
    private static readonly Guid IID_ID3D11Texture2D = new(
        0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);

    private const uint FmtR8G8B8A8     = 28;
    private const uint FmtR8G8B8A8Srgb = 29;
    private const uint FmtB8G8R8A8     = 87;
    private const uint FmtB8G8R8A8Srgb = 91;

    public static (byte[] Png, int Width, int Height) CaptureFrame()
    {
        var kernelDevice = Device.Instance();
        if (kernelDevice == null)
            throw new InvalidOperationException("Game graphics device is not initialized.");

        var gameSwapChain = kernelDevice->SwapChain;
        if (gameSwapChain == null)
            throw new InvalidOperationException("Game swap chain is not initialized.");

        var swapChain = (nint)gameSwapChain->DXGISwapChain;
        var context   = (nint)kernelDevice->D3D11DeviceContext;

        if (swapChain == 0) throw new InvalidOperationException("IDXGISwapChain pointer is null.");
        if (context   == 0) throw new InvalidOperationException("ID3D11DeviceContext pointer is null.");
        
        nint device = 0;
        VtGetDevice(context, &device);
        if (device == 0)
            throw new InvalidOperationException("Failed to obtain ID3D11Device from context.");

        try
        {
            return CaptureImpl(swapChain, device, context);
        }
        finally
        {
            VtRelease(device);
        }
    }

    private static (byte[] Png, int Width, int Height) CaptureImpl(nint swapChain, nint device, nint context)
    {
        var iid = IID_ID3D11Texture2D;
        nint backbuffer = 0;
        int hr = VtSwapChainGetBuffer(swapChain, 0, &iid, &backbuffer);
        if (hr < 0 || backbuffer == 0)
            throw new InvalidOperationException($"IDXGISwapChain::GetBuffer failed (0x{hr:X8}).");

        try
        {
            D3D11Texture2DDesc desc = default;
            VtTexture2DGetDesc(backbuffer, &desc);

            Plugin.Log.Debug("D3D11 capture: format={Format}, size={W}x{H}, sampleCount={S}",
                             desc.Format, desc.Width, desc.Height, desc.SampleDesc.Count);

            if (desc.SampleDesc.Count > 1)
                throw new NotSupportedException(
                    $"MSAA backbuffer (sample count {desc.SampleDesc.Count}) is not supported. Disable MSAA in-game.");

            if (desc.Format is not (FmtB8G8R8A8 or FmtB8G8R8A8Srgb or FmtR8G8B8A8 or FmtR8G8B8A8Srgb))
                throw new NotSupportedException($"Unsupported backbuffer format ({desc.Format}). Expected B8G8R8A8 or R8G8B8A8.");

            var stagingDesc = new D3D11Texture2DDesc
            {
                Width          = desc.Width,
                Height         = desc.Height,
                MipLevels      = 1,
                ArraySize      = 1,
                Format         = desc.Format,
                SampleDesc     = new SampleDesc { Count = 1, Quality = 0 },
                Usage          = 3,         
                BindFlags      = 0,
                CpuAccessFlags = 0x0002_0000,
                MiscFlags      = 0,
            };

            nint staging = 0;
            int createHr = VtCreateTexture2D(device, &stagingDesc, null, &staging);
            if (createHr < 0 || staging == 0)
                throw new InvalidOperationException($"ID3D11Device::CreateTexture2D failed (0x{createHr:X8}).");

            try
            {
                VtCopyResource(context, staging, backbuffer);

                D3D11MappedSubresource mapped = default;
                int mapHr = VtMap(context, staging, 0, 1, 0, &mapped);
                if (mapHr < 0)
                    throw new InvalidOperationException($"ID3D11DeviceContext::Map failed (0x{mapHr:X8}).");

                try
                {
                    var png = ToPng((int)desc.Width, (int)desc.Height, desc.Format, mapped.RowPitch, mapped.pData);
                    return (png, (int)desc.Width, (int)desc.Height);
                }
                finally
                {
                    VtUnmap(context, staging, 0);
                }
            }
            finally
            {
                VtRelease(staging);
            }
        }
        finally
        {
            VtRelease(backbuffer);
        }
    }

    // Backbuffer alpha is always opaque, so this writes a plain RGB (color type 2) PNG rather than RGBA.
    private static byte[] ToPng(int width, int height, uint format, uint rowPitch, void* srcData)
    {
        // B8G8R8A8 is laid out B,G,R,A in memory, so it needs the R/B swap to land in the RGB byte order the PNG wants.
        bool swapRB = format is FmtB8G8R8A8 or FmtB8G8R8A8Srgb;
        var src = (byte*)srcData;

        var stride = 1 + width * 3; // filter-type byte + RGB per pixel
        var raw = new byte[(long)height * stride];
        for (int y = 0; y < height; y++)
        {
            byte* srcRow = src + y * rowPitch;
            var rowOffset = y * stride + 1; // raw[y*stride] stays 0 (filter type "None")

            for (int x = 0; x < width; x++)
            {
                var b0 = srcRow[x * 4 + 0];
                var b1 = srcRow[x * 4 + 1];
                var b2 = srcRow[x * 4 + 2];
                var o = rowOffset + x * 3;
                raw[o + 0] = swapRB ? b2 : b0;
                raw[o + 1] = b1;
                raw[o + 2] = swapRB ? b0 : b2;
            }
        }

        return PngEncoder.Encode(width, height, raw);
    }

    private static void VtRelease(nint com)
    {
        if (com == 0) return;
        ((delegate* unmanaged<nint, uint>*)(*(nint*)com))[2](com);
    }

    private static void VtGetDevice(nint deviceChild, nint* ppDevice)
        => ((delegate* unmanaged<nint, nint*, void>*)(*(nint*)deviceChild))[3](deviceChild, ppDevice);

    private static int VtSwapChainGetBuffer(nint swapChain, uint buffer, Guid* riid, nint* ppSurface)
        => ((delegate* unmanaged<nint, uint, Guid*, nint*, int>*)(*(nint*)swapChain))[9](swapChain, buffer, riid, ppSurface);

    private static void VtTexture2DGetDesc(nint texture, D3D11Texture2DDesc* pDesc)
        => ((delegate* unmanaged<nint, D3D11Texture2DDesc*, void>*)(*(nint*)texture))[10](texture, pDesc);

    private static int VtCreateTexture2D(nint device, D3D11Texture2DDesc* pDesc, void* pInitialData, nint* ppTexture)
        => ((delegate* unmanaged<nint, D3D11Texture2DDesc*, void*, nint*, int>*)(*(nint*)device))[5](device, pDesc, pInitialData, ppTexture);

    private static void VtCopyResource(nint ctx, nint dst, nint src)
        => ((delegate* unmanaged<nint, nint, nint, void>*)(*(nint*)ctx))[47](ctx, dst, src);

    private static int VtMap(nint ctx, nint resource, uint subresource, uint mapType, uint mapFlags, D3D11MappedSubresource* pMapped)
        => ((delegate* unmanaged<nint, nint, uint, uint, uint, D3D11MappedSubresource*, int>*)(*(nint*)ctx))[14](ctx, resource, subresource, mapType, mapFlags, pMapped);

    private static void VtUnmap(nint ctx, nint resource, uint subresource)
        => ((delegate* unmanaged<nint, nint, uint, void>*)(*(nint*)ctx))[15](ctx, resource, subresource);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public SampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public void* pData;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
