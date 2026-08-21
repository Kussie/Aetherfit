using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Designs;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

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
        var layers = ResolveLayersForDisplay(id);

        if (layers != null)
        {
            ImGui.TextDisabled("Some values below may be inherited from this design's layers.");
            ImGui.Spacing();
        }

        var slotLabelWidth = LabelColumnWidth(details, layers);

        foreach (var (slot, label) in DesignDetailView.SlotDisplay)
        {
            slotMap.TryGetValue(slot, out var entry);
            DrawEquipmentRow(id, slot, label, slotLabelWidth, entry, affectedBy, raceGender, layers);
        }

        foreach (var (slotKey, label) in DesignDetailView.BonusSlotDisplay)
        {
            bonusMap.TryGetValue(slotKey, out var entry);
            DrawBonusRow(id, slotKey, label, slotLabelWidth, entry, affectedBy, layers);
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

            DrawToggleRow(details, layers);
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Only meaningful when the design actually has a resolvable base/before/after layer - null lets
    // every row-drawing helper below fall back to today's exact behavior with a single null check.
    private DesignLayerResolutionService.Result? ResolveLayersForDisplay(Guid id)
    {
        if (!plugin.Configuration.EnableRandomLayers)
            return null;
        var resolved = plugin.LayerResolution.Resolve(id);
        return resolved.HasAnyLayers ? resolved : null;
    }

    private void DrawCustomizationsPanel(Guid id, CachedOutfit details)
    {
        var layers = ResolveLayersForDisplay(id);

        // We only cache the customizations a design actually applies, so if there are none of its own
        // and no layer contributes any either, there's nothing to show.
        if (details.Customizations.Count == 0 && (layers == null || layers.CustomizationSource.Count == 0))
            return;

        if (!Pills.DrawCollapsibleSubheader("Customizations", ref customizationsPanelOpen))
            return;

        ImGui.Indent();

        // Reuse the equipment panel's column width so the values line up across both sections.
        var labelWidth = LabelColumnWidth(details, layers);

        // The design's own order first (unchanged from before layers existed), then any keys only a
        // layer contributes, appended after.
        var keys = new List<string>(details.Customizations.Select(c => c.Key));
        if (layers != null)
            foreach (var key in layers.CustomizationSource.Keys)
                if (!keys.Contains(key))
                    keys.Add(key);

        foreach (var key in keys)
        {
            var res = layers != null && layers.CustomizationSource.TryGetValue(key, out var r) ? r : default;
            var (c, sourceOutfit) = ResolveLayerCustomizationEntry(res.Winner, key, details);
            if (c == null)
                continue;

            var rowStart = ImGui.GetCursorScreenPos();
            var rowWidth = ImGui.GetContentRegionAvail().X;

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
                DrawCustomizationColorSwatch(c, sourceOutfit);
            }

            var winnerSource = res.Winner is { IsBaseDesign: false } w ? w : (DesignLayerResolutionService.FieldSource?)null;
            var hairstyleMod = key == "Hairstyle" ? GetAffectedBy(winnerSource?.SourceDesignId ?? id, sourceOutfit).Hairstyle : null;

            var modHovered = key == "Hairstyle" && hairstyleMod != null
                && DesignDetailView.DrawAffectedByText(DesignAttributionService.ModDisplayName(hairstyleMod));
            if (modHovered)
                HandleAffectedModHover(hairstyleMod!);

            if (winnerSource != null)
                DrawInheritedLink(winnerSource.Value.SourceDesignId);
            if (res.Contested)
                DesignDetailView.DrawContestedCaveat(res.ContestedCandidateCount);

            DrawCustomizationContextMenu(winnerSource?.SourceDesignId ?? id, c, rowStart, rowWidth, modHovered);
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Resolves which CachedCustomization entry (and its owning outfit, for color-swatch clan/gender)
    // actually wins for `key` per the layer resolution - falls back to the base design's own entry
    // (possibly null) when there's no layer winner or its source can't be found (e.g. deleted mid-session).
    private (CachedCustomization? Entry, CachedOutfit SourceOutfit) ResolveLayerCustomizationEntry(
        DesignLayerResolutionService.FieldSource? winner, string key, CachedOutfit details)
    {
        if (winner is { IsBaseDesign: false } w && plugin.Configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var outfit))
        {
            var sourceEntry = outfit.Customizations.FirstOrDefault(c => c.Key == key);
            if (sourceEntry != null)
                return (sourceEntry, outfit);
        }

        return (details.Customizations.FirstOrDefault(c => c.Key == key), details);
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

    // Right-click anywhere on a customization row to apply just that value - same shape as
    // DrawSlotContextMenu, just adapted since customization rows don't go through DrawSlotRow.
    private void DrawCustomizationContextMenu(Guid designId, CachedCustomization customization,
        Vector2 rowStart, float rowWidth, bool modHovered)
    {
        var popupId = $"##cust_{customization.Key}Menu";
        if (!modHovered && !ImGui.IsPopupOpen(popupId))
        {
            var rowEnd = new Vector2(rowStart.X + rowWidth, ImGui.GetCursorScreenPos().Y);
            if (ImGui.IsMouseHoveringRect(rowStart, rowEnd))
            {
                ImGui.SetTooltip("Right-click to apply just this to your current outfit.");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup(popupId);
            }
        }

        using var popup = ImRaii.Popup(popupId);
        if (popup.Success && ImGui.MenuItem("Apply Just This"))
            plugin.DesignApply.ApplySingleCustomization(designId, customization.Key, customization.Label);
    }

    // The label column width, shared by the Equipment and Customizations panels so their values line up.
    // Measures every equipment/bonus slot label plus whatever customizations this design (and any
    // layer-only customizations it inherits) has.
    private float LabelColumnWidth(CachedOutfit details, DesignLayerResolutionService.Result? layers)
    {
        var width = 0f;
        foreach (var (_, label) in DesignDetailView.SlotDisplay)
            width = Math.Max(width, ImGui.CalcTextSize(label).X);
        foreach (var (_, label) in DesignDetailView.BonusSlotDisplay)
            width = Math.Max(width, ImGui.CalcTextSize(label).X);
        foreach (var c in details.Customizations)
            width = Math.Max(width, ImGui.CalcTextSize(c.Label).X);

        if (layers != null)
            foreach (var (key, res) in layers.CustomizationSource)
            {
                if (details.Customizations.Any(c => c.Key == key))
                    continue;
                var (entry, _) = ResolveLayerCustomizationEntry(res.Winner, key, details);
                if (entry != null)
                    width = Math.Max(width, ImGui.CalcTextSize(entry.Label).X);
            }

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

    private void DrawEquipmentRow(Guid designId, EquipmentSlot slot, string label, float labelWidth,
        CachedEquipmentSlot? entry, AffectedBy affectedBy, (uint RaceRowId, bool IsFemale)? raceGender,
        DesignLayerResolutionService.Result? layers)
    {
        var itemRes = layers != null && layers.ItemSource.TryGetValue(slot, out var ir) ? ir : default;
        var stainRes = layers != null && layers.StainSource.TryGetValue(slot, out var sr) ? sr : default;

        var itemSource = ResolveLayerEquipmentEntry(itemRes.Winner, slot) ?? entry;
        var stainSource = ResolveLayerEquipmentEntry(stainRes.Winner, slot) ?? entry;

        var applied = layers != null ? itemRes.Winner != null : entry?.Apply == true;
        var itemName = itemSource == null ? null : plugin.GameData.ResolveItemName(itemSource.ItemId);
        bool? wearable = itemSource != null && raceGender is { } rg
            ? plugin.GameData.IsItemWearableBy(itemSource.ItemId, rg.RaceRowId, rg.IsFemale)
            : null;
        var wearableRacesText = wearable == false ? plugin.GameData.DescribeWearableRaces(itemSource!.ItemId) : null;
        var applyStain = layers != null ? stainRes.Winner != null : entry?.ApplyStain ?? false;

        // An item inherited from a layer shows that layer's own mod attribution, not the base design's.
        var itemWinner = itemRes.Winner is { IsBaseDesign: false } iw ? iw : (DesignLayerResolutionService.FieldSource?)null;
        var rowAffectedBy = itemWinner != null && plugin.Configuration.CachedOutfits.TryGetValue(itemWinner.Value.SourceDesignId, out var itemSourceOutfit)
            ? GetAffectedBy(itemWinner.Value.SourceDesignId, itemSourceOutfit)
            : affectedBy;

        var rowStart = ImGui.GetCursorScreenPos();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var modHovered = DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
            stainSource?.Stain ?? 0, stainSource?.Stain2 ?? 0, applyStain, applied, rowAffectedBy.Items,
            wearable, wearableRacesText);

        DrawEquipmentInheritanceAnnotations(itemRes, stainRes);

        // Nothing to single-apply if the slot isn't equipped, or the design leaves it disabled.
        var canApply = applied && itemName != GameDataService.NothingItemName;
        var applyDesignId = itemWinner?.SourceDesignId ?? designId;
        DrawSlotContextMenu($"slot_{slot}", rowStart, rowWidth, canApply, modHovered,
            itemName, rowAffectedBy, () => plugin.DesignApply.ApplySingleEquipmentSlot(applyDesignId, slot, label));
    }

    private CachedEquipmentSlot? ResolveLayerEquipmentEntry(DesignLayerResolutionService.FieldSource? winner, EquipmentSlot slot)
    {
        if (winner is not { IsBaseDesign: false } w) return null;
        if (!plugin.Configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var outfit)) return null;
        return outfit.Equipment.FirstOrDefault(e => e.Slot == slot);
    }

    // Item and stain can be inherited from two different layers (Apply/ApplyStain are independent) -
    // show one untagged link when they agree, or two tagged links when they don't. Contested caveats
    // are deduped by candidate count, since a field can only be contested by one slot at a time.
    private void DrawEquipmentInheritanceAnnotations(DesignLayerResolutionService.FieldResolution itemRes,
        DesignLayerResolutionService.FieldResolution stainRes)
    {
        var itemWinner = itemRes.Winner is { IsBaseDesign: false } iw ? iw.SourceDesignId : (Guid?)null;
        var stainWinner = stainRes.Winner is { IsBaseDesign: false } sw ? sw.SourceDesignId : (Guid?)null;

        if (itemWinner != null && itemWinner == stainWinner)
        {
            DrawInheritedLink(itemWinner.Value);
        }
        else
        {
            if (itemWinner != null) DrawInheritedLink(itemWinner.Value, stainWinner != null ? "item" : null);
            if (stainWinner != null) DrawInheritedLink(stainWinner.Value, itemWinner != null ? "stain" : null);
        }

        if (itemRes.Contested)
            DesignDetailView.DrawContestedCaveat(itemRes.ContestedCandidateCount);
        if (stainRes.Contested && !(itemRes.Contested && stainRes.ContestedCandidateCount == itemRes.ContestedCandidateCount))
            DesignDetailView.DrawContestedCaveat(stainRes.ContestedCandidateCount);
    }

    // "(inherited from Layer: {name})" with the same Shift+click-to-open behavior as DrawDesignLinkRow.
    private void DrawInheritedLink(Guid sourceDesignId, string? tag = null)
    {
        var name = ResolveLinkedDesignName(sourceDesignId);
        var hovered = DesignDetailView.DrawInheritedFromLayerText(name, tag);
        if (!hovered)
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.SetTooltip("Shift + left-click to open in Aetherfit\nShift + right-click to open in Glamourer");

        if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            selectedDesign = sourceDesignId;
            coverMode = false;
        }
        if (ImGui.GetIO().KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && plugin.Configuration.CachedOutfits.TryGetValue(sourceDesignId, out var sourceOutfit)
            && sourceOutfit.Source == DesignSource.Glamourer)
            plugin.Glamourer.OpenInGlamourer(sourceDesignId, name);
    }

    private void DrawBonusRow(Guid designId, string slotKey, string label, float labelWidth,
        CachedBonusItem? entry, AffectedBy affectedBy, DesignLayerResolutionService.Result? layers)
    {
        var res = layers != null && layers.BonusItemSource.TryGetValue(slotKey, out var r) ? r : default;
        var source = ResolveLayerBonusEntry(res.Winner, slotKey) ?? entry;

        var applied = layers != null ? res.Winner != null : entry?.Apply == true;
        var itemName = source == null ? null : plugin.GameData.ResolveBonusItemName(source.Slot, source.ItemId);

        var winner = res.Winner is { IsBaseDesign: false } w ? w : (DesignLayerResolutionService.FieldSource?)null;
        var rowAffectedBy = winner != null && plugin.Configuration.CachedOutfits.TryGetValue(winner.Value.SourceDesignId, out var sourceOutfit)
            ? GetAffectedBy(winner.Value.SourceDesignId, sourceOutfit)
            : affectedBy;

        var rowStart = ImGui.GetCursorScreenPos();
        var rowWidth = ImGui.GetContentRegionAvail().X;
        var modHovered = DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
            stain: 0, stain2: 0, applyStain: false, applied, rowAffectedBy.Items);

        if (winner != null)
            DrawInheritedLink(winner.Value.SourceDesignId);
        if (res.Contested)
            DesignDetailView.DrawContestedCaveat(res.ContestedCandidateCount);

        var canApply = applied && itemName != GameDataService.NothingItemName;
        var applyDesignId = winner?.SourceDesignId ?? designId;
        DrawSlotContextMenu($"bonus_{slotKey}", rowStart, rowWidth, canApply, modHovered,
            itemName, rowAffectedBy, () => plugin.DesignApply.ApplySingleBonusItem(applyDesignId, slotKey, label));
    }

    private CachedBonusItem? ResolveLayerBonusEntry(DesignLayerResolutionService.FieldSource? winner, string slotKey)
    {
        if (winner is not { IsBaseDesign: false } w) return null;
        if (!plugin.Configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var outfit)) return null;
        return outfit.BonusItems.FirstOrDefault(b => b.Slot == slotKey);
    }

    // Right-click anywhere on the row (when it's actually equipped) to apply just that item - the
    // existing "affected by mod" hover/click behavior still wins when that specific text is hovered.
    private void DrawSlotContextMenu(string idSuffix, Vector2 rowStart, float rowWidth, bool canApply,
        bool modHovered, string? itemName, AffectedBy affectedBy, Action onApply)
    {
        var popupId = $"##{idSuffix}Menu";
        if (modHovered && itemName != null && affectedBy.Mods.TryGetValue(itemName, out var mod))
        {
            HandleAffectedModHover(mod);
        }
        else if (canApply && !ImGui.IsPopupOpen(popupId))
        {
            var rowEnd = new Vector2(rowStart.X + rowWidth, ImGui.GetCursorScreenPos().Y);
            if (ImGui.IsMouseHoveringRect(rowStart, rowEnd))
            {
                ImGui.SetTooltip("Right-click to apply just this item to your current outfit.");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup(popupId);
            }
        }

        using var popup = ImRaii.Popup(popupId);
        if (popup.Success && ImGui.MenuItem("Apply Just This"))
            onApply();
    }

    private void HandleAffectedModHover(CachedMod mod)
    {
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        DrawModTooltip(mod);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            plugin.Penumbra.OpenMod(mod.Directory, mod.Name);
    }

    private void DrawToggleRow(CachedOutfit details, DesignLayerResolutionService.Result? layers)
    {
        DrawToggle("Hat Visible", details.HatVisible, layers?.HatVisibleSource ?? default, o => o.HatVisible);
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawToggle("Weapon", details.WeaponVisible, layers?.WeaponVisibleSource ?? default, o => o.WeaponVisible);
        ImGui.SameLine(0, 24f * ImGuiHelpers.GlobalScale);
        DrawToggle("Visor", details.VisorToggled, layers?.VisorToggledSource ?? default, o => o.VisorToggled);

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

    private void DrawToggle(string label, bool? baseState, DesignLayerResolutionService.FieldResolution res,
        Func<CachedOutfit, bool?> selector)
    {
        var winner = res.Winner is { IsBaseDesign: false } w ? w : (DesignLayerResolutionService.FieldSource?)null;
        var state = baseState;
        if (winner != null && plugin.Configuration.CachedOutfits.TryGetValue(winner.Value.SourceDesignId, out var sourceOutfit))
            state = selector(sourceOutfit);

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

        if (winner != null)
            DrawInheritedLink(winner.Value.SourceDesignId);
        if (res.Contested)
            DesignDetailView.DrawContestedCaveat(res.ContestedCandidateCount);
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

    private void DrawModsPanel(Guid id, CachedOutfit details)
    {
        if (!Pills.DrawCollapsibleSubheader("Mod Associations", ref modsPanelOpen))
            return;

        ImGui.Indent();

        var layers = ResolveLayersForDisplay(id);
        var inherited = CollectInheritedMods(details, layers);

        if (details.Mods.Count == 0 && inherited.Count == 0)
        {
            ImGui.TextDisabled("No mods associated with this design");
            ImGui.Unindent();
            ImGui.Spacing();
            return;
        }

        foreach (var mod in details.Mods)
            DrawModRow(mod);

        foreach (var (mod, sourceDesignId) in inherited)
            DrawModRow(mod, sourceDesignId);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Mods responsible for an equipment/bonus item or the hairstyle actually inherited from a layer -
    // i.e. exactly the same attribution already shown inline on those rows, collected in one place.
    // Deduped by directory against both the base design's own list and each other.
    private List<(CachedMod Mod, Guid SourceDesignId)> CollectInheritedMods(CachedOutfit details, DesignLayerResolutionService.Result? layers)
    {
        var result = new List<(CachedMod, Guid)>();
        if (layers == null)
            return result;

        var seen = new HashSet<string>(details.Mods.Select(m => m.Directory), StringComparer.Ordinal);

        void Add(DesignLayerResolutionService.FieldSource winner, CachedMod? mod)
        {
            if (mod == null || !seen.Add(mod.Directory))
                return;
            result.Add((mod, winner.SourceDesignId));
        }

        foreach (var slot in Enum.GetValues<EquipmentSlot>())
        {
            if (!layers.ItemSource.TryGetValue(slot, out var res) || res.Winner is not { IsBaseDesign: false } w)
                continue;
            if (ResolveLayerEquipmentEntry(res.Winner, slot) is not { Apply: true } entry)
                continue;
            if (!plugin.Configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var sourceOutfit))
                continue;
            var itemName = plugin.GameData.ResolveItemName(entry.ItemId);
            Add(w, GetAffectedBy(w.SourceDesignId, sourceOutfit).Mods.GetValueOrDefault(itemName));
        }

        foreach (var (slotKey, res) in layers.BonusItemSource)
        {
            if (res.Winner is not { IsBaseDesign: false } w)
                continue;
            if (ResolveLayerBonusEntry(res.Winner, slotKey) is not { Apply: true } entry)
                continue;
            if (!plugin.Configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var sourceOutfit))
                continue;
            var itemName = plugin.GameData.ResolveBonusItemName(entry.Slot, entry.ItemId);
            Add(w, GetAffectedBy(w.SourceDesignId, sourceOutfit).Mods.GetValueOrDefault(itemName));
        }

        if (layers.CustomizationSource.TryGetValue("Hairstyle", out var hairRes)
            && hairRes.Winner is { IsBaseDesign: false } hw
            && plugin.Configuration.CachedOutfits.TryGetValue(hw.SourceDesignId, out var hairSourceOutfit))
        {
            Add(hw, GetAffectedBy(hw.SourceDesignId, hairSourceOutfit).Hairstyle);
        }

        return result;
    }

    private void DrawModRow(CachedMod mod, Guid? inheritedFromLayerId = null)
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

        if (inheritedFromLayerId is { } layerId)
            DrawInheritedLink(layerId);
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
