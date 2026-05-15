using System;
using System.IO;
using System.Numerics;
using Aetherfit.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public sealed class ScreenshotCropWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    
    private string? capturedImagePath;
    private Action<string>? onConfirmed;
    private string? errorMessage;
    
    private Vector2 selStart;
    private Vector2 selEnd;
    private bool hasSelection;
    private bool dragging;

    public ScreenshotCropWindow(Plugin plugin)
        : base("Aetherfit Crop##AetherfitScreenshotCrop", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(640, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() => DeleteCapturedFile();

    public void Begin(string capturePath, Action<string> onConfirmedCallback)
    {
        DeleteCapturedFile();
        capturedImagePath = capturePath;
        onConfirmed = onConfirmedCallback;
        hasSelection = false;
        dragging = false;
        errorMessage = null;
        selStart = Vector2.Zero;
        selEnd = Vector2.Zero;
        IsOpen = true;
        BringToFront();
    }

    public override void OnClose()
    {
        DeleteCapturedFile();
        onConfirmed = null;
        hasSelection = false;
        dragging = false;
        errorMessage = null;
    }

    public override void Draw()
    {
        if (string.IsNullOrEmpty(capturedImagePath) || !File.Exists(capturedImagePath))
        {
            ImGui.Text("No capture available.");
            return;
        }

        ImGui.TextWrapped("Drag on the image to select the area to keep. It will save automatically when you release.");
        ImGui.Spacing();

        var style = ImGui.GetStyle();
        var buttonsHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y;
        var errorHeight = string.IsNullOrEmpty(errorMessage)
            ? 0
            : ImGui.GetTextLineHeightWithSpacing() + style.ItemSpacing.Y;
        var avail = ImGui.GetContentRegionAvail();
        var imageAreaH = Math.Max(80, avail.Y - buttonsHeight - errorHeight);

        DrawCropImage(new Vector2(avail.X, imageAreaH));

        ImGui.Spacing();
        if (ImGui.Button("Retake", new Vector2(120, 0)))
            Retake();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            IsOpen = false;

        if (!string.IsNullOrEmpty(errorMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), errorMessage);
        }
    }

    private void DrawCropImage(Vector2 area)
    {
        var tex = Plugin.TextureProvider.GetFromFile(capturedImagePath!).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading screenshot...");
            return;
        }

        var scale = Math.Min(area.X / tex.Width, area.Y / tex.Height);
        var dispSize = new Vector2(tex.Width * scale, tex.Height * scale);
        var offset = new Vector2(
            (area.X - dispSize.X) * 0.5f,
            (area.Y - dispSize.Y) * 0.5f);

        var cursor = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(cursor + offset);
        ImGui.Image(tex.Handle, dispSize);
        var imgMin = ImGui.GetItemRectMin();
        var imgMax = ImGui.GetItemRectMax();

        // Overlay an invisible button so the click is consumed by an interactive item; otherwise the mouse-down falls through to the window and starts a window-drag.
        ImGui.SetCursorScreenPos(imgMin);
        ImGui.InvisibleButton("##cropArea", dispSize);

        if (ImGui.IsItemActivated())
        {
            dragging = true;
            hasSelection = false;
            var p = ScreenToImage(ImGui.GetMousePos(), imgMin, scale, tex.Width, tex.Height);
            selStart = p;
            selEnd = p;
        }

        var justFinishedSelection = false;
        if (dragging)
        {
            if (ImGui.IsItemActive())
            {
                selEnd = ScreenToImage(ImGui.GetMousePos(), imgMin, scale, tex.Width, tex.Height);
            }
            else
            {
                dragging = false;
                hasSelection = Math.Abs(selEnd.X - selStart.X) >= 4
                            && Math.Abs(selEnd.Y - selStart.Y) >= 4;
                justFinishedSelection = hasSelection;
            }
        }

        if (hasSelection || dragging)
            DrawSelectionOverlay(imgMin, imgMax, scale);

        if (justFinishedSelection)
            ConfirmCrop();
    }

    private void DrawSelectionOverlay(Vector2 imgMin, Vector2 imgMax, float scale)
    {
        var p1 = ImageToScreen(selStart, imgMin, scale);
        var p2 = ImageToScreen(selEnd, imgMin, scale);
        var rectMin = new Vector2(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y));
        var rectMax = new Vector2(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));

        var dl = ImGui.GetWindowDrawList();
        var dim = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.5f));
        dl.AddRectFilled(imgMin, new Vector2(imgMax.X, rectMin.Y), dim);
        dl.AddRectFilled(new Vector2(imgMin.X, rectMax.Y), imgMax, dim);
        dl.AddRectFilled(new Vector2(imgMin.X, rectMin.Y), new Vector2(rectMin.X, rectMax.Y), dim);
        dl.AddRectFilled(new Vector2(rectMax.X, rectMin.Y), new Vector2(imgMax.X, rectMax.Y), dim);

        var color = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.4f, 1f));
        dl.AddRect(rectMin, rectMax, color, 0f, ImDrawFlags.None, 2f);
    }

    private static Vector2 ScreenToImage(Vector2 mouse, Vector2 imgMin, float scale, int imgW, int imgH)
    {
        var x = (mouse.X - imgMin.X) / scale;
        var y = (mouse.Y - imgMin.Y) / scale;
        return new Vector2(
            Math.Clamp(x, 0, imgW),
            Math.Clamp(y, 0, imgH));
    }

    private static Vector2 ImageToScreen(Vector2 imgCoords, Vector2 imgMin, float scale)
    {
        return new Vector2(
            imgMin.X + imgCoords.X * scale,
            imgMin.Y + imgCoords.Y * scale);
    }

    private void Retake()
    {
        // Hand the callback back to the setup window before closing so OnClose doesn't null it on us.
        var cb = onConfirmed;
        onConfirmed = null;
        IsOpen = false;
        if (cb != null)
            plugin.ScreenshotSetup.Begin(cb);
    }

    private void ConfirmCrop()
    {
        if (string.IsNullOrEmpty(capturedImagePath) || !hasSelection)
            return;

        try
        {
            var x = (int)Math.Min(selStart.X, selEnd.X);
            var y = (int)Math.Min(selStart.Y, selEnd.Y);
            var w = (int)Math.Abs(selEnd.X - selStart.X);
            var h = (int)Math.Abs(selEnd.Y - selStart.Y);

            var croppedPath = plugin.Screenshot.CropTempToOutput(capturedImagePath, x, y, w, h);
            Sounds.PlayCapture();

            // Take the callback locally; closing the window nulls onConfirmed via OnClose.
            var cb = onConfirmed;
            onConfirmed = null;
            IsOpen = false;
            cb?.Invoke(croppedPath);

            plugin.Screenshot.CleanupTemp(croppedPath);
        }
        catch (Exception ex)
        {
            errorMessage = $"Crop failed: {ex.Message}";
            Plugin.Log.Warning(ex, "Failed to crop captured screenshot");
        }
    }

    private void DeleteCapturedFile()
    {
        plugin.Screenshot.CleanupTemp(capturedImagePath);
        capturedImagePath = null;
    }
}
