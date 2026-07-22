using System;
using System.Collections.Generic;
using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// A real window rather than an ImGui popup - a popup closes the instant you click anywhere else (e.g.
// to paste the code into a chat tell), which made it look like the share had died. This only closes
// via its own close button/X.
public sealed class ShareLiveWindow : Window, IDisposable
{
    private static readonly int[] TtlPresets = { 15, 30, 60 };

    private readonly Plugin plugin;
    private bool filterAvailable;
    private HashSet<Guid> filteredIds = new();
    private int ttlMinutes = 30;

    public ShareLiveWindow(Plugin plugin)
        : base("Share Directly##AetherfitShareLive", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.plugin = plugin;
        Size = new Vector2(340, 240);
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public void Show(bool hasFilter, HashSet<Guid> filtered)
    {
        filterAvailable = hasFilter;
        filteredIds = filtered;
        ttlMinutes = plugin.Configuration.LiveShareDefaultTtlMinutes;
        this.PositionNearMouse();
        IsOpen = true;
    }

    public override void OnClose() => plugin.LiveShare.Cancel();

    public override void Draw()
    {
        var live = plugin.LiveShare;

        if (live.Phase == LiveSharePhase.Idle)
        {
            ImGui.TextWrapped("Choose what to share and how long the code should last.");
            ImGui.Spacing();

            ImGui.TextDisabled("Expires after:");
            ImGui.SameLine();
            for (var i = 0; i < TtlPresets.Length; i++)
            {
                var minutes = TtlPresets[i];
                if (ImGui.RadioButton($"{minutes}m", ttlMinutes == minutes))
                    ttlMinutes = minutes;
                if (i < TtlPresets.Length - 1)
                    ImGui.SameLine();
            }
            ImGui.Spacing();

            if (ImGui.Button("Share All Designs", new Vector2(-1, 0)))
                Start(null);

            using (ImRaii.Disabled(!filterAvailable))
            {
                if (ImGui.Button("Share Filtered Designs", new Vector2(-1, 0)))
                    Start(filteredIds);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(filterAvailable
                    ? "Share only the designs currently shown by the active filters."
                    : "Set a filter first to share only the designs that remain visible.");
            return;
        }

        switch (live.Phase)
        {
            case LiveSharePhase.ExportingBundle:
                ImGui.TextWrapped("Preparing your gallery...");
                ImGui.ProgressBar(live.Progress, new Vector2(-1, 0));
                break;
            case LiveSharePhase.Uploading:
                ImGui.TextWrapped("Uploading...");
                ImGui.ProgressBar(live.Progress, new Vector2(-1, 0));
                break;
            case LiveSharePhase.Ready:
                ImGui.TextWrapped("Use the code below to share your design with other players.");
                ImGui.Spacing();
                ImGui.SetWindowFontScale(1.3f);
                ImGui.TextColored(UiTheme.GoldAccent, live.PairingCode ?? "??????");
                ImGui.SetWindowFontScale(1.0f);
                ImGui.SameLine();
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy))
                    ImGui.SetClipboardText(live.PairingCode ?? string.Empty);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Copy code");
                ImGui.Spacing();
                DrawExpiryCountdown(live);
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
        var finished = live.Phase is LiveSharePhase.Ready or LiveSharePhase.Failed;
        if (ImGui.Button(finished ? "Close" : "Cancel"))
            IsOpen = false;
    }

    private void Start(IReadOnlySet<Guid>? onlyIds)
    {
        if (plugin.Configuration.LiveShareDefaultTtlMinutes != ttlMinutes)
        {
            plugin.Configuration.LiveShareDefaultTtlMinutes = ttlMinutes;
            plugin.Configuration.Save();
        }

        var label = Plugin.PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(Plugin.PlayerState.CharacterName)
            ? Plugin.PlayerState.CharacterName
            : "Shared Gallery";
        plugin.LiveShare.HostAsync(label, onlyIds, ttlMinutes);
    }

    private static void DrawExpiryCountdown(GalleryLiveShareService live)
    {
        if (live.ExpiresAt is not { } expiresAt || live.RequestedTtlSeconds <= 0)
            return;

        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ImGui.TextDisabled("This code has expired.");
            return;
        }

        var fraction = (float)Math.Clamp(remaining.TotalSeconds / live.RequestedTtlSeconds, 0, 1);
        var label = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:D2} remaining";
        ImGui.ProgressBar(fraction, new Vector2(-1, 0), label);
    }
}
