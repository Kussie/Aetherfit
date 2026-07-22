using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// A real window rather than an ImGui popup - see ShareLiveWindow for why (doesn't close on click-away).
public sealed class ReceiveLiveWindow : Window, System.IDisposable
{
    private readonly Plugin plugin;
    private string codeInput = string.Empty;

    public ReceiveLiveWindow(Plugin plugin)
        : base("Directly Shared##AetherfitReceiveLive", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.plugin = plugin;
        Size = new Vector2(340, 240);
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public void Show()
    {
        codeInput = string.Empty;
        this.PositionNearMouse();
        IsOpen = true;
    }

    public override void OnClose() => plugin.LiveShare.Cancel();

    public override void Draw()
    {
        var live = plugin.LiveShare;

        if (live.Phase == LiveSharePhase.Idle)
        {
            ImGui.TextWrapped("Enter the pairing code the other player gave you.");
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##receiveLiveCode", "e.g. AB12CD", ref codeInput, 16,
                ImGuiInputTextFlags.CharsUppercase);
            ImGui.Spacing();

            using (ImRaii.Disabled(codeInput.Trim().Length == 0))
            {
                if (ImGui.Button("Connect", new Vector2(-1, 0)))
                    live.JoinAsync(codeInput);
            }
            return;
        }

        switch (live.Phase)
        {
            case LiveSharePhase.Downloading:
                ImGui.TextWrapped("Downloading...");
                ImGui.ProgressBar(live.Progress, new Vector2(-1, 0));
                break;
            case LiveSharePhase.Importing:
                ImGui.TextWrapped("Importing...");
                ImGui.ProgressBar(live.Progress, new Vector2(-1, 0));
                break;
            case LiveSharePhase.Done:
                ImGui.TextColored(UiTheme.GoldAccent, "Received! Check the viewer window that opened.");
                break;
            case LiveSharePhase.Failed:
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(UiTheme.ErrorText, live.ErrorMessage ?? "Something went wrong.");
                ImGui.PopTextWrapPos();
                break;
        }

        ImGui.Spacing();
        // OnClose (native X) already resets state via live.Cancel() - the button just needs to close
        // the window and let that handle it, so both paths behave identically.
        var finished = live.Phase is LiveSharePhase.Done or LiveSharePhase.Failed;
        if (ImGui.Button(finished ? "Close" : "Cancel"))
            IsOpen = false;
    }
}
