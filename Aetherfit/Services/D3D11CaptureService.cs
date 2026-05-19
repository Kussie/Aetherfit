using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Aetherfit.Services;

// Captures the game's current backbuffer directly from the D3D11 swap chain, bypassing GDI.
// This works correctly on Linux via Wine/DXVK where GDI-based capture returns a black frame.
//
// Access path:
//   Device.Instance() -> SwapChain* -> void* DXGISwapChain (offset 104)
//   Device.Instance() -> void* D3D11DeviceContext           (offset 920240, ID3D11DeviceContext4*)
//   ID3D11Device* obtained via ID3D11DeviceChild::GetDevice (vtable slot 3) on the context
internal static unsafe class D3D11CaptureService
{
    private static readonly Guid IID_ID3D11Texture2D = new(
        0x6f15aaf2, 0xd208, 0x4e89, 0x9a, 0xb4, 0x48, 0x95, 0x35, 0xd3, 0x4f, 0x9c);

    private const uint DxgiFormatR8G8B8A8Unorm     = 28;
    private const uint DxgiFormatR8G8B8A8UnormSrgb = 29;
    private const uint DxgiFormatB8G8R8A8Unorm     = 87;
    private const uint DxgiFormatB8G8R8A8UnormSrgb = 91;

    private const uint D3D11UsageStaging    = 3;
    private const uint D3D11CpuAccessRead   = 0x0002_0000;
    private const uint D3D11MapRead         = 1;

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

        // GetDevice (vtable slot 3) adds a reference — must Release when done.
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

            if (desc.Format is not (DxgiFormatB8G8R8A8Unorm or DxgiFormatB8G8R8A8UnormSrgb
                                 or DxgiFormatR8G8B8A8Unorm or DxgiFormatR8G8B8A8UnormSrgb))
                throw new NotSupportedException($"Unsupported backbuffer format ({desc.Format}). Expected B8G8R8A8 or R8G8B8A8.");

            var stagingDesc = new D3D11Texture2DDesc
            {
                Width          = desc.Width,
                Height         = desc.Height,
                MipLevels      = 1,
                ArraySize      = 1,
                Format         = desc.Format,
                SampleDesc     = new SampleDesc { Count = 1, Quality = 0 },
                Usage          = D3D11UsageStaging,
                BindFlags      = 0,
                CpuAccessFlags = D3D11CpuAccessRead,
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
                int mapHr = VtMap(context, staging, 0, D3D11MapRead, 0, &mapped);
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

    private static byte[] ToPng(int width, int height, uint format, uint rowPitch, void* srcData)
    {
        // R8G8B8A8 sources need R and B swapped to match GDI's Format32bppArgb (BGRA in memory).
        // B8G8R8A8 sources are already in BGRA order and can be copied directly.
        bool swapRB = format is DxgiFormatR8G8B8A8Unorm or DxgiFormatR8G8B8A8UnormSrgb;

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var src = (byte*)srcData;
            var dst = (byte*)bits.Scan0;
            int dstStride = bits.Stride;

            for (int y = 0; y < height; y++)
            {
                byte* srcRow = src + y * rowPitch;
                byte* dstRow = dst + y * dstStride;

                for (int x = 0; x < width; x++)
                {
                    if (swapRB)
                    {
                        dstRow[x * 4 + 0] = srcRow[x * 4 + 2]; // B <- R
                        dstRow[x * 4 + 1] = srcRow[x * 4 + 1]; // G = G
                        dstRow[x * 4 + 2] = srcRow[x * 4 + 0]; // R <- B
                    }
                    else
                    {
                        dstRow[x * 4 + 0] = srcRow[x * 4 + 0]; // B = B
                        dstRow[x * 4 + 1] = srcRow[x * 4 + 1]; // G = G
                        dstRow[x * 4 + 2] = srcRow[x * 4 + 2]; // R = R
                    }
                    dstRow[x * 4 + 3] = 255; // force fully opaque
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // COM vtable helpers
    //
    // COM objects are laid out as: [ptr to vtable][fields...]
    //   vtable: [fn0][fn1][fn2]...
    //
    // Slot indices follow the D3D11 interface inheritance chain:
    //   IUnknown               : 0=QueryInterface  1=AddRef       2=Release
    //   ID3D11DeviceChild      : 3=GetDevice        4-6=private data helpers
    //   IDXGIObject            : 3-6 (parallel hierarchy, not used here)
    //   IDXGIDeviceSubObject   : 7=GetDevice
    //   IDXGISwapChain         : 8=Present  9=GetBuffer  ...
    //   ID3D11Resource         : 7=GetType  8-9=eviction priority
    //   ID3D11Texture2D        : 10=GetDesc
    //   ID3D11Device           : 5=CreateTexture2D  ...
    //   ID3D11DeviceContext    : 14=Map  15=Unmap  47=CopyResource
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // D3D11 struct definitions (must match d3d11.h layout exactly)
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;          // DXGI_FORMAT
        public SampleDesc SampleDesc;
        public uint Usage;           // D3D11_USAGE
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
