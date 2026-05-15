using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace Aetherfit.Windows;

public sealed class ScreenshotSetupWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Action<string>? onConfirmed;
    private string? errorMessage;

    public ScreenshotSetupWindow(Plugin plugin)
        : base("Aetherfit Screenshot##AetherfitScreenshotSetup", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(500, 230);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void Begin(Action<string> onConfirmedCallback)
    {
        onConfirmed = onConfirmedCallback;
        errorMessage = null;

        if (Plugin.ClientState.IsGPosing)
        {
            BeginCapture();
            return;
        }

        IsOpen = true;
        BringToFront();
    }

    public override void OnClose()
    {
        onConfirmed = null;
        errorMessage = null;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Set up your pose, camera, and framing in GPose, then click Capture.");
        ImGui.Spacing();
        ImGui.TextDisabled("This plugin window stays open inside GPose so you can keep using it while posing.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var inGPose = Plugin.ClientState.IsGPosing;
        using (ImRaii.Disabled(inGPose))
        {
            if (ImGui.Button("Enter GPose", new Vector2(120, 0)))
                TriggerGPoseToggle();
        }
        ImGui.SameLine();
        if (ImGui.Button("Capture", new Vector2(120, 0)))
            BeginCapture();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120, 0)))
            IsOpen = false;

        if (inGPose)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("(already in GPose)");
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), errorMessage);
        }
    }

    private void BeginCapture()
    {
        errorMessage = null;

        // Hold the callback locally so OnClose nulling onConfirmed doesn't lose it.
        var cb = onConfirmed;
        if (cb == null)
            return;

        plugin.Screenshot.CaptureGameWindowDelayed(
            onBeforeCapture: () =>
            {
                plugin.SetMainWindowHiddenForCapture(true);
                IsOpen = false;
            },
            onAfterCapture: () => plugin.SetMainWindowHiddenForCapture(false),
            onTempReady: tempPath => plugin.ScreenshotCrop.Begin(tempPath, cb),
            onError: ex =>
            {
                onConfirmed = cb;
                errorMessage = $"Capture failed: {ex.Message}";
                IsOpen = true;
                BringToFront();
            });
    }

    private static unsafe void TriggerGPoseToggle()
    {
        try
        {
            var rapture = RaptureShellModule.Instance();
            var uiModule = UIModule.Instance();
            if (rapture == null || uiModule == null)
                return;

            var cmd = Utf8String.FromString("/gpose");
            if (cmd == null)
                return;
            try { rapture->ExecuteCommandInner(cmd, uiModule); }
            finally { cmd->Dtor(true); }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to trigger /gpose");
        }
    }
}
