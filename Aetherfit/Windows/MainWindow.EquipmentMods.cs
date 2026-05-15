using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private static readonly Vector4 OnColor = new(0.30f, 0.78f, 0.30f, 1.0f);
    private static readonly Vector4 OffColor = new(0.88f, 0.32f, 0.32f, 1.0f);
    private static readonly Vector4 UnsetColor = new(0.55f, 0.55f, 0.55f, 1.0f);
    private static readonly Vector4 AppliedTextColor = new(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Vector4 SectionHeader = new(0.85f, 0.85f, 0.85f, 1.0f);
    private static readonly Vector4 ModLinkColor = new(0.55f, 0.78f, 1.0f, 1.0f);

    private bool equipmentPanelOpen = true;
    private bool modsPanelOpen = true;

    private static readonly (EquipmentSlot Slot, string Label)[] SlotDisplay =
    {
        (EquipmentSlot.MainHand, "Main Hand"),
        (EquipmentSlot.OffHand,  "Off Hand"),
        (EquipmentSlot.Head,     "Head"),
        (EquipmentSlot.Body,     "Body"),
        (EquipmentSlot.Hands,    "Hands"),
        (EquipmentSlot.Legs,     "Legs"),
        (EquipmentSlot.Feet,     "Feet"),
        (EquipmentSlot.Ears,     "Ears"),
        (EquipmentSlot.Neck,     "Neck"),
        (EquipmentSlot.Wrists,   "Wrists"),
        (EquipmentSlot.RFinger,  "Right Finger"),
        (EquipmentSlot.LFinger,  "Left Finger"),
    };

    private static readonly (string SlotKey, string Label)[] BonusSlotDisplay =
    {
        ("Glasses", "Facewear Accessory"),
    };

    private void DrawEquipmentPanel(CachedOutfit details)
    {
        if (!DrawCollapsibleSubheader("Equipment", ref equipmentPanelOpen))
            return;

        ImGui.Indent();
        var slotMap = BuildSlotMap(details.Equipment);
        var bonusMap = BuildBonusMap(details.BonusItems);

        var slotLabelWidth = 0f;
        foreach (var (_, label) in SlotDisplay)
            slotLabelWidth = Math.Max(slotLabelWidth, ImGui.CalcTextSize(label).X);
        foreach (var (_, label) in BonusSlotDisplay)
            slotLabelWidth = Math.Max(slotLabelWidth, ImGui.CalcTextSize(label).X);
        slotLabelWidth += 16f * ImGuiHelpers.GlobalScale;

        foreach (var (slot, label) in SlotDisplay)
        {
            slotMap.TryGetValue(slot, out var entry);
            DrawEquipmentRow(label, slotLabelWidth, entry);
        }

        foreach (var (slotKey, label) in BonusSlotDisplay)
        {
            bonusMap.TryGetValue(slotKey, out var entry);
            DrawBonusRow(label, slotLabelWidth, entry);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawToggleRow(details);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private static bool DrawCollapsibleSubheader(string label, ref bool open)
    {
        // Custom header to recover CollapsingHeader's framed look while keeping the label near-aligned with the TextColored subheaders above it, otherwise the spacing looks off and it really annoys me
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();

        var avail = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var rectH = lineH + style.FramePadding.Y * 2f;

        var rectMin = ImGui.GetCursorScreenPos();
        var rectMax = new Vector2(rectMin.X + avail, rectMin.Y + rectH);

        if (ImGui.InvisibleButton($"##sub_{label}", new Vector2(avail, rectH)))
            open = !open;

        var bg = ImGui.IsItemActive() ? ImGuiCol.HeaderActive
               : ImGui.IsItemHovered() ? ImGuiCol.HeaderHovered
               : ImGuiCol.Header;
        draw.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(bg), style.FrameRounding);

        var textY = rectMin.Y + (rectH - lineH) * 0.5f;
        draw.AddText(new Vector2(rectMin.X + style.FramePadding.X, textY),
            ImGui.GetColorU32(SectionHeader), label);

        var chevron = open ? "▼" : "▶";
        var chevSize = ImGui.CalcTextSize(chevron);
        draw.AddText(new Vector2(rectMax.X - chevSize.X - style.FramePadding.X, textY),
            ImGui.GetColorU32(SectionHeader), chevron);

        return open;
    }

    private static Dictionary<EquipmentSlot, CachedEquipmentSlot> BuildSlotMap(List<CachedEquipmentSlot> equipment)
    {
        var map = new Dictionary<EquipmentSlot, CachedEquipmentSlot>(equipment.Count);
        foreach (var e in equipment)
            map[e.Slot] = e;
        return map;
    }

    private static Dictionary<string, CachedBonusItem> BuildBonusMap(List<CachedBonusItem> bonus)
    {
        var map = new Dictionary<string, CachedBonusItem>(bonus.Count, StringComparer.Ordinal);
        foreach (var b in bonus)
            map[b.Slot] = b;
        return map;
    }

    private void DrawEquipmentRow(string label, float labelWidth, CachedEquipmentSlot? entry)
    {
        var applied = entry?.Apply == true;
        var labelColor = applied ? AppliedTextColor : UnsetColor;

        var rowStartX = ImGui.GetCursorPosX();
        ImGui.TextColored(labelColor, label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(rowStartX + labelWidth);

        if (entry == null)
        {
            ImGui.TextColored(UnsetColor, "(not in design)");
            return;
        }

        var itemName = plugin.GameData.ResolveItemName(entry.ItemId);
        ImGui.TextColored(labelColor, itemName);

        DrawStainSwatch(entry.Stain, entry.ApplyStain && applied);
        DrawStainSwatch(entry.Stain2, entry.ApplyStain && applied);
    }

    private void DrawBonusRow(string label, float labelWidth, CachedBonusItem? entry)
    {
        var applied = entry?.Apply == true;
        var labelColor = applied ? AppliedTextColor : UnsetColor;

        var rowStartX = ImGui.GetCursorPosX();
        ImGui.TextColored(labelColor, label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(rowStartX + labelWidth);

        if (entry == null)
        {
            ImGui.TextColored(UnsetColor, "(not in design)");
            return;
        }

        var itemName = plugin.GameData.ResolveBonusItemName(entry.Slot, entry.ItemId);
        ImGui.TextColored(labelColor, itemName);
    }

    private void DrawStainSwatch(byte stainId, bool active)
    {
        if (stainId == 0)
            return;

        var (name, color) = plugin.GameData.ResolveStain(stainId);
        var v4 = StainColorToVec4(color, active ? 1.0f : 0.4f);

        ImGui.SameLine();
        var size = new Vector2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());
        ImGui.ColorButton($"##stain{stainId}", v4,
            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.NoInputs,
            size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(active ? name : $"{name} (not applied)");
    }

    private static Vector4 StainColorToVec4(uint color, float alpha)
    {
        // Stain.Color is packed as 0xRRGGBB.
        var r = ((color >> 16) & 0xFF) / 255f;
        var g = ((color >> 8) & 0xFF) / 255f;
        var b = (color & 0xFF) / 255f;
        return new Vector4(r, g, b, alpha);
    }

    private void DrawToggleRow(CachedOutfit details)
    {
        DrawToggle("Hat Visible", details.HatVisible);
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawToggle("Weapon", details.WeaponVisible);
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawToggle("Visor", details.VisorToggled);
    }

    private static void DrawToggle(string label, bool? state)
    {
        // Group the icon + label so IsItemHovered checks the union of both rects, not just
        // the most recent item.
        ImGui.BeginGroup();
        DrawTriStateIcon(state);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(state switch
            {
                true  => $"{label}: forced on",
                false => $"{label}: forced off",
                null  => $"{label}: not applied by this design",
            });
    }

    private static void DrawTriStateIcon(bool? state)
    {
        var (icon, color) = state switch
        {
            true  => (FontAwesomeIcon.Check, OnColor),
            false => (FontAwesomeIcon.Times, OffColor),
            null  => (FontAwesomeIcon.Circle, UnsetColor),
        };
        DrawFontAwesome(icon, color);
    }

    private static void DrawFontAwesome(FontAwesomeIcon icon, Vector4 color)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
    }

    private void DrawModsPanel(CachedOutfit details)
    {
        if (!DrawCollapsibleSubheader("Mod Associations", ref modsPanelOpen))
            return;

        ImGui.Indent();
        if (details.Mods.Count == 0)
        {
            ImGui.TextDisabled("No mods associated with this design");
            ImGui.Unindent();
            ImGui.Spacing();
            return;
        }

        foreach (var mod in details.Mods)
            DrawModRow(mod);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawModRow(CachedMod mod)
    {
        DrawModStateIcon(mod.State);
        ImGui.SameLine();

        var label = string.IsNullOrWhiteSpace(mod.Name) ? mod.Directory : mod.Name;
        if (string.IsNullOrWhiteSpace(label))
            label = "(unnamed mod)";

        ImGui.TextColored(ModLinkColor, label);

        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            DrawModTooltip(mod);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.Penumbra.OpenMod(mod.Directory, mod.Name);
    }

    private static void DrawModStateIcon(ModState state)
    {
        var (icon, color) = state switch
        {
            ModState.Enabled  => (FontAwesomeIcon.Check, OnColor),
            ModState.Disabled => (FontAwesomeIcon.Times, OffColor),
            _                 => (FontAwesomeIcon.Circle, UnsetColor),
        };
        DrawFontAwesome(icon, color);
    }

    private static void DrawModTooltip(CachedMod mod)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);

        if (!string.IsNullOrWhiteSpace(mod.Name))
            ImGui.TextUnformatted(mod.Name);
        if (!string.IsNullOrWhiteSpace(mod.Directory))
            ImGui.TextDisabled(mod.Directory);

        ImGui.Spacing();
        ImGui.TextColored(SectionHeader, "State:");
        ImGui.SameLine();
        ImGui.TextUnformatted(mod.State.ToString());

        if (mod.State == ModState.Enabled)
        {
            ImGui.TextColored(SectionHeader, "Priority:");
            ImGui.SameLine();
            ImGui.TextUnformatted(mod.Priority.ToString());
        }

        if (mod.Settings.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(SectionHeader, "Settings");
            foreach (var (group, value) in mod.Settings)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(string.IsNullOrEmpty(value) ? group : $"{group}: {value}");
            }
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
