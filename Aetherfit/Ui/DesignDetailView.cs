using System.Collections.Generic;
using System.Numerics;
using Aetherfit.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Ui;

// Rendering shared by the local design-detail panel (MainWindow) and the read-only shared-gallery popup
// (ForeignGalleryWindow). The two used to carry near-identical copies of these helpers; the only real
// difference is the source data type, so callers resolve names/values and hand primitives to these methods.
internal static class DesignDetailView
{
    public static readonly (EquipmentSlot Slot, string Label)[] SlotDisplay =
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

    public static readonly (string SlotKey, string Label)[] BonusSlotDisplay =
    {
        ("Glasses", "Facewear Accessory"),
    };

    // One equipment/bonus row: a label column then the item name, dye swatches and "(affected by mod)" suffix.
    // itemName == null means the slot isn't in the design (greyed "(not in design)"). Bonus rows pass stain 0.
    public static void DrawSlotRow(GameDataService gameData, string label, float labelWidth, string? itemName,
        byte stain, byte stain2, bool applyStain, bool applied, IReadOnlyDictionary<string, string> affected)
    {
        var labelColor = applied ? UiTheme.AppliedText : UiTheme.StateUnset;

        var rowStartX = ImGui.GetCursorPosX();
        ImGui.TextColored(labelColor, label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(rowStartX + labelWidth);

        if (itemName == null)
        {
            ImGui.TextColored(UiTheme.StateUnset, "(not in design)");
            return;
        }

        ImGui.TextColored(labelColor, itemName);
        DrawStainSwatch(gameData, stain, applyStain && applied);
        DrawStainSwatch(gameData, stain2, applyStain && applied);
        DrawAffectedSuffix(affected, applied, itemName);
    }

    // "(Appearance affected by {mod})" with the mod name tinted so it stands out. The mod name is rendered
    // format-safe (TextUnformatted) since it can originate from an imported design/bundle.
    public static void DrawAffectedSuffix(IReadOnlyDictionary<string, string> affected, bool applied, string itemName)
    {
        if (!applied || itemName == GameDataService.NothingItemName)
            return;
        if (!affected.TryGetValue(itemName, out var modName))
            return;

        DrawAffectedByText(modName);
    }

    // "(Appearance affected by {modName})" with the mod name tinted; rendered format-safe.
    public static void DrawAffectedByText(string modName)
    {
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.StateUnset, "(Appearance affected by ");
        ImGui.SameLine(0, 0);
        TextColoredUnformatted(UiTheme.ModLink, modName);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(UiTheme.StateUnset, ")");
    }

    public static void DrawStainSwatch(GameDataService gameData, byte stainId, bool active)
    {
        if (stainId == 0)
            return;

        var (name, color) = gameData.ResolveStain(stainId);
        var v4 = StainColorToVec4(color, active ? 1.0f : 0.4f);

        ImGui.SameLine();
        var size = new Vector2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());
        ImGui.ColorButton($"##stain{stainId}", v4,
            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.NoInputs,
            size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(active ? name : $"{name} (not applied)");
    }

    public static void DrawModStateIcon(ModState state)
    {
        var (icon, color) = state switch
        {
            ModState.Enabled  => (FontAwesomeIcon.Check, UiTheme.StateOn),
            ModState.Disabled => (FontAwesomeIcon.Times, UiTheme.StateOff),
            _                 => (FontAwesomeIcon.Circle, UiTheme.StateUnset),
        };
        DrawFontAwesome(icon, color);
    }

    public static void DrawFontAwesome(FontAwesomeIcon icon, Vector4 color)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
    }

    // Coloured text that is never treated as a printf-style format string - for untrusted free text.
    public static void TextColoredUnformatted(Vector4 color, string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
    }

    public static Vector4 StainColorToVec4(uint color, float alpha)
    {
        // Stain.Color is packed as 0xRRGGBB.
        var r = ((color >> 16) & 0xFF) / 255f;
        var g = ((color >> 8) & 0xFF) / 255f;
        var b = (color & 0xFF) / 255f;
        return new Vector4(r, g, b, alpha);
    }
}
