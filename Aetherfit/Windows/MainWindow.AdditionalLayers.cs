using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    private const string SlotDragType = "AF_LAYER_SLOT";
    private const string DesignDragType = "AF_LAYER_DESIGN";

    private bool additionalLayersPanelOpen = true;
    private Guid? layerPickerSelection;
    private string layerPickerFilter = string.Empty;
    private string slotPickerFilter = string.Empty;

    // Index state for an in-flight drag; the payload itself is just a marker. Only read while a drop of the
    // matching payload type is being delivered, so stale values from a finished drag are never consumed.
    private int draggedSlot = -1;
    private (int Slot, int Design) draggedDesign = (-1, -1);

    // Structural change requested by a row this frame (reorder/move/remove/add), executed after all slots
    // have drawn so we never mutate the list mid-iteration.
    private Action? pendingLayerEdit;

    private void DrawAdditionalLayersPanel(Guid id)
    {
        if (!DrawCollapsibleSubheader("Additional Design Layers", ref additionalLayersPanelOpen))
            return;

        ImGui.Indent();

        if (!plugin.Configuration.AdditionalLayersHelpDismissed)
            DrawLayersHelpBox();

        var slots = plugin.Configuration.GetLayerSlots(id);
        DrawLayerPicker(id, slots);
        ImGui.Spacing();

        if (slots.Count == 0)
        {
            ImGui.TextDisabled("No layers yet. Pick a design above and click Add Layer.");
        }
        else
        {
            ImGui.TextDisabled("Layers are applied top to bottom, using only designs that match your current job.");
            ImGui.TextDisabled("When several designs in a layer match, one is picked at random. Drag a layer onto another to reorder it.");
            for (var i = 0; i < slots.Count; i++)
                DrawLayerSlot(id, slots, i);
            DrawNewSlotDropZone(slots);

            if (pendingLayerEdit is { } edit)
            {
                pendingLayerEdit = null;
                edit();
                plugin.Configuration.SetLayerSlots(id, slots);
                plugin.Configuration.Save();
            }
        }

        ImGui.Spacing();
        DrawAssociatedBaseOutfits(id);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Intro blurb for first-time users; the close button hides it for good.
    private void DrawLayersHelpBox()
    {
        const string helpText =
            "Layers apply extra designs on top of this one whenever it is applied, working top to bottom. "
            + "A layer can hold several designs — only those matching your current job are considered, and "
            + "when more than one qualifies, a single design is chosen at random each time. "
            + "Useful for accessories, job variants, or randomised extras.";

        var style = ImGui.GetStyle();
        var pad = 8f * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var closeSize = ImGui.GetFrameHeight();
        var wrapW = availW - (pad * 2) - closeSize - style.ItemSpacing.X;
        var textH = ImGui.CalcTextSize(helpText, false, wrapW).Y;
        var boxH = Math.Max(textH, closeSize) + (pad * 2);

        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(availW, boxH),
            ImGui.ColorConvertFloat4ToU32(UiTheme.ToggleOffBg), 4f);

        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapW);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(new Vector2(
            start.X + availW - closeSize - (pad * 0.5f), start.Y + (pad * 0.5f)));
        if (HeaderIconButton("layersHelpClose", FontAwesomeIcon.Times, UiTheme.PlaceholderText,
                new Vector2(closeSize, closeSize)))
        {
            plugin.Configuration.AdditionalLayersHelpDismissed = true;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dismiss (won't be shown again)");

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + boxH));
        ImGui.Spacing();
    }

    private void DrawLayerPicker(Guid id, List<DesignLayerSlot> slots)
    {
        var existing = slots.SelectMany(s => s.Designs).Select(l => l.DesignId).ToHashSet();
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
                slots.Add(new DesignLayerSlot { Designs = { new DesignLayer { DesignId = pick } } });
                plugin.Configuration.SetLayerSlots(id, slots);
                plugin.Configuration.Save();
                layerPickerSelection = null;
                layerPickerFilter = string.Empty;
            }
        }
    }

    private void DrawLayerSlot(Guid baseId, List<DesignLayerSlot> slots, int index)
    {
        var slot = slots[index];
        using var slotId = ImRaii.PushId(index);

        var label = $"Layer {index + 1}";
        if (slot.Designs.Count > 1)
        {
            label += $" — random pick of {slot.Designs.Count}";
            if (Plugin.PlayerState.IsLoaded)
            {
                var jobId = Plugin.PlayerState.ClassJob.RowId;
                var matching = slot.Designs.Count(l => l.AllJobs || l.Jobs.Contains(jobId));
                label += matching switch
                {
                    0 => " (none match your current job - skipped)",
                    1 => " (only 1 matches your current job)",
                    _ when matching == slot.Designs.Count => "",
                    _ => $" ({matching} match your current job)",
                };
            }
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextDisabled(FontAwesomeIcon.GripVertical.ToIconString());
        ImGui.SameLine();
        ImGui.Selectable(label);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drag to reorder. Drop a design here to add it to this layer's random pick.\n"
                             + "Only designs matching your current job are rolled - if just one matches, it is\n"
                             + "applied outright, and if none match the layer is skipped.");

        if (ImGui.BeginDragDropSource())
        {
            draggedSlot = index;
            ImGui.SetDragDropPayload(SlotDragType, ReadOnlySpan<byte>.Empty);
            ImGui.TextUnformatted(label);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            if (AcceptDragPayload(SlotDragType) && draggedSlot >= 0 && draggedSlot != index)
            {
                var from = draggedSlot;
                pendingLayerEdit = () =>
                {
                    var moved = slots[from];
                    slots.RemoveAt(from);
                    slots.Insert(index, moved);
                };
            }

            if (AcceptDragPayload(DesignDragType) && draggedDesign.Slot >= 0 && draggedDesign.Slot != index)
            {
                var (fromSlot, fromDesign) = draggedDesign;
                pendingLayerEdit = () =>
                {
                    var moved = slots[fromSlot].Designs[fromDesign];
                    slots[fromSlot].Designs.RemoveAt(fromDesign);
                    slot.Designs.Add(moved);
                };
            }

            ImGui.EndDragDropTarget();
        }

        ImGui.Indent();
        for (var d = 0; d < slot.Designs.Count; d++)
            DrawLayerDesignRow(slots, index, d);
        DrawAddToSlotButton(baseId, slots, slot);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawLayerDesignRow(List<DesignLayerSlot> slots, int slotIndex, int designIndex)
    {
        var slot = slots[slotIndex];
        var layer = slot.Designs[designIndex];
        using var rowId = ImRaii.PushId(layer.DesignId.ToString());

        var name = ResolveLinkedDesignName(layer.DesignId);
        DesignDetailView.TextColoredUnformatted(ModLinkColor, name);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Double-click to apply\nShift + left-click to open in Aetherfit\nShift + right-click to open in Glamourer\nDrag onto another layer to group them into a random pick");

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyShift)
                ApplyDesignById(layer.DesignId);

            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                selectedDesign = layer.DesignId;
                coverMode = false;
            }
            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                plugin.Glamourer.OpenInGlamourer(layer.DesignId, name);
        }

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
        {
            draggedDesign = (slotIndex, designIndex);
            ImGui.SetDragDropPayload(DesignDragType, ReadOnlySpan<byte>.Empty);
            ImGui.TextUnformatted(name);
            ImGui.EndDragDropSource();
        }

        ImGui.Indent();
        var trashWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - trashWidth - ImGui.GetStyle().ItemSpacing.X);
        if (DrawJobMultiselect(layer))
            plugin.Configuration.Save();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
            pendingLayerEdit = () => slot.Designs.Remove(layer);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove this design from the layer");
        ImGui.Unindent();
    }

    private void DrawAddToSlotButton(Guid baseId, List<DesignLayerSlot> slots, DesignLayerSlot slot)
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
        {
            slotPickerFilter = string.Empty;
            ImGui.OpenPopup("##addToSlot");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add a design to this layer - when the layer is applied, one design matching your\ncurrent job is picked at random.");

        using var popup = ImRaii.Popup("##addToSlot");
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##slotFilter", "Filter by name...", ref slotPickerFilter, 64);
        ImGui.Separator();

        var existing = slots.SelectMany(s => s.Designs).Select(l => l.DesignId).ToHashSet();
        foreach (var (designId, name) in AllDesignsSorted())
        {
            if (designId == baseId || existing.Contains(designId))
                continue;
            if (slotPickerFilter.Length > 0 && !name.Contains(slotPickerFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{name}##slotAdd{designId}"))
            {
                pendingLayerEdit = () => slot.Designs.Add(new DesignLayer { DesignId = designId });
                ImGui.CloseCurrentPopup();
            }
        }
    }

    // A drop target below the last slot: dropping a design here splits it out into its own new layer, and
    // dropping a layer here moves it to the bottom of the stack.
    private void DrawNewSlotDropZone(List<DesignLayerSlot> slots)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.Selectable("Drop a design here to move it into its own new layer##newSlotZone");

        if (!ImGui.BeginDragDropTarget())
            return;

        if (AcceptDragPayload(SlotDragType) && draggedSlot >= 0 && draggedSlot != slots.Count - 1)
        {
            var from = draggedSlot;
            pendingLayerEdit = () =>
            {
                var moved = slots[from];
                slots.RemoveAt(from);
                slots.Add(moved);
            };
        }

        if (AcceptDragPayload(DesignDragType) && draggedDesign.Slot >= 0)
        {
            var (fromSlot, fromDesign) = draggedDesign;
            pendingLayerEdit = () =>
            {
                var moved = slots[fromSlot].Designs[fromDesign];
                slots[fromSlot].Designs.RemoveAt(fromDesign);
                slots.Add(new DesignLayerSlot { Designs = { moved } });
            };
        }

        ImGui.EndDragDropTarget();
    }

    private static bool AcceptDragPayload(string type)
        => !ImGui.AcceptDragDropPayload(type).IsNull;

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
        var associations = plugin.Configuration.DesignLayerSlots
            .Select(kv => (BaseId: kv.Key, Layer: kv.Value.SelectMany(s => s.Designs).FirstOrDefault(l => l.DesignId == id)))
            .Where(t => t.Layer != null && plugin.Configuration.CachedOutfits.ContainsKey(t.BaseId))
            .ToList();

        if (associations.Count == 0)
            return;

        DrawSubheader("Associated Base Outfits");
        ImGui.Indent();
        ImGui.TextDisabled("This design's own layers are not applied when it is used as a layer.");

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
        foreach (var baseId in plugin.Configuration.DesignLayerSlots.Keys.ToList())
        {
            if (!validIds.Contains(baseId))
            {
                plugin.Configuration.DesignLayerSlots.Remove(baseId);
                continue;
            }

            var slots = plugin.Configuration.DesignLayerSlots[baseId];
            foreach (var slot in slots)
                slot.Designs.RemoveAll(l => !validIds.Contains(l.DesignId));
            slots.RemoveAll(s => s.Designs.Count == 0);
            if (slots.Count == 0)
                plugin.Configuration.DesignLayerSlots.Remove(baseId);
        }
    }
}
