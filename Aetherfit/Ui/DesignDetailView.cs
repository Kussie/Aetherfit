using System.Collections.Generic;
using System.Numerics;
using Aetherfit.Services.Game;
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
    // wearableByCurrentCharacter is null when not checked (or unknown); false draws a warning icon after the
    // item name. Returns true while the affected-by mod name is hovered.
    public static bool DrawSlotRow(GameDataService gameData, string label, float labelWidth, string? itemName,
        byte stain, byte stain2, bool applyStain, bool applied, IReadOnlyDictionary<string, string> affected,
        bool? wearableByCurrentCharacter = null, string? wearableRacesText = null)
    {
        var labelColor = applied ? UiTheme.AppliedText : UiTheme.StateUnset;

        var rowStartX = ImGui.GetCursorPosX();
        ImGui.TextColored(labelColor, label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(rowStartX + labelWidth);

        if (itemName == null)
        {
            ImGui.TextColored(UiTheme.StateUnset, "(not in design)");
            return false;
        }

        ImGui.TextColored(labelColor, itemName);
        if (wearableByCurrentCharacter == false)
        {
            ImGui.SameLine();
            DrawFontAwesome(FontAwesomeIcon.ExclamationTriangle, UiTheme.ErrorText);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Not wearable by your current race/gender - only {wearableRacesText ?? "some races"}.");
        }
        DrawStainSwatch(gameData, stain, applyStain && applied);
        DrawStainSwatch(gameData, stain2, applyStain && applied);
        return DrawAffectedSuffix(affected, applied, itemName);
    }

    // "(Appearance affected by {mod})" with the mod name tinted so it stands out. The mod name is rendered
    // format-safe (TextUnformatted) since it can originate from an imported design/bundle.
    public static bool DrawAffectedSuffix(IReadOnlyDictionary<string, string> affected, bool applied, string itemName)
    {
        if (!applied || itemName == GameDataService.NothingItemName)
            return false;
        if (!affected.TryGetValue(itemName, out var modName))
            return false;

        return DrawAffectedByText(modName);
    }

    // "(Appearance affected by {modName})" with the mod name tinted; true while the mod name is hovered.
    public static bool DrawAffectedByText(string modName)
    {
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.StateUnset, "(Appearance affected by ");
        ImGui.SameLine(0, 0);
        TextColoredUnformatted(UiTheme.ModLink, modName);
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, 0);
        ImGui.TextColored(UiTheme.StateUnset, ")");
        return hovered;
    }

    // "(inherited from Layer: {name})" with the name tinted as a clickable cross-reference; true while
    // the name is hovered, so callers can drive the same Shift+click-to-open behavior as other design
    // links (e.g. DrawDesignLinkRow). tag distinguishes item vs stain when a row's two are inherited
    // from different layers.
    public static bool DrawInheritedFromLayerText(string designName, string? tag = null)
    {
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.StateUnset, "(inherited from Layer: ");
        ImGui.SameLine(0, 0);
        TextColoredUnformatted(UiTheme.ModLink, designName);
        var hovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, 0);
        ImGui.TextColored(UiTheme.StateUnset, tag == null ? ")" : $" ({tag}))");
        return hovered;
    }

    // "(also contested by a random layer pick among N designs)" - never claims a specific inherited
    // value, just flags that one might still apply. A distinct caution tint so it doesn't read as
    // clickable and doesn't blend into "(not in design)" grey text.
    public static void DrawContestedCaveat(int candidateCount)
    {
        ImGui.SameLine();
        ImGui.TextColored(UiTheme.CautionText,
            $"(also contested by a random layer pick among {candidateCount} designs)");
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
