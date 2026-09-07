using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Designs;

// The inverse of DesignAttributionService: that matches a design's own mods against its own items, this
// matches every enabled mod in the player's live Penumbra collection against what's actually equipped.
public sealed class GearImportService
{
    private readonly Configuration configuration;
    private readonly GameDataService gameData;
    private readonly PenumbraService penumbra;
    private readonly GlamourerService glamourer;

    public GearImportService(Configuration configuration, GameDataService gameData, PenumbraService penumbra, GlamourerService glamourer)
    {
        this.configuration = configuration;
        this.gameData = gameData;
        this.penumbra = penumbra;
        this.glamourer = glamourer;
    }

    public sealed record CaptureResult(bool Success, string? Error, GearImportOverride? Override);

    public CaptureResult Capture(CachedOutfit design, DesignLayerResolutionService.Result layers)
    {
        var (result, state) = glamourer.GetState();
        if (result != GlamourerApiEc.Success || state == null)
            return new CaptureResult(false, $"Couldn't read current Glamourer state ({result}).", null);

        bool IsLayerWinner(DesignLayerResolutionService.FieldResolution res)
            => layers.HasAnyLayers && res.Winner is { IsBaseDesign: false };

        var savedDesignJson = glamourer.GetDesignJObject(design.ProviderDesignId);
        var savedEquipment = GlamourerService.ParseEquipment(savedDesignJson?["Equipment"] as JObject);
        var savedBonusItems = GlamourerService.ParseBonusItems(savedDesignJson?["Bonus"]);
        var savedCustomize = savedDesignJson?["Customize"] as JObject;
        var savedCustomizations = GlamourerJsonSchema.ParseCustomizations(savedCustomize);
        var savedClan = (int)GlamourerJsonSchema.ReadUInt64(savedCustomize?["Clan"]?["Value"]);
        var savedGender = (int)GlamourerJsonSchema.ReadUInt64(savedCustomize?["Gender"]?["Value"]);
        var savedClanApplied = GlamourerJsonSchema.ReadBool(savedCustomize?["Clan"]?["Apply"]);
        var savedGenderApplied = GlamourerJsonSchema.ReadBool(savedCustomize?["Gender"]?["Apply"]);

        var customize = state["Customize"] as JObject;
        var liveEquipment = GlamourerService.ParseEquipment(state["Equipment"] as JObject);
        var liveBonusItems = GlamourerService.ParseBonusItems(state["Bonus"]);
        var liveCustomizations = GlamourerJsonSchema.ParseCustomizations(customize);
        var liveClan = (int)GlamourerJsonSchema.ReadUInt64(customize?["Clan"]?["Value"]);
        var liveGender = (int)GlamourerJsonSchema.ReadUInt64(customize?["Gender"]?["Value"]);

        var designEquipmentBySlot = savedEquipment.ToDictionary(e => e.Slot);
        var liveEquipmentBySlot = liveEquipment.ToDictionary(e => e.Slot);
        var equipment = new List<CachedEquipmentSlot>();
        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            var itemRes = layers.ItemSource.GetValueOrDefault(slot);
            var live = liveEquipmentBySlot.GetValueOrDefault(slot);
            var liveIsWorn = live != null && gameData.ResolveItemName(live.ItemId) != GameDataService.NothingItemName;
            var isLayerWinner = IsLayerWinner(itemRes);
            var layerEntry = isLayerWinner ? ResolveLayerEquipment(itemRes.Winner, slot) : null;
            var matchesLayer = MatchesEquipment(live, layerEntry);

            // A layer-won slot defers to the layer unless something worn there actually differs from
            // what the layer itself supplies - matching the layer exactly just means nothing was
            // overridden, so keep the design's own (empty) entry rather than baking the layer's item in.
            if (isLayerWinner && (!liveIsWorn || matchesLayer))
            {
                if (designEquipmentBySlot.TryGetValue(slot, out var existing))
                    equipment.Add(existing);
            }
            else if (live != null)
            {
                equipment.Add(live);
            }
        }

        var designBonusBySlot = savedBonusItems.ToDictionary(b => b.Slot);
        var liveBonusBySlot = liveBonusItems.ToDictionary(b => b.Slot);
        var bonusItems = new List<CachedBonusItem>();
        foreach (var slotKey in liveBonusBySlot.Keys.Union(designBonusBySlot.Keys))
        {
            var bonusRes = layers.BonusItemSource.GetValueOrDefault(slotKey);
            var live = liveBonusBySlot.GetValueOrDefault(slotKey);
            var liveIsWorn = live != null && gameData.ResolveBonusItemName(live.Slot, live.ItemId) != GameDataService.NothingItemName;

            if (IsLayerWinner(bonusRes) && (!liveIsWorn || MatchesBonusItem(live, ResolveLayerBonusItem(bonusRes.Winner, slotKey))))
            {
                if (designBonusBySlot.TryGetValue(slotKey, out var existing))
                    bonusItems.Add(existing);
            }
            else if (live != null)
            {
                bonusItems.Add(live);
            }
        }

        var liveByKey = liveCustomizations.ToDictionary(c => c.Key);
        var changedCustomizations = savedCustomizations
            .Where(existing => !IsLayerWinner(layers.CustomizationSource.GetValueOrDefault(existing.Key))
                             && liveByKey.TryGetValue(existing.Key, out var live) && live.RawValue != existing.RawValue)
            .Select(existing => liveByKey[existing.Key])
            .ToList();

        var clanIsLayerWinner = IsLayerWinner(layers.CustomizationSource.GetValueOrDefault("Clan"));
        var genderIsLayerWinner = IsLayerWinner(layers.CustomizationSource.GetValueOrDefault("Gender"));

        var ov = new GearImportOverride
        {
            Equipment = equipment,
            BonusItems = bonusItems,
            Customizations = changedCustomizations,
            CustomizeClan = savedClanApplied && !clanIsLayerWinner && liveClan != savedClan ? liveClan : savedClan,
            CustomizeGender = savedGenderApplied && !genderIsLayerWinner && liveGender != savedGender ? liveGender : savedGender,
            CustomizeClanApplied = savedClanApplied,
            CustomizeGenderApplied = savedGenderApplied,
        };

        var hairstyleIsLayerWinner = IsLayerWinner(layers.CustomizationSource.GetValueOrDefault("Hairstyle"));
        ov.Mods = DetectActiveMods(equipment, bonusItems, liveCustomizations, liveClan, liveGender, hairstyleIsLayerWinner);
        return new CaptureResult(true, null, ov);
    }

    // Same lookup as MainWindow.EquipmentMods.cs's ResolveLayerEquipmentEntry - the winning layer's own
    // saved value for this slot, so it can be compared against what's actually worn.
    private CachedEquipmentSlot? ResolveLayerEquipment(DesignLayerResolutionService.FieldSource? winner, EquipmentSlot slot)
    {
        if (winner is not { IsBaseDesign: false } w)
            return null;
        if (!configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var outfit))
            return null;
        return outfit.Equipment.FirstOrDefault(e => e.Slot == slot);
    }

    private CachedBonusItem? ResolveLayerBonusItem(DesignLayerResolutionService.FieldSource? winner, string slotKey)
    {
        if (winner is not { IsBaseDesign: false } w)
            return null;
        if (!configuration.CachedOutfits.TryGetValue(w.SourceDesignId, out var outfit))
            return null;
        return outfit.BonusItems.FirstOrDefault(b => b.Slot == slotKey);
    }

    private static bool MatchesEquipment(CachedEquipmentSlot? live, CachedEquipmentSlot? layerEntry)
        => live != null && layerEntry != null
        && live.ItemId == layerEntry.ItemId && live.Stain == layerEntry.Stain && live.Stain2 == layerEntry.Stain2;

    private static bool MatchesBonusItem(CachedBonusItem? live, CachedBonusItem? layerEntry)
        => live != null && layerEntry != null && live.ItemId == layerEntry.ItemId;

    // Highest-priority mod wins per item/hairstyle, same as DesignAttributionService.Build. Matches
    // against the layer-filtered equipment/bonus lists (not the raw live ones) so a mod that only
    // affects a layer-inherited item is never attributed to this design.
    private List<CachedMod> DetectActiveMods(List<CachedEquipmentSlot> equipment, List<CachedBonusItem> bonusItems,
        List<CachedCustomization> liveCustomizations, int liveClan, int liveGender, bool hairstyleIsLayerWinner)
    {
        var result = new List<CachedMod>();

        var itemNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in equipment)
        {
            var name = gameData.ResolveItemName(e.ItemId);
            if (name != GameDataService.NothingItemName)
                itemNames.Add(name);
        }
        foreach (var b in bonusItems)
        {
            var name = gameData.ResolveBonusItemName(b.Slot, b.ItemId);
            if (name != GameDataService.NothingItemName)
                itemNames.Add(name);
        }

        var hairstyle = hairstyleIsLayerWinner ? null : liveCustomizations.FirstOrDefault(c => c.Key == "Hairstyle");
        var hairFragment = hairstyle == null ? null : HairAttribution.HairFragment(liveClan, liveGender);
        var hairValue = hairstyle?.RawValue ?? 0;

        if (itemNames.Count == 0 && hairFragment == null)
            return result;

        var collectionId = penumbra.GetLocalPlayerCollectionId();
        if (collectionId == null)
            return result;

        var allSettings = penumbra.GetAllModSettings(collectionId.Value);
        if (allSettings.Count == 0)
            return result;

        var names = penumbra.GetModDisplayNames();
        var matchedItems = new HashSet<string>(StringComparer.Ordinal);
        var matchedHair = false;

        foreach (var (directory, settings) in allSettings.OrderByDescending(kv => kv.Value.Priority))
        {
            if (!settings.Enabled)
                continue;

            var displayName = names.TryGetValue(directory, out var n) ? n : directory;
            var changed = penumbra.GetChangedItemNames(directory, displayName);
            if (changed.Count == 0)
                continue;

            var touchesItem = itemNames.Any(i => !matchedItems.Contains(i) && changed.Contains(i));
            var touchesHair = !matchedHair && hairFragment != null && changed.Any(k => HairAttribution.KeyMatches(k, hairFragment, hairValue));
            if (!touchesItem && !touchesHair)
                continue;

            foreach (var i in itemNames)
                if (changed.Contains(i))
                    matchedItems.Add(i);
            if (touchesHair)
                matchedHair = true;

            result.Add(new CachedMod
            {
                Name = displayName,
                Directory = directory,
                State = ModState.Enabled,
                Priority = settings.Priority,
                // Same comma-joined-per-group convention DesignApplyService splits back on apply.
                Settings = settings.Settings.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value)),
            });
        }

        return result;
    }

    // Override entries replace the base entry for that Key; every other base entry is left untouched.
    public static List<CachedCustomization> MergeCustomizations(List<CachedCustomization> baseList, List<CachedCustomization> overrideList)
    {
        var byKey = baseList.ToDictionary(c => c.Key);
        foreach (var c in overrideList)
            byKey[c.Key] = c;
        return byKey.Values.ToList();
    }

    // A mod the design already references (any state) is left alone; only new directories get appended.
    public static List<CachedMod> MergeMods(List<CachedMod> baseMods, List<CachedMod> overrideMods)
    {
        var existingDirs = new HashSet<string>(baseMods.Select(m => m.Directory), StringComparer.OrdinalIgnoreCase);
        var merged = new List<CachedMod>(baseMods);
        merged.AddRange(overrideMods.Where(m => !existingDirs.Contains(m.Directory)));
        return merged;
    }

    public static void ApplyOverlay(CachedOutfit outfit, GearImportOverride ov)
    {
        outfit.Equipment = ov.Equipment;
        outfit.BonusItems = ov.BonusItems;
        outfit.Customizations = MergeCustomizations(outfit.Customizations, ov.Customizations);
        outfit.CustomizeClan = ov.CustomizeClan;
        outfit.CustomizeGender = ov.CustomizeGender;
        outfit.CustomizeClanApplied = ov.CustomizeClanApplied;
        outfit.CustomizeGenderApplied = ov.CustomizeGenderApplied;
        outfit.Mods = MergeMods(outfit.Mods, ov.Mods);
    }
}
