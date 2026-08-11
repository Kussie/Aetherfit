using System;
using System.Collections.Generic;
using System.Numerics;
using Aetherfit.Services.Designs;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private static readonly Vector4 OnColor = UiTheme.StateOn;
    private static readonly Vector4 OffColor = UiTheme.StateOff;
    private static readonly Vector4 UnsetColor = UiTheme.StateUnset;
    private static readonly Vector4 AppliedTextColor = UiTheme.AppliedText;
    private static readonly Vector4 SectionHeader = UiTheme.SectionHeader;
    private static readonly Vector4 ModLinkColor = UiTheme.ModLink;

    // Per-design mod attributions, built the first time we draw a design and cleared on refresh.
    private readonly Dictionary<Guid, AffectedBy> affectedByCache = new();

    // Which mod (if any) is responsible for parts of a design's look: item name -> mod for equipment,
    // plus the mod changing the applied hairstyle. Items is the same map flattened to display names.
    private sealed record AffectedBy(IReadOnlyDictionary<string, CachedMod> Mods,
        IReadOnlyDictionary<string, string> Items, CachedMod? Hairstyle);

    private bool equipmentPanelOpen = true;
    private bool customizationsPanelOpen = true;
    private bool modsPanelOpen = true;
    private bool designLinksPanelOpen = true;
    private bool imagesPanelOpen = true;
    private bool jobAssociationsPanelOpen = true;
    private bool tagsPanelOpen = true;
    private bool descriptionPanelOpen = true;
    private bool variantPanelOpen = true;

    // The application aspects a design link can toggle, in Glamourer's flag order. Mirrors ApplicationType.
    private static readonly (DesignLinkApplication Flag, string Label)[] LinkApplicationFlags =
    {
        (DesignLinkApplication.Armor, "Armor"),
        (DesignLinkApplication.Customizations, "Customizations"),
        (DesignLinkApplication.Weapons, "Weapons"),
        (DesignLinkApplication.GearCustomization, "Dyes/Crests"),
        (DesignLinkApplication.Accessories, "Accessories"),
    };

    private void DrawEquipmentPanel(Guid id, CachedOutfit details)
    {
        if (!Pills.DrawCollapsibleSubheader("Equipment", ref equipmentPanelOpen))
            return;

        ImGui.Indent();
        var slotMap = BuildSlotMap(details.Equipment);
        var bonusMap = BuildBonusMap(details.BonusItems);
        var affectedBy = GetAffectedBy(id, details);
        var raceGender = plugin.GameData.ResolveEffectiveRaceGender(details);

        var slotLabelWidth = LabelColumnWidth(details);

        foreach (var (slot, label) in DesignDetailView.SlotDisplay)
        {
            slotMap.TryGetValue(slot, out var entry);
            DrawEquipmentRow(label, slotLabelWidth, entry, affectedBy, raceGender);
        }

        foreach (var (slotKey, label) in DesignDetailView.BonusSlotDisplay)
        {
            bonusMap.TryGetValue(slotKey, out var entry);
            DrawBonusRow(label, slotLabelWidth, entry, affectedBy);
        }

        // These flags are Glamourer design metadata - Glamaholic/Glamour Plate designs are relayed
        // through Glamourer's equipment-only apply, so they never carry them. Simple Glamour Switcher
        // does carry Hat/Weapon/Visor toggles of its own (see SimpleGlamourSwitcherService), so it's
        // included here too.
        if (details.Source is DesignSource.Glamourer or DesignSource.SimpleGlamourSwitcher)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawToggleRow(details);
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawCustomizationsPanel(Guid id, CachedOutfit details)
    {
        // We only cache the customizations a design actually applies, so if there are none there's nothing to show.
        if (details.Customizations.Count == 0)
            return;

        if (!Pills.DrawCollapsibleSubheader("Customizations", ref customizationsPanelOpen))
            return;

        ImGui.Indent();

        // Reuse the equipment panel's column width so the values line up across both sections.
        var labelWidth = LabelColumnWidth(details);
        var hairstyleMod = GetAffectedBy(id, details).Hairstyle;

        foreach (var c in details.Customizations)
        {
            var rowStartX = ImGui.GetCursorPosX();
            ImGui.TextColored(AppliedTextColor, c.Label);
            ImGui.SameLine();
            ImGui.SetCursorPosX(rowStartX + labelWidth);

            if (c.IsToggle)
            {
                ImGui.TextColored(c.Value == "On" ? OnColor : OffColor, c.Value);
            }
            else
            {
                ImGui.TextColored(AppliedTextColor, c.Value);
                DrawCustomizationColorSwatch(c, details);
            }

            if (c.Key == "Hairstyle" && hairstyleMod != null
                && DesignDetailView.DrawAffectedByText(DesignAttributionService.ModDisplayName(hairstyleMod)))
                HandleAffectedModHover(hairstyleMod);
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawCustomizationColorSwatch(CachedCustomization c, CachedOutfit details)
    {
        if (!plugin.GameData.TryResolveCustomizeColor(
                c.Key, c.RawValue, details.CustomizeClan, details.CustomizeGender, out var rgb))
            return;

        ImGui.SameLine();
        var size = new Vector2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());
        ImGui.ColorButton($"##cust_{c.Key}", DesignDetailView.StainColorToVec4(rgb, 1.0f),
            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.NoInputs,
            size);
    }

    // The label column width, shared by the Equipment and Customizations panels so their values line up.
    // Measures every equipment/bonus slot label plus whatever customizations this design has.
    private static float LabelColumnWidth(CachedOutfit details)
    {
        var width = 0f;
        foreach (var (_, label) in DesignDetailView.SlotDisplay)
            width = Math.Max(width, ImGui.CalcTextSize(label).X);
        foreach (var (_, label) in DesignDetailView.BonusSlotDisplay)
            width = Math.Max(width, ImGui.CalcTextSize(label).X);
        foreach (var c in details.Customizations)
            width = Math.Max(width, ImGui.CalcTextSize(c.Label).X);
        return width + 16f * ImGuiHelpers.GlobalScale;
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

    // Mod attribution depends on the current character's race/gender (for hairstyles a design doesn't
    // pin), so the cached results go stale when the player changes - clear them on login.
    public void InvalidateAttributionCache() => affectedByCache.Clear();

    private AffectedBy GetAffectedBy(Guid id, CachedOutfit details)
    {
        if (affectedByCache.TryGetValue(id, out var cached))
            return cached;

        var result = plugin.Attribution.Build(details);
        var names = new Dictionary<string, string>(result.Items.Count, StringComparer.Ordinal);
        foreach (var (itemName, mod) in result.Items)
            names[itemName] = DesignAttributionService.ModDisplayName(mod);

        var affected = new AffectedBy(result.Items, names, result.Hairstyle);
        affectedByCache[id] = affected;
        return affected;
    }

    private void DrawEquipmentRow(string label, float labelWidth, CachedEquipmentSlot? entry, AffectedBy affectedBy,
        (uint RaceRowId, bool IsFemale)? raceGender)
    {
        var applied = entry?.Apply == true;
        var itemName = entry == null ? null : plugin.GameData.ResolveItemName(entry.ItemId);
        bool? wearable = entry != null && raceGender is { } rg
            ? plugin.GameData.IsItemWearableBy(entry.ItemId, rg.RaceRowId, rg.IsFemale)
            : null;
        var wearableRacesText = wearable == false ? plugin.GameData.DescribeWearableRaces(entry!.ItemId) : null;
        var modHovered = DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
            entry?.Stain ?? 0, entry?.Stain2 ?? 0, entry?.ApplyStain ?? false, applied, affectedBy.Items,
            wearable, wearableRacesText);
        if (modHovered && itemName != null && affectedBy.Mods.TryGetValue(itemName, out var mod))
            HandleAffectedModHover(mod);
    }

    private void DrawBonusRow(string label, float labelWidth, CachedBonusItem? entry, AffectedBy affectedBy)
    {
        var applied = entry?.Apply == true;
        var itemName = entry == null ? null : plugin.GameData.ResolveBonusItemName(entry.Slot, entry.ItemId);
        var modHovered = DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
            stain: 0, stain2: 0, applyStain: false, applied, affectedBy.Items);
        if (modHovered && itemName != null && affectedBy.Mods.TryGetValue(itemName, out var mod))
            HandleAffectedModHover(mod);
    }

    private void HandleAffectedModHover(CachedMod mod)
    {
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        DrawModTooltip(mod);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.Penumbra.OpenMod(mod.Directory, mod.Name);
    }

    private void DrawToggleRow(CachedOutfit details)
    {
        DrawToggle("Hat Visible", details.HatVisible);
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawToggle("Weapon", details.WeaponVisible);
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawToggle("Visor", details.VisorToggled);

        DrawBoolToggle("Force Redraw", details.ForcedRedraw,
            "Force Redraw: enabled — the design redraws the character on apply",
            "Force Redraw: disabled");
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawBoolToggle("Reset Temporary Settings", details.ResetTemporarySettings,
            "Reset Temporary Settings: enabled — the design resets temporary settings on apply",
            "Reset Temporary Settings: disabled");
    }

    // A plain on/off toggle (always set, never tri-state) for design-level application flags.
    private static void DrawBoolToggle(string label, bool state, string onTooltip, string offTooltip)
    {
        ImGui.BeginGroup();
        DesignDetailView.DrawFontAwesome(state ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, state ? OnColor : OffColor);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(state ? onTooltip : offTooltip);
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
        DesignDetailView.DrawFontAwesome(icon, color);
    }

    private void DrawDesignLinksPanel(CachedOutfit details)
    {
        // Only Glamourer designs with links have anything to show, so skip the section entirely otherwise.
        if (details.Links.Count == 0)
            return;

        if (!Pills.DrawCollapsibleSubheader("Design Links", ref designLinksPanelOpen))
            return;

        ImGui.Indent();
        foreach (var link in details.Links)
            DrawDesignLinkRow(link);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawDesignLinkRow(CachedDesignLink link)
    {
        var name = ResolveLinkedDesignName(link.DesignId);

        DesignDetailView.TextColoredUnformatted(ModLinkColor, name);
        var hovered = ImGui.IsItemHovered();

        ImGui.SameLine();
        ImGui.TextDisabled(link.IsBefore ? "(before)" : "(after)");

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Shift + left-click to open in Aetherfit\nShift + right-click to open in Glamourer");

            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                selectedDesign = link.DesignId;
                coverMode = false;
            }
            // Links are a native Glamourer concept (only ever populated from a Glamourer design's own
            // JSON), so link.DesignId is always Glamourer-sourced - guarded anyway for consistency.
            if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Right)
                && plugin.Configuration.CachedOutfits.TryGetValue(link.DesignId, out var quickOpenOutfit)
                && quickOpenOutfit.Source == DesignSource.Glamourer)
                plugin.Glamourer.OpenInGlamourer(link.DesignId, name);
        }

        ImGui.Indent();
        DrawLinkApplicationToggles(link.LinkType);

        var condition = DescribeLinkCondition(link);
        if (condition != null)
            ImGui.TextDisabled(condition);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private static void DrawLinkApplicationToggles(int linkType)
    {
        var type = (DesignLinkApplication)linkType;
        for (var i = 0; i < LinkApplicationFlags.Length; i++)
        {
            var (flag, label) = LinkApplicationFlags[i];
            // Three per line, matching the equipment metadata toggle row so it doesn't run off a narrow pane.
            if (i % 3 != 0)
                ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);

            var on = (type & flag) != 0;
            DrawBoolToggle(label, on,
                $"{label}: applied by this link",
                $"{label}: not applied by this link");
        }
    }

    private string ResolveLinkedDesignName(Guid id) => plugin.Configuration.ResolveDesignName(id);

    // A link's job/gearset gate, mirroring Glamourer's precedence: a set gearset wins, else a job category,
    // else no restriction (applies to everything, so we show nothing).
    private string? DescribeLinkCondition(CachedDesignLink link)
    {
        if (link.Gearset >= 0)
            return $"Restricted to gearset {link.Gearset}";

        var jobs = plugin.GameData.ResolveJobGroupName(link.JobGroup);
        return jobs == null ? null : $"Restricted to: {jobs}";
    }

    private void DrawModsPanel(CachedOutfit details)
    {
        if (!Pills.DrawCollapsibleSubheader("Mod Associations", ref modsPanelOpen))
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
        DesignDetailView.DrawModStateIcon(mod.State);
        ImGui.SameLine();

        var label = DesignAttributionService.ModDisplayName(mod);
        if (string.IsNullOrWhiteSpace(label))
            label = "(unnamed mod)";

        DesignDetailView.TextColoredUnformatted(ModLinkColor, label);

        // An association forces its own enabled state at apply time, so the only real failure mode
        // is the mod no longer being installed - see DesignAttributionService.HasMissingModAssociation.
        var missing = mod.State == ModState.Enabled
            && !plugin.Penumbra.GetInstalledModDirectories().Contains(mod.Directory);
        if (missing)
        {
            ImGui.SameLine();
            DesignDetailView.DrawFontAwesome(FontAwesomeIcon.ExclamationTriangle, UiTheme.ErrorText);
        }

        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            DrawModTooltip(mod, missing);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.Penumbra.OpenMod(mod.Directory, mod.Name);
    }

    private static void DrawModTooltip(CachedMod mod, bool missing = false)
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

        if (missing)
        {
            ImGui.Spacing();
            ImGui.TextColored(UiTheme.ErrorText, "Not installed — this association will fail to apply.");
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
