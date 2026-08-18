using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace Aetherfit.Services.Screenshots;

// Captures the game viewport through Dalamud's own texture pipeline rather than reading the swapchain
// backbuffer by hand. TakeBeforeImGuiRender excludes this frame's ImGui pass, so plugin windows never
// leak into the shot even without hiding them first.
internal static class DalamudViewportCaptureService
{
    // WIC's well-known PNG container format GUID (stable since Vista, see GUID_ContainerFormatPng) -
    // Dalamud's GetSupportedImageEncoderInfos() wraps WIC's own codec list, whose ContainerGuid is this
    // value directly. Matching on it here instead of string-searching Extensions/MimeTypes at runtime,
    // since that search came back empty against the real values Dalamud returns.
    private static readonly Guid PngContainerFormat = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");

    public static async Task<(byte[] Png, int Width, int Height)> CaptureFrameAsync()
    {
        using var wrap = await Plugin.TextureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
        {
            ViewportId = ImGui.GetMainViewport().ID,
            AutoUpdate = false,
            TakeBeforeImGuiRender = true,
            KeepTransparency = false,
        });

        using var ms = new MemoryStream();
        await Plugin.TextureReadback.SaveToStreamAsync(wrap, PngContainerFormat, ms, leaveWrapOpen: true, leaveStreamOpen: true);
        return (ms.ToArray(), wrap.Width, wrap.Height);
    }
}
