using System;
using System.Numerics;
using Aetherfit.Services.Screenshots;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// A tiny floating toolbar for creating designs without the main window open: Revert, Create (the same
// "From Worn"/"From Code" menu as the tree leaf), Snap (set the just-created design's cover from the
// game), and a shortcut to open Aetherfit straight to whatever this window last created.
public sealed class QuickCreateWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Guid? lastCreatedDesignId;

    public QuickCreateWindow(Plugin plugin)
        : base("Aetherfit Quick Create##AetherfitQuickCreate",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        plugin.MainWindow.DesignCreated += OnDesignCreated;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(220, 0) * ImGuiHelpers.GlobalScale,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() => plugin.MainWindow.DesignCreated -= OnDesignCreated;

    private void OnDesignCreated(Guid id) => lastCreatedDesignId = id;

    public override void Draw()
    {
        // Needs an active window to attach the popups to - this window counts just as well as
        // MainWindow's own, and the frame guard keeps it from double-drawing if both are open.
        plugin.MainWindow.DrawCreateOverlaysOnce();

        if (ImGuiComponents.IconButton("qcRevert", FontAwesomeIcon.Undo))
            plugin.DesignApply.RevertAppearance();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Revert Appearance");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("qcCreate", FontAwesomeIcon.Plus))
            plugin.MainWindow.OpenCreateNewDesignMenu(showExtras: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Create Design");

        ImGui.SameLine();
        using (ImRaii.Disabled(lastCreatedDesignId == null))
        {
            if (ImGuiComponents.IconButton("qcSnap", FontAwesomeIcon.Camera) && lastCreatedDesignId is { } snapId)
                plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.SetCover(snapId, croppedPath));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(lastCreatedDesignId == null ? "Create a design first" : "Snap - set its cover from the game");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("qcOpen", FontAwesomeIcon.WindowMaximize))
        {
            if (lastCreatedDesignId is { } openId)
                plugin.MainWindow.OpenDesign(openId);
            else
            {
                plugin.MainWindow.IsOpen = true;
                plugin.MainWindow.BringToFront();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open Aetherfit");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (lastCreatedDesignId is { } id)
        {
            var hasCover = plugin.ImageStorage.GetCoverPath(id) != null;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                ImGui.TextColored(hasCover ? UiTheme.StateOn : UiTheme.ErrorText, FontAwesomeIcon.Camera.ToIconString());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(hasCover ? "Has a cover screenshot" : "No cover screenshot yet");

            ImGui.SameLine();
            ImGui.TextColored(UiTheme.GoldAccent, plugin.Configuration.ResolveDesignName(id));
        }
        else
        {
            ImGui.TextDisabled("No design saved yet");
        }
    }
}
