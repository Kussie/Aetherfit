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

public sealed class HealthReportWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string ClearIgnoredPopupId = "Clear Ignored Health Report Findings?##clearHealthIgnores";

    private bool missingModsOpen = true;
    private bool brokenItemsOpen = true;
    private bool incompatibleOpen = true;
    private bool duplicatesOpen = true;

    public HealthReportWindow(Plugin plugin)
        : base("Health Report##AetherfitHealthReport")
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

    public override void Draw()
    {
        // Recomputed every frame while this window is open (not while it's closed - see the plain,
        // un-badged toolbar button in MainWindow.DrawTopToolbar). Each check is cheap hash-set/
        // dictionary work over what's already cached in memory; revisit only if that stops being true.
        var missingMods = plugin.HealthReport.FindMissingModAssociations();
        var brokenItems = plugin.HealthReport.FindBrokenItems();
        var incompatible = plugin.HealthReport.FindIncompatibleDesigns();
        var duplicates = plugin.HealthReport.FindDuplicates();

        DrawFindingSection("Missing Mod Associations", ref missingModsOpen, missingMods, finding =>
        {
            var detail = string.Join(", ", finding.MissingMods.Select(DesignAttributionService.ModDisplayName));
            DrawFindingRow(finding.Id, finding.Name, detail, HealthReportService.MissingModCheck);
        });
        ImGui.Spacing();

        DrawFindingSection("Broken Items", ref brokenItemsOpen, brokenItems, finding =>
            DrawFindingRow(finding.Id, finding.Name, string.Join(", ", finding.BrokenSlots), HealthReportService.BrokenItemCheck));
        ImGui.Spacing();

        DrawFindingSection("Incompatible Items", ref incompatibleOpen, incompatible, finding =>
            DrawFindingRow(finding.Id, finding.Name, string.Join(", ", finding.IncompatibleSlots), HealthReportService.IncompatibleCheck));
        ImGui.Spacing();

        DrawFindingSection("Duplicate Designs", ref duplicatesOpen, duplicates, DrawDuplicateGroup);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Clear Ignored"))
            ConfirmDialog.Open(ClearIgnoredPopupId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Un-ignore every dismissed finding across every section above");

        if (ConfirmDialog.Draw(ClearIgnoredPopupId,
                "This will un-ignore every previously dismissed Health Report finding across every "
                + "section above - anything you've dismissed will start showing up again.",
                "Clear Ignored"))
        {
            plugin.Configuration.ClearIgnoredHealthChecks();
        }
    }

    private static void DrawFindingSection<T>(string label, ref bool open, IReadOnlyList<T> items, Action<T> drawItem)
    {
        if (!Pills.DrawCollapsibleSubheader($"{label} ({items.Count})", ref open))
            return;

        ImGui.Indent();
        if (items.Count == 0)
            ImGui.TextDisabled("No issues found.");
        else
            foreach (var item in items)
                drawItem(item);
        ImGui.Unindent();
    }

    private void DrawFindingRow(Guid id, string name, string detail, string checkType)
    {
        using var rowId = ImRaii.PushId($"{id}{checkType}");
        DrawDesignNameCell(id, name);
        ImGui.SameLine();
        ImGui.TextDisabled($"— {detail}");
        DrawIgnoreButton(id, checkType);
    }

    private void DrawDuplicateGroup(HealthReportService.DuplicateGroup group)
    {
        ImGui.TextDisabled("Identical equipment:");
        ImGui.Indent();
        foreach (var (id, name) in group.Designs)
        {
            using var rowId = ImRaii.PushId($"{id}dup");
            DrawDesignNameCell(id, name);
            DrawIgnoreButton(id, HealthReportService.DuplicateCheck);
        }
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawDesignNameCell(Guid id, string name)
    {
        DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, name);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Click to open in Aetherfit");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                plugin.MainWindow.OpenDesign(id);
        }
    }

    private void DrawIgnoreButton(Guid id, string checkType)
    {
        var width = ImGui.GetFrameHeight();
        ImGui.SameLine(ImGui.GetContentRegionMax().X - width);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.EyeSlash))
            plugin.Configuration.IgnoreHealthCheck(id, checkType);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ignore — won't show in this report again");
    }
}
