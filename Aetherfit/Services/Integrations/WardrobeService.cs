using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfit.Utils;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;

namespace Aetherfit.Services.Integrations;

// Wardrobe has no IPC of its own - reads outfits straight from its config file, relays equipment
// through Glamourer, and activates mods itself via Penumbra temporary settings, same as
// SimpleGlamourSwitcherService. Wardrobe's own wear flow instead flips mods permanently in the real
// collection (Penumbra.TrySetMod.V5); Aetherfit deliberately uses its own non-destructive path here.
public sealed class WardrobeService : IDesignProvider
{
    private readonly GlamourerService glamourer;
    private readonly PenumbraService penumbra;

    private readonly Dictionary<Guid, JObject> lastKnownOutfits = new();
    private readonly Dictionary<Guid, JObject> lastKnownItems = new();

    public WardrobeService(GlamourerService glamourer, PenumbraService penumbra)
    {
        this.glamourer = glamourer;
        this.penumbra = penumbra;
    }

    public DesignSource Source => DesignSource.Wardrobe;
    public string DisplayName => "Wardrobe";
    public DesignProviderCapabilities Capabilities => DesignProviderCapabilities.Apply | DesignProviderCapabilities.Mods;

    // Wardrobe's own EquipSlot values (Models/EquipSlot.cs) - persisted as-is and explicitly never
    // renumbered per that file's own comments, so hardcoding them here is as safe as reading an enum.
    private const int ModCategoryFloor = 30; // 30/31/32 = Animation/Vfx/Mount - not exclusive per slot
    private static readonly int[] ExclusiveWardrobeSlots = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 20, 21, 22, 23, 24, 25 };
    private const int MaxNonExclusiveItems = 16; // generous cap on simultaneous Animation/Vfx/Mount items in one outfit

    // Only the true equipment slots (1-12) have a Glamourer item to equip; 20-25 are pure model/texture
    // replacers with no Glamourer field at all - see EquipSlot.cs's own IsCustomization/IsModOnly.
    private static readonly (int WardrobeSlot, string WardrobeName, EquipmentSlot AetherfitSlot)[] EquipmentSlotMap =
    {
        (1, "Head", EquipmentSlot.Head), (2, "Body", EquipmentSlot.Body), (3, "Hands", EquipmentSlot.Hands),
        (4, "Legs", EquipmentSlot.Legs), (5, "Feet", EquipmentSlot.Feet), (6, "Ears", EquipmentSlot.Ears),
        (7, "Neck", EquipmentSlot.Neck), (8, "Wrists", EquipmentSlot.Wrists),
        (9, "RingRight", EquipmentSlot.RFinger), (10, "RingLeft", EquipmentSlot.LFinger),
        (11, "MainHand", EquipmentSlot.MainHand), (12, "OffHand", EquipmentSlot.OffHand),
    };

    private static readonly Dictionary<string, EquipmentSlot> VanillaSlotNameMap =
        EquipmentSlotMap.ToDictionary(t => t.WardrobeName, t => t.AetherfitSlot, StringComparer.OrdinalIgnoreCase);

    // Own reserved key range for Penumbra temp mod settings, distinct from SGS's own 0x_41_45_54_0 ("AET").
    private const int KeyBase = 0x_41_45_57_0; // "AEW"
    private static int SlotKey(int wardrobeSlot) => -(KeyBase + wardrobeSlot);
    private static int ItemKey(int indexInOutfit) => -(KeyBase + 100 + indexInOutfit);

    private static string ConfigPath =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.Parent!.FullName, "WardrobePlugin.json");

    public PluginIntegrationInfo CheckIntegration()
    {
        if (PluginIntegrationCheck.CheckInstalledAndLoaded("WardrobePlugin", out var exposed) is { } early)
            return early;

        return File.Exists(ConfigPath)
            ? new PluginIntegrationInfo(PluginIntegrationStatus.Ok, exposed!.Version, null)
            : new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed!.Version, null);
    }

    public ProviderDesignListResult FetchDesignList()
    {
        if (!File.Exists(ConfigPath))
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), null);

        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(ConfigPath));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read Wardrobe's config file");
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), "Failed to read Wardrobe's config file.");
        }

        lastKnownOutfits.Clear();
        lastKnownItems.Clear();

        if (root["WardrobeItems"] is JArray itemsArray)
            foreach (var item in itemsArray.OfType<JObject>())
                if (Guid.TryParse(GlamourerJsonSchema.ReadString(item["Id"]), out var itemId))
                    lastKnownItems[itemId] = item;

        var designs = new List<ProviderDesignInfo>();
        if (root["Outfits"] is JArray outfitsArray)
        {
            foreach (var outfit in outfitsArray.OfType<JObject>())
            {
                if (!Guid.TryParse(GlamourerJsonSchema.ReadString(outfit["Id"]), out var outfitId))
                    continue;
                var name = GlamourerJsonSchema.ReadString(outfit["Name"]);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                lastKnownOutfits[outfitId] = outfit;
                designs.Add(new ProviderDesignInfo(outfitId, name, name, Color: 0));
            }
        }

        return new ProviderDesignListResult(designs, null);
    }

    public CachedOutfit? FetchDesignMetadata(Guid nativeId)
        => lastKnownOutfits.TryGetValue(nativeId, out var outfit) ? ParseOutfit(outfit) : null;

    private CachedOutfit ParseOutfit(JObject outfit)
    {
        var name = GlamourerJsonSchema.ReadString(outfit["Name"]) ?? "(unnamed)";
        var tags = (outfit["Tags"] as JArray)?.Select(t => t.ToString())
            .Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();

        var items = ResolveOutfitItems(outfit);
        var equipment = ResolveEquipment(outfit, items);

        var mods = new Dictionary<string, CachedMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.ExclusiveItems.Values.Concat(items.NonExclusiveItems))
            CollectItemMods(item, mods);

        return new CachedOutfit
        {
            Name = name,
            Equipment = equipment.Select(kv => new CachedEquipmentSlot
            {
                Slot = kv.Key,
                ItemId = kv.Value.ItemId,
                Stain = kv.Value.Stain1,
                Stain2 = kv.Value.Stain2,
                Apply = true,
                ApplyStain = true,
            }).ToList(),
            Mods = mods.Values.ToList(),
            // Seeds Aetherfit's own tag store on first sight only - GetOrSeedDesignMeta ignores this
            // after that, same pattern GlamaholicService uses for its own plates.
            GlamourerTags = tags,
        };
    }

    private readonly record struct ResolvedOutfitItems(Dictionary<int, JObject> ExclusiveItems, List<JObject> NonExclusiveItems);

    private ResolvedOutfitItems ResolveOutfitItems(JObject outfit)
    {
        var exclusive = new Dictionary<int, JObject>();
        var nonExclusive = new List<JObject>();

        if (outfit["ItemIds"] is JArray itemIds)
        {
            foreach (var idToken in itemIds)
            {
                if (!Guid.TryParse(idToken.ToString(), out var itemId) || !lastKnownItems.TryGetValue(itemId, out var item))
                    continue;

                var wardrobeSlot = GlamourerJsonSchema.ReadInt32(item["Slot"], 0);
                if (wardrobeSlot >= ModCategoryFloor)
                    nonExclusive.Add(item);
                else if (wardrobeSlot > 0)
                    exclusive[wardrobeSlot] = item; // last one wins if an outfit somehow has two for the same exclusive slot
            }
        }

        return new ResolvedOutfitItems(exclusive, nonExclusive);
    }

    // Resolves the 12 real equipment slots only - the customization-only slots (20-25) have nothing to
    // put here, they contribute mods only (see CollectItemMods/ApplyMods).
    private static Dictionary<EquipmentSlot, (ulong ItemId, byte Stain1, byte Stain2)> ResolveEquipment(JObject outfit, ResolvedOutfitItems items)
    {
        var result = new Dictionary<EquipmentSlot, (ulong, byte, byte)>();
        var dyes = outfit["Dyes"] as JObject;

        foreach (var (wardrobeSlot, _, aetherfitSlot) in EquipmentSlotMap)
        {
            if (!items.ExclusiveItems.TryGetValue(wardrobeSlot, out var item))
                continue;
            var glamId = GlamourerJsonSchema.ReadUInt64(item["GlamourerItemId"]);
            if (glamId == 0)
                continue;

            var itemIdStr = GlamourerJsonSchema.ReadString(item["Id"]);
            var dye = itemIdStr != null ? dyes?[itemIdStr] as JObject : null;
            result[aetherfitSlot] = (glamId,
                (byte)GlamourerJsonSchema.ReadUInt64(dye?["Stain1"]),
                (byte)GlamourerJsonSchema.ReadUInt64(dye?["Stain2"]));
        }

        // Plain game items filling slots none of the outfit's own items cover.
        if (outfit["VanillaItems"] is JObject vanillaItems)
        {
            foreach (var prop in vanillaItems.Properties())
            {
                if (!VanillaSlotNameMap.TryGetValue(prop.Name, out var slot) || result.ContainsKey(slot))
                    continue;
                if (prop.Value is not JObject piece)
                    continue;

                result[slot] = (GlamourerJsonSchema.ReadUInt64(piece["ItemId"]),
                    (byte)GlamourerJsonSchema.ReadUInt64(piece["Stain1"]),
                    (byte)GlamourerJsonSchema.ReadUInt64(piece["Stain2"]));
            }
        }

        return result;
    }

    // Deduped by directory - the same mod can appear on more than one item (e.g. an upscale shared
    // across a set). Display-only: the values here feed the gallery/health report, not the live apply.
    private static void CollectItemMods(JObject item, Dictionary<string, CachedMod> mods)
    {
        if (item["Mods"] is not JArray modsArray)
            return;

        foreach (var modRef in modsArray.OfType<JObject>())
        {
            var directory = GlamourerJsonSchema.ReadString(modRef["ModDirectory"]);
            if (string.IsNullOrWhiteSpace(directory) || mods.ContainsKey(directory))
                continue;

            mods[directory] = new CachedMod
            {
                Name = GlamourerJsonSchema.ReadString(modRef["ModName"]) ?? string.Empty,
                Directory = directory,
                State = ModState.Enabled,
                Settings = ResolveModSettings(modRef).ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value)),
            };
        }
    }

    // Options (single-select) always contributes; MultiOptions (legacy multi-select) fills any group
    // Options didn't; OptionStates (tri-state) is read last and overrides both per-group - exactly the
    // precedence ModReference's own doc comment describes.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ResolveModSettings(JObject modRef)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        if (modRef["Options"] is JObject options)
            foreach (var prop in options.Properties())
            {
                var value = GlamourerJsonSchema.ReadString(prop.Value);
                if (!string.IsNullOrEmpty(value))
                    result[prop.Name] = new List<string> { value };
            }

        if (modRef["MultiOptions"] is JObject multiOptions)
            foreach (var prop in multiOptions.Properties())
                if (prop.Value is JArray arr)
                    result[prop.Name] = arr.Select(v => v.ToString()).ToList();

        if (modRef["OptionStates"] is JObject optionStates)
            foreach (var prop in optionStates.Properties())
                if (prop.Value is JObject states)
                    result[prop.Name] = states.Properties()
                        .Where(p => GlamourerJsonSchema.ReadBool(p.Value))
                        .Select(p => p.Name).ToList();

        return result;
    }

    public bool Apply(Guid nativeId, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false)
    {
        if (!lastKnownOutfits.TryGetValue(nativeId, out var outfit))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": outfit not found in Wardrobe.");
            return false;
        }

        var items = ResolveOutfitItems(outfit);
        var equipment = ResolveEquipment(outfit, items);

        // Mods before the state push, so Glamourer's own redraw already sees them resolved.
        ApplyMods(items);

        return glamourer.RelayApply(nativeId, designName, quiet, "Wardrobe",
            state => state["Equipment"] = BuildEquipmentJObject(equipment),
            state => glamourer.ApplyEquipmentState(state));
    }

    private static JObject BuildEquipmentJObject(Dictionary<EquipmentSlot, (ulong ItemId, byte Stain1, byte Stain2)> resolved)
    {
        var result = new JObject();
        foreach (var (_, _, slot) in EquipmentSlotMap)
        {
            var apply = resolved.TryGetValue(slot, out var piece);
            result[slot.ToString()] = new JObject
            {
                ["ItemId"] = apply ? (long)piece.ItemId : 0L,
                ["Stain"] = apply ? piece.Stain1 : (byte)0,
                ["Stain2"] = apply ? piece.Stain2 : (byte)0,
                ["Apply"] = apply,
                ["ApplyStain"] = apply,
            };
        }
        return result;
    }

    private void ApplyMods(ResolvedOutfitItems items)
    {
        foreach (var wardrobeSlot in ExclusiveWardrobeSlots)
            ApplySlotMods(SlotKey(wardrobeSlot), items.ExclusiveItems.GetValueOrDefault(wardrobeSlot));

        for (var i = 0; i < MaxNonExclusiveItems; i++)
            ApplySlotMods(ItemKey(i), i < items.NonExclusiveItems.Count ? items.NonExclusiveItems[i] : null);
    }

    private void ApplySlotMods(int key, JObject? item)
    {
        penumbra.RemoveAllTemporaryModSettingsPlayer(key);
        if (item?["Mods"] is not JArray modsArray)
            return;

        const string source = "Aetherfit (via Wardrobe)";
        foreach (var modRef in modsArray.OfType<JObject>())
        {
            var directory = GlamourerJsonSchema.ReadString(modRef["ModDirectory"]);
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var settings = ResolveModSettings(modRef);
            var result = penumbra.SetTemporaryModSettingsPlayer(directory, true, 0, settings, source, key);
            if (result != PenumbraApiEc.Success)
                Plugin.Log.Warning("Failed to set temporary mod settings for {Mod} ({Key}): {Result}", directory, key, result);
        }
    }

    public void ClearAllTemporaryModSettings()
    {
        foreach (var wardrobeSlot in ExclusiveWardrobeSlots)
            penumbra.RemoveAllTemporaryModSettingsPlayer(SlotKey(wardrobeSlot));
        for (var i = 0; i < MaxNonExclusiveItems; i++)
            penumbra.RemoveAllTemporaryModSettingsPlayer(ItemKey(i));
    }

    public string? GetNativeImagePath(Guid nativeId) => null;

    public void ApplyLayer(Guid nativeId) { }
    public void OpenInNativeUi(Guid nativeId, string designName) { }
    public void Revert() { }

    public event Action<nint, DesignFinalizationType>? OnExternalStateFinalized { add { } remove { } }
    public event Action<nint, DesignFinalizationType>? OnAnyStateFinalized { add { } remove { } }

    public void Dispose() { }
}
