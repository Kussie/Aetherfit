using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Designs;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public sealed class OrphanedModWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string ClearIgnoredPopupId = "Clear Ignored Orphaned Mods?##clearOrphanedModIgnores";

    public OrphanedModWindow(Plugin plugin)
        : base("Orphaned Mods##AetherfitOrphanedMods")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(900, 900),
        };
        Size = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    private OrphanedModService.Report cachedReport = new(true, null, Array.Empty<OrphanedModService.OrphanedMod>());
    private int cachedGeneration = -1;
    private int cachedIgnoreVersion = -1;
    private string newIgnoredFolderInput = string.Empty;

    // Penumbra's enabled-state has no version counter to react to - the Refresh button below covers
    // "I just toggled something in Penumbra while this window was open."
    private void RefreshIfStale()
    {
        var generation = plugin.MainWindow.DesignListGeneration;
        var ignoreVersion = plugin.Configuration.OrphanedModIgnoreVersion;
        if (cachedGeneration == generation && cachedIgnoreVersion == ignoreVersion)
            return;

        cachedReport = plugin.OrphanedMods.BuildReport();
        cachedGeneration = generation;
        cachedIgnoreVersion = ignoreVersion;
    }

    public override void Draw()
    {
        RefreshIfStale();
        var report = cachedReport;

        if (ImGui.Button("Refresh"))
            cachedReport = report = plugin.OrphanedMods.BuildReport();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-check Penumbra's current mod-enabled state");
        ImGui.Spacing();

        if (!report.Success)
        {
            ImGui.TextColored(UiTheme.ErrorText, report.Error);
            return;
        }

        ImGui.TextDisabled($"{report.OrphanedMods.Count} mod{(report.OrphanedMods.Count == 1 ? "" : "s")} enabled but unused by any design");
        ImGui.Spacing();

        if (report.OrphanedMods.Count == 0)
        {
            ImGui.TextDisabled("Nothing found.");
        }
        else
        {
            foreach (var mod in report.OrphanedMods)
                DrawOrphanedModRow(mod);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Clear Ignored"))
            ConfirmDialog.Open(ClearIgnoredPopupId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Un-ignore every dismissed mod - anything you've dismissed will start showing up again");

        if (ConfirmDialog.Draw(ClearIgnoredPopupId,
                "This will un-ignore every previously dismissed orphaned mod - anything you've dismissed "
                + "will start showing up again.",
                "Clear Ignored"))
        {
            plugin.Configuration.ClearIgnoredOrphanedMods();
        }

        DrawIgnoredFoldersSection();
    }

    private void DrawOrphanedModRow(OrphanedModService.OrphanedMod mod)
    {
        using var rowId = ImRaii.PushId(mod.Directory);
        var folder = ModFolder(mod.PenumbraPath);

        ImGui.TextUnformatted(mod.DisplayName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(mod.PenumbraPath ?? mod.Directory);

        // Icon glyphs aren't frame-height wide - measure each one rather than assuming, or the last
        // button in the row ends up positioned past the window's edge and clipped.
        float openW, folderW, ignoreW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            var pad = ImGui.GetStyle().FramePadding.X * 2;
            openW = ImGui.CalcTextSize(FontAwesomeIcon.ExternalLinkAlt.ToIconString()).X + pad;
            folderW = ImGui.CalcTextSize(FontAwesomeIcon.FolderMinus.ToIconString()).X + pad;
            ignoreW = ImGui.CalcTextSize(FontAwesomeIcon.EyeSlash.ToIconString()).X + pad;
        }
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;

        ImGui.SameLine(ImGui.GetContentRegionMax().X - openW - folderW - ignoreW - (spacing * 2));
        if (ImGuiComponents.IconButton("openInPenumbra", FontAwesomeIcon.ExternalLinkAlt))
            plugin.Penumbra.OpenMod(mod.Directory, mod.DisplayName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open in Penumbra");

        ImGui.SameLine(0, spacing);
        using (ImRaii.Disabled(folder == null))
        {
            if (ImGuiComponents.IconButton("ignoreOrphanedModFolder", FontAwesomeIcon.FolderMinus) && folder != null)
                plugin.Configuration.IgnoreOrphanedModPath(folder);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(folder != null
                ? $"Ignore every mod in Penumbra's \"{folder}\" folder"
                : "This mod isn't in a Penumbra folder");

        ImGui.SameLine(0, spacing);
        if (ImGuiComponents.IconButton("ignoreOrphanedMod", FontAwesomeIcon.EyeSlash))
            plugin.Configuration.IgnoreOrphanedMod(mod.Directory);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ignore — won't show in this report again");
    }

    // Everything before the last path segment of a mod's Penumbra sort-order path - null if the mod
    // isn't filed under a folder there at all.
    private static string? ModFolder(string? penumbraPath)
    {
        if (string.IsNullOrEmpty(penumbraPath))
            return null;
        var slash = penumbraPath.LastIndexOf('/');
        return slash > 0 ? penumbraPath[..slash] : null;
    }

    private void DrawIgnoredFoldersSection()
    {
        // A copy - the Trash button below mutates the live list mid-loop (RemoveIgnoredOrphanedModPath).
        var paths = plugin.Configuration.IgnoredOrphanedModPaths.ToList();

        ImGui.Spacing();
        ImGui.TextDisabled("Ignored folders:");
        foreach (var path in paths)
        {
            using var rowId = ImRaii.PushId(path);
            if (ImGuiComponents.IconButton("removeIgnoredFolder", FontAwesomeIcon.Trash))
                plugin.Configuration.RemoveIgnoredOrphanedModPath(path);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Stop ignoring this folder");
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(path);
        }

        float addW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            addW = ImGui.CalcTextSize(FontAwesomeIcon.Plus.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;

        ImGui.SetNextItemWidth(-addW - spacing);
        var submitted = ImGui.InputTextWithHint("##addIgnoredFolder", "Add a folder to ignore, e.g. Upscales",
            ref newIgnoredFolderInput, 260, ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = newIgnoredFolderInput.Trim();

        ImGui.SameLine(0, spacing);
        using (ImRaii.Disabled(trimmed.Length == 0))
        {
            if (ImGuiComponents.IconButton("addIgnoredFolder", FontAwesomeIcon.Plus) || (submitted && trimmed.Length > 0))
            {
                plugin.Configuration.IgnoreOrphanedModPath(trimmed);
                newIgnoredFolderInput = string.Empty;
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Matches this path and anything nested under it, whether or not it's currently showing as orphaned");
    }
}
