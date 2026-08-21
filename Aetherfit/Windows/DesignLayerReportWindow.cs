using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Designs;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public sealed class DesignLayerReportWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string filterText = string.Empty;
    private bool onlyShowWithLayers = true;
    private bool hideInheritOnlyBaseLayer = true;
    private readonly Dictionary<string, bool> entryOpenState = new();

    private List<DesignLayerReportService.ReportEntry> cachedEntries = new();
    private int cachedGeneration = -1;

    public DesignLayerReportWindow(Plugin plugin)
        : base("Design Layer Report##AetherfitDesignLayerReport")
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

    // Rebuilding walks every design's layer config - cheap, but no need to redo it every frame this
    // window happens to be open, only when the design list has actually changed underneath it.
    private void RefreshIfStale()
    {
        var generation = plugin.MainWindow.DesignListGeneration;
        if (cachedGeneration == generation)
            return;

        cachedEntries = plugin.LayerReport.BuildReport();
        cachedGeneration = generation;
    }

    public override void Draw()
    {
        if (!plugin.Configuration.EnableRandomLayers)
        {
            ImGui.TextWrapped("The Additional Design Layers feature is turned off - enable it in Settings, "
                + "under Design Layers, to configure and report on layers.");
            return;
        }

        RefreshIfStale();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##layerReportFilter", "Filter by design or layer name...", ref filterText, 128);

        ImGui.Checkbox("Only show designs with layers", ref onlyShowWithLayers);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide designs with no Base Design Layer and no Applied Before/After slots of their own.");
        ImGui.SameLine();
        ImGui.Checkbox("Hide designs only inheriting the Base Layer", ref hideInheritOnlyBaseLayer);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide a design whose only \"layer\" is the global Base Design Layer default it never "
                + "explicitly opted into (still set to Inherit) - keeps the list focused on designs with their own "
                + "explicit layer setup.");
        IEnumerable<DesignLayerReportService.ReportEntry> query = cachedEntries;
        if (onlyShowWithLayers)
            query = query.Where(e => e.HasOwnLayers);
        if (hideInheritOnlyBaseLayer)
            query = query.Where(e => !e.IsInheritOnlyBaseLayer);
        if (!string.IsNullOrWhiteSpace(filterText))
            query = query.Where(Matches);
        var filtered = query.ToList();

        ImGui.TextDisabled($"{filtered.Count} of {cachedEntries.Count} design(s) shown");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (filtered.Count == 0)
        {
            ImGui.TextDisabled(cachedEntries.Count == 0
                ? "No designs cached yet - refresh in the main window first."
                : "No designs match the current filters.");
        }
        else
        {
            foreach (var entry in filtered)
                DrawEntry(entry);
        }
    }

    private bool Matches(DesignLayerReportService.ReportEntry e)
    {
        bool NameMatch(string name) => name.Contains(filterText, StringComparison.OrdinalIgnoreCase);

        if (NameMatch(e.Name))
            return true;
        if (e.BaseDesignLayer != null && NameMatch(e.BaseDesignLayer.Name))
            return true;
        foreach (var slot in e.Before.Concat(e.After))
            foreach (var d in slot.Designs)
                if (NameMatch(d.Name))
                    return true;

        return false;
    }

    private void DrawEntry(DesignLayerReportService.ReportEntry e)
    {
        using var id = ImRaii.PushId(e.Id.ToString());
        var isOpen = Pills.DrawKeyedSubheader(entryOpenState, e.Name, e.Id.ToString());
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Click to expand/collapse\nShift + left-click to open in Aetherfit");
            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                plugin.MainWindow.OpenDesign(e.Id);
        }
        if (!isOpen)
            return;

        ImGui.Indent();

        if (e.BaseDesignLayer is { } baseLayer)
        {
            ImGui.TextDisabled(e.BaseDesignLayerIsOverride ? "Base Design Layer (override):" : "Base Design Layer:");
            ImGui.SameLine();
            DrawDesignLink(baseLayer);
        }

        DrawSlotSection("Applied Before", e.Before);
        DrawSlotSection("Applied After", e.After);

        if (e.UsedAsLayerBy.Count > 0)
        {
            ImGui.TextDisabled("Used as a layer by:");
            ImGui.Indent();
            foreach (var user in e.UsedAsLayerBy)
                DrawDesignLink(user);
            ImGui.Unindent();
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawSlotSection(string label, IReadOnlyList<DesignLayerReportService.LayerSlotInfo> slots)
    {
        if (slots.Count == 0)
            return;

        ImGui.TextDisabled($"{label}:");
        ImGui.Indent();
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            ImGui.TextDisabled(slot.Designs.Count > 1 ? $"Layer {i + 1} — random pick of {slot.Designs.Count}:" : $"Layer {i + 1}:");
            ImGui.SameLine();
            for (var d = 0; d < slot.Designs.Count; d++)
            {
                if (d > 0)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.TextDisabled(", ");
                    ImGui.SameLine(0, 0);
                }
                DrawDesignLink(slot.Designs[d]);
            }
        }
        ImGui.Unindent();
    }

    private void DrawDesignLink(DesignLayerReportService.LayerRef reference)
    {
        DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, reference.Name);
        if (!ImGui.IsItemHovered())
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetTooltip("Click to open in Aetherfit");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.MainWindow.OpenDesign(reference.DesignId);
    }
}
