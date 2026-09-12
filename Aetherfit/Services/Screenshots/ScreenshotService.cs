using System;
using System.IO;
using System.Threading.Tasks;

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
            _ = CaptureAndFinishAsync();
        }

        async Task CaptureAndFinishAsync()
        {
            try
            {
                var (png, _, _) = await ScreenshotCaptureService.CaptureGameWindowAsync();
                var dir = EnsureTempDir();
                var path = Path.Combine(dir, $"capture_{Guid.NewGuid():N}.png");
                await File.WriteAllBytesAsync(path, png);

                // Both awaits above resume on a thread-pool thread, not the game's framework thread -
                // callers chain straight from onTempReady into applying the next design (Glamourer/Penumbra
                // IPC touching live character draw data), which is not safe to do off the framework thread
                // and was crashing the game intermittently (native access violation deep in weapon/animation
                // reload) when it raced the main thread's own frame update. Marshal back before calling out.
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    onAfterCapture();
                    onTempReady(path);
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Screenshot capture failed");
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    onAfterCapture();
                    onError(ex);
                });
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
