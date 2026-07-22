using System.Collections.Generic;
using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private string receiveLiveCodeInput = string.Empty;

    private void OpenShareLiveDialog(IReadOnlySet<System.Guid>? onlyIds = null)
    {
        var label = Plugin.PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(Plugin.PlayerState.CharacterName)
            ? Plugin.PlayerState.CharacterName
            : "Shared Gallery";

        if (!plugin.LiveShare.IsBusy)
            plugin.LiveShare.HostAsync(label, onlyIds);
        ImGui.OpenPopup("##shareLivePopup");
    }

    private void OpenReceiveLiveDialog()
    {
        receiveLiveCodeInput = string.Empty;
        ImGui.OpenPopup("##receiveLivePopup");
    }

    private void DrawShareLivePopup()
    {
        using var popup = ImRaii.Popup("##shareLivePopup");
        if (!popup.Success)
            return;

        var live = plugin.LiveShare;
        ImGui.TextColored(UiTheme.GoldAccent, "Share Live");
        ImGui.Separator();
        ImGui.Spacing();

        switch (live.Phase)
        {
            case LiveSharePhase.WaitingForPeer:
                ImGui.TextWrapped("Give this code to the other player. Waiting for them to connect...");
                ImGui.Spacing();
                ImGui.SetWindowFontScale(1.3f);
                ImGui.TextColored(UiTheme.GoldAccent, live.PairingCode ?? "??????");
                ImGui.SetWindowFontScale(1.0f);
                ImGui.SameLine();
                if (ImGui.SmallButton("Copy"))
                    ImGui.SetClipboardText(live.PairingCode ?? string.Empty);
                break;
            case LiveSharePhase.ExportingBundle:
                ImGui.TextWrapped("Partner connected - preparing your gallery...");
                break;
            case LiveSharePhase.Handshaking:
                ImGui.TextWrapped("Establishing a direct connection...");
                break;
            case LiveSharePhase.Transferring:
                ImGui.TextWrapped("Sending...");
                ImGui.ProgressBar(live.Progress, new Vector2(-1, 0));
                break;
            case LiveSharePhase.Done:
                ImGui.TextColored(UiTheme.GoldAccent, "Sent!");
                break;
            case LiveSharePhase.Failed:
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(UiTheme.ErrorText, live.ErrorMessage ?? "Something went wrong.");
                ImGui.PopTextWrapPos();
                break;
        }

        ImGui.Spacing();
        var finished = live.Phase is LiveSharePhase.Done or LiveSharePhase.Failed or LiveSharePhase.Idle;
        if (ImGui.Button(finished ? "Close" : "Cancel"))
        {
            if (!finished)
                live.Cancel();
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawReceiveLivePopup()
    {
        using var popup = ImRaii.Popup("##receiveLivePopup");
        if (!popup.Success)
            return;

        var live = plugin.LiveShare;
        ImGui.TextColored(UiTheme.GoldAccent, "Receive Live");
        ImGui.Separator();
        ImGui.Spacing();

        if (live.Phase == LiveSharePhase.Idle)
        {
            ImGui.TextWrapped("Enter the pairing code the other player gave you.");
            ImGui.SetNextItemWidth(160f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##receiveLiveCode", "e.g. AB12CD", ref receiveLiveCodeInput, 16,
                ImGuiInputTextFlags.CharsUppercase);
            ImGui.SameLine();
            using (ImRaii.Disabled(receiveLiveCodeInput.Trim().Length == 0))
            {
                if (ImGui.Button("Connect"))
                    live.JoinAsync(receiveLiveCodeInput);
            }
            return;
        }

        switch (live.Phase)
        {
            case LiveSharePhase.Connecting:
                ImGui.TextWrapped("Connecting...");
                break;
            case LiveSharePhase.Handshaking:
                ImGui.TextWrapped("Establishing a direct connection...");
                break;
            case LiveSharePhase.Transferring:
                ImGui.TextWrapped("Receiving...");
                ImGui.ProgressBar(live.Progress, new Vector2(-1, 0));
                break;
            case LiveSharePhase.Importing:
                ImGui.TextWrapped("Importing...");
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
        var finished = live.Phase is LiveSharePhase.Done or LiveSharePhase.Failed;
        if (ImGui.Button(finished ? "Close" : "Cancel"))
        {
            if (!finished)
                live.Cancel();
            ImGui.CloseCurrentPopup();
        }
    }
}
