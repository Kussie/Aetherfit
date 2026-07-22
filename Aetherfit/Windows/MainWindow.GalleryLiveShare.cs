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
    private bool shareLiveFilterAvailable;
    private HashSet<System.Guid> shareLiveFilteredIds = new();

    // OpenPopup for these can't be called directly from the Selectable click - at that point we're
    // still inside the enclosing dropdown's own popup scope, so ImGui treats the new popup as a child
    // of it and closes it immediately. Deferred to the root draw scope instead (same fix already used
    // by ForeignGalleryWindow's details popup).
    private bool openShareLiveRequested;
    private bool openReceiveLiveRequested;

    private void OpenShareLiveDialog()
    {
        // Snapshot the current filter at click-time, same as OpenExportGalleryDialog(CollectVisibleDesignIds())
        // does for the plain export - the modal may sit open for a while and shouldn't chase a filter
        // that keeps changing underneath it.
        shareLiveFilterAvailable = HasAnyFilter;
        shareLiveFilteredIds = CollectVisibleDesignIds();
        openShareLiveRequested = true;
    }

    private void OpenReceiveLiveDialog()
    {
        receiveLiveCodeInput = string.Empty;
        openReceiveLiveRequested = true;
    }

    private void StartShareLive(IReadOnlySet<System.Guid>? onlyIds)
    {
        var label = Plugin.PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(Plugin.PlayerState.CharacterName)
            ? Plugin.PlayerState.CharacterName
            : "Shared Gallery";
        plugin.LiveShare.HostAsync(label, onlyIds);
    }

    // The "Open Shared Gallery" dropdown: a local file, or a live pull from another online player.
    private void DrawOpenGalleryPopup()
    {
        using var popup = ImRaii.Popup("##openGalleryPopup");
        if (!popup.Success)
            return;

        var galleryBusy = plugin.GallerySharing.IsBusy;
        using (ImRaii.Disabled(galleryBusy))
        {
            if (ImGui.Selectable("From File..."))
                OpenImportGalleryDialog();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(galleryBusy
                ? "An export or import is already running."
                : "Open another user's exported .afgallery file in a read-only viewer.");

        ImGui.Separator();

        var liveBusy = plugin.LiveShare.IsBusy;
        var liveConfigured = !string.IsNullOrWhiteSpace(plugin.Configuration.SignalingServerUrl);
        using (ImRaii.Disabled(liveBusy || !liveConfigured))
        {
            if (ImGui.Selectable("Receive Live..."))
                OpenReceiveLiveDialog();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(liveBusy
                ? "A live share is already running."
                : !liveConfigured
                    ? "Set a signaling server address in Settings first."
                    : "Receive a gallery directly from another online player.");
    }

    private const float LiveSharePopupWidth = 260f;

    private void DrawShareLivePopup()
    {
        ImGui.SetNextWindowSize(new Vector2(LiveSharePopupWidth * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Appearing);
        using var popup = ImRaii.Popup("##shareLivePopup");
        if (!popup.Success)
            return;

        var live = plugin.LiveShare;
        ImGui.TextColored(UiTheme.GoldAccent, "Share Live");
        ImGui.Separator();
        ImGui.Spacing();

        if (live.Phase == LiveSharePhase.Idle)
        {
            ImGui.TextWrapped("Choose what to share, then generate a pairing code for the other player.");
            ImGui.Spacing();

            if (ImGui.Button("Share All Designs", new Vector2(-1, 0)))
                StartShareLive(null);

            using (ImRaii.Disabled(!shareLiveFilterAvailable))
            {
                if (ImGui.Button("Share Filtered Designs", new Vector2(-1, 0)))
                    StartShareLive(shareLiveFilteredIds);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(shareLiveFilterAvailable
                    ? "Share only the designs currently shown by the active filters."
                    : "Set a filter first to share only the designs that remain visible.");

            ImGui.Spacing();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            return;
        }

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
        var finished = live.Phase is LiveSharePhase.Done or LiveSharePhase.Failed;
        if (ImGui.Button(finished ? "Close" : "Cancel"))
        {
            if (!finished)
                live.Cancel();
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawReceiveLivePopup()
    {
        ImGui.SetNextWindowSize(new Vector2(LiveSharePopupWidth * ImGuiHelpers.GlobalScale, 0), ImGuiCond.Appearing);
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
