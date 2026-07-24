using System;
using System.IO;

namespace Aetherfit.Services.Screenshots;

public sealed class ScreenshotService
{
    public void CaptureGameWindowDelayed(
        Action onBeforeCapture,
        Action onAfterCapture,
        Action<string> onTempReady,
        Action<Exception> onError)
    {
        onBeforeCapture();
        
        Plugin.Framework.RunOnTick(() =>
        {
            Plugin.PluginInterface.UiBuilder.Draw += CaptureOnNextDraw;
        }, delayTicks: 3);

        void CaptureOnNextDraw()
        {
            Plugin.PluginInterface.UiBuilder.Draw -= CaptureOnNextDraw;
            try
            {
                var (png, _, _) = ScreenshotCaptureService.CaptureGameWindow();
                var dir = EnsureTempDir();
                var path = Path.Combine(dir, $"capture_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, png);
                onAfterCapture();
                onTempReady(path);
            }
            catch (Exception ex)
            {
                onAfterCapture();
                Plugin.Log.Warning(ex, "Screenshot capture failed");
                onError(ex);
            }
        }
    }
    
    public string CropTempToOutput(string tempCapturePath, int x, int y, int w, int h)
    {
        var dir = EnsureTempDir();
        var croppedPath = Path.Combine(dir, $"crop_{Guid.NewGuid():N}.png");
        ScreenshotCaptureService.CropAndSave(tempCapturePath, croppedPath, x, y, w, h);
        return croppedPath;
    }
    
    public void CleanupTemp(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { File.Delete(path); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to delete temp screenshot {Path}", path); }
    }

    private static string EnsureTempDir()
    {
        var dir = ImageStorageService.TempDirectoryPath;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
