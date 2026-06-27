using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private bool randomLayersPanelOpen = true;
    private Guid? layerPickerSelection;
    private string layerPickerFilter = string.Empty;

    private void DrawRandomLayersPanel(Guid id)
    {
        if (!DrawCollapsibleSubheader("Random Layer Designs", ref randomLayersPanelOpen))
            return;

        ImGui.Indent();

        var layers = plugin.Configuration.GetLayers(id);
        DrawLayerPicker(id, layers);
        ImGui.Spacing();

        if (layers.Count == 0)
            ImGui.TextDisabled("No layers yet. Pick a design above and click Add Layer.");
        else
            foreach (var layer in layers.ToList())
                DrawLayerRow(id, layers, layer);

        ImGui.Spacing();
        DrawAssociatedBaseOutfits(id);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawLayerPicker(Guid id, List<DesignLayer> layers)
    {
        var existing = layers.Select(l => l.DesignId).ToHashSet();
        var preview = layerPickerSelection is { } sel ? ResolveLinkedDesignName(sel) : "Select a design...";

        var style = ImGui.GetStyle();
        var addWidth = ImGui.CalcTextSize("Add Layer").X + (style.FramePadding.X * 2);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - addWidth - style.ItemSpacing.X);

        using (var combo = ImRaii.Combo("##layerPicker", preview))
        {
            if (combo.Success)
            {
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##layerFilter", "Filter by name...", ref layerPickerFilter, 64);
                ImGui.Separator();

                foreach (var (designId, name) in AllDesignsSorted())
                {
                    if (designId == id || existing.Contains(designId))
                        continue;
                    if (layerPickerFilter.Length > 0 && !name.Contains(layerPickerFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (ImGui.Selectable($"{name}##layer{designId}"))
                        layerPickerSelection = designId;
                }
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(layerPickerSelection is null))
        {
            if (ImGui.Button("Add Layer") && layerPickerSelection is { } pick)
            {
                layers.Add(new DesignLayer { DesignId = pick });
                plugin.Configuration.SetLayers(id, layers);
                plugin.Configuration.Save();
                layerPickerSelection = null;
                layerPickerFilter = string.Empty;
            }
        }
    }

    private void DrawLayerRow(Guid baseId, List<DesignLayer> layers, DesignLayer layer)
    {
        using var rowId = ImRaii.PushId(layer.DesignId.ToString());

        var name = ResolveLinkedDesignName(layer.DesignId);
        DesignDetailView.TextColoredUnformatted(ModLinkColor, name);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Shift + left-click to open in Aetherfit\nShift + right-click to open in Glamourer");

            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                selectedDesign = layer.DesignId;
                coverMode = false;
            }
            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                plugin.Glamourer.OpenInGlamourer(layer.DesignId, name);
        }

        ImGui.Indent();
        var trashWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - trashWidth - ImGui.GetStyle().ItemSpacing.X);
        if (DrawJobMultiselect(layer))
            plugin.Configuration.Save();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
        {
            layers.Remove(layer);
            plugin.Configuration.SetLayers(baseId, layers);
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove layer");
        ImGui.Unindent();
    }

    private bool DrawJobMultiselect(DesignLayer layer)
    {
        using var combo = ImRaii.Combo("##jobs", JobSummary(layer));
        if (!combo.Success)
            return false;

        var changed = false;
        var allJobs = layer.AllJobs;
        if (ImGui.Checkbox("All Jobs", ref allJobs))
        {
            layer.AllJobs = allJobs;
            if (allJobs)
                layer.Jobs.Clear();
            changed = true;
        }

        ImGui.Separator();
        using (ImRaii.Disabled(layer.AllJobs))
        {
            JobRole? lastRole = null;
            foreach (var job in plugin.GameData.GetSelectableJobs())
            {
                if (lastRole != job.Role)
                {
                    if (lastRole != null)
                        ImGui.Spacing();
                    ImGui.TextDisabled(GameDataService.RoleLabel(job.Role));
                    lastRole = job.Role;
                }

                var on = layer.Jobs.Contains(job.RowId);
                if (ImGui.Checkbox($"{job.Name}##job{job.RowId}", ref on))
                {
                    if (on)
                        layer.Jobs.Add(job.RowId);
                    else
                        layer.Jobs.Remove(job.RowId);
                    changed = true;
                }
            }
        }

        return changed;
    }

    private void DrawAssociatedBaseOutfits(Guid id)
    {
        var associations = plugin.Configuration.DesignLayers
            .Select(kv => (BaseId: kv.Key, Layer: kv.Value.FirstOrDefault(l => l.DesignId == id)))
            .Where(t => t.Layer != null && plugin.Configuration.CachedOutfits.ContainsKey(t.BaseId))
            .ToList();

        if (associations.Count == 0)
            return;

        DrawSubheader("Associated Base Outfits");
        ImGui.Indent();
        ImGui.TextDisabled("This design's own layers are not rolled when it is applied as a layer.");

        foreach (var (baseId, layer) in associations)
        {
            var name = ResolveLinkedDesignName(baseId);
            DesignDetailView.TextColoredUnformatted(ModLinkColor, name);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Click to open in Aetherfit");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    selectedDesign = baseId;
                    coverMode = false;
                }
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"— {JobSummary(layer!)}");
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private IEnumerable<(Guid Id, string Name)> AllDesignsSorted()
        => plugin.Configuration.CachedOutfits
            .Select(kv => (kv.Key, kv.Value.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

    private string JobSummary(DesignLayer layer)
    {
        if (layer.AllJobs)
            return "All Jobs";
        if (layer.Jobs.Count == 0)
            return "(no jobs)";
        return string.Join(", ", layer.Jobs.Select(plugin.GameData.ResolveJobName));
    }

    private void CleanupStaleLayers(HashSet<Guid> validIds)
    {
        foreach (var baseId in plugin.Configuration.DesignLayers.Keys.ToList())
        {
            if (!validIds.Contains(baseId))
            {
                plugin.Configuration.DesignLayers.Remove(baseId);
                continue;
            }

            var layers = plugin.Configuration.DesignLayers[baseId];
            layers.RemoveAll(l => !validIds.Contains(l.DesignId));
            if (layers.Count == 0)
                plugin.Configuration.DesignLayers.Remove(baseId);
        }
    }
}
