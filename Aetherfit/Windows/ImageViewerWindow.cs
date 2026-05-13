using System;
using System.IO;
using System.Numerics;
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

    public override void Draw()
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            ImGui.TextDisabled("Image is no longer available.");
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

        var scale = Math.Min(avail.X / tex.Width, avail.Y / tex.Height);
        var size = new Vector2(tex.Width * scale, tex.Height * scale);

        var offsetX = Math.Max(0, (avail.X - size.X) * 0.5f);
        var offsetY = Math.Max(0, (avail.Y - size.Y) * 0.5f);
        var cursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(cursor.X + offsetX, cursor.Y + offsetY));
        ImGui.Image(tex.Handle, size);
    }
}
