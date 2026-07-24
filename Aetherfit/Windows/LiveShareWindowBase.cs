using System;
using System.Numerics;
using Aetherfit.Services.Sharing;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public abstract class LiveShareWindowBase : Window, IDisposable
{
    protected readonly Plugin plugin;

    protected LiveShareWindowBase(Plugin plugin, string windowName)
        : base(windowName, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.plugin = plugin;
        Size = new Vector2(340, 240);
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public override void OnClose() => plugin.LiveShare.Cancel();

    // Positions near the mouse and opens - callers reset their own local state first.
    protected void ShowNearMouse()
    {
        this.PositionNearMouse();
        IsOpen = true;
    }

    protected static void DrawFailedPhase(GalleryLiveShareService live)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(UiTheme.ErrorText, live.ErrorMessage ?? "Something went wrong.");
        ImGui.PopTextWrapPos();
    }

    protected void DrawFinishedButtons(bool finished)
    {
        ImGui.Spacing();
        if (ImGui.Button(finished ? "Close" : "Cancel"))
            IsOpen = false;
    }
}
