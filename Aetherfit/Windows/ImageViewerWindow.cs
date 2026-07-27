using System;
using System.IO;
using System.Numerics;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public class ImageViewerWindow : Window, IDisposable
{
    private string? imagePath;

    public ImageViewerWindow()
        : base("Aetherfit Image##AetherfitImageViewer",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)
    {
        Size = new Vector2(720, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(200, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public void Show(string path)
    {
        imagePath = path;
        IsOpen = true;
        BringToFront();
    }

    // Swaps the displayed image without opening the window or stealing focus.
    public void SyncTo(string? path)
    {
        if (IsOpen)
            imagePath = path;
    }

    public override void Draw()
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            ImGui.TextDisabled("No image to display.");
            return;
        }

        var tex = Plugin.TextureProvider.GetFromFile(imagePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading image...");
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        if (avail.X <= 0 || avail.Y <= 0)
            return;

        var (size, offset) = GalleryDraw.ComputeFitSize(avail, tex.Width, tex.Height);
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(cursor + Vector2.Max(offset, Vector2.Zero));
        ImGui.Image(tex.Handle, size);
    }
}
