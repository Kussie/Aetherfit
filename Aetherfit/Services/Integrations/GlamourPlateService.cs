using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Aetherfit.Utils;
using FFXIVClientStructs.FFXIV.Client.Game;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Integrations;

[SupportedOSPlatform("windows")]
public sealed class GlamourPlateService : IDesignProvider
{
    private const int PlateCount = 20;
    private const int SlotCount = 12;

    private readonly GlamourerService glamourer;
    private readonly Configuration configuration;

    private readonly Dictionary<Guid, PlateSnapshot> lastKnownPlates = new();

    public GlamourPlateService(GlamourerService glamourer, Configuration configuration)
    {
        this.glamourer = glamourer;
        this.configuration = configuration;
    }

    public DesignSource Source => DesignSource.GlamourPlate;
    public string DisplayName => "Glamour Plates";
    public DesignProviderCapabilities Capabilities => DesignProviderCapabilities.Apply;

    private readonly record struct PlateSnapshot(string Name, uint[] ItemIds, byte[] Stain0Ids, byte[] Stain1Ids);

    private static readonly EquipmentSlot[] SlotMap =
    {
        EquipmentSlot.MainHand,
        EquipmentSlot.OffHand,
        EquipmentSlot.Head,
        EquipmentSlot.Body,
        EquipmentSlot.Hands,
        EquipmentSlot.Legs,
        EquipmentSlot.Feet,
        EquipmentSlot.Ears,
        EquipmentSlot.Neck,
        EquipmentSlot.Wrists,
        EquipmentSlot.RFinger,
        EquipmentSlot.LFinger,
    };

    public PluginIntegrationInfo CheckIntegration()
    {
        try
        {
            return CheckIntegrationUnsafe();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to check native Glamour Plate availability");
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, null, null);
        }
    }

    private static unsafe PluginIntegrationInfo CheckIntegrationUnsafe()
    {
        var mgr = MirageManager.Instance();
        var ok = mgr != null && mgr->GlamourPlatesLoaded;
        return new PluginIntegrationInfo(ok ? PluginIntegrationStatus.Ok : PluginIntegrationStatus.NotLoaded, null, null);
    }

    public ProviderDesignListResult FetchDesignList()
    {
        try
        {
            return FetchDesignListUnsafe();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read native Glamour Plates");
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), ex.Message);
        }
    }

    private unsafe ProviderDesignListResult FetchDesignListUnsafe()
    {
        var mgr = MirageManager.Instance();
        if (mgr == null || !mgr->GlamourPlatesLoaded)
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(),
                "Not yet loaded this zone - open the Glamour Dresser once.");

        var designs = new List<ProviderDesignInfo>();
        for (var i = 0; i < PlateCount; i++)
        {
            var plate = mgr->GlamourPlates[i];
            var itemIds = new uint[SlotCount];
            var stain0 = new byte[SlotCount];
            var stain1 = new byte[SlotCount];
            var any = false;
            for (var slot = 0; slot < SlotCount; slot++)
            {
                itemIds[slot] = plate.ItemIds[slot];
                stain0[slot] = plate.Stain0Ids[slot];
                stain1[slot] = plate.Stain1Ids[slot];
                if (itemIds[slot] != 0)
                    any = true;
            }
            if (!any)
                continue; // never-configured plate slot

            var name = $"Glamour Plate {i + 1}";
            var nativeId = SyntheticPlateId(i);
            lastKnownPlates[nativeId] = new PlateSnapshot(name, itemIds, stain0, stain1);
            designs.Add(new ProviderDesignInfo(nativeId, name, name, Color: 0));
        }

        return new ProviderDesignListResult(designs, null);
    }

    public CachedOutfit? FetchDesignMetadata(Guid nativeId)
        => lastKnownPlates.TryGetValue(nativeId, out var plate)
            ? new CachedOutfit { Name = plate.Name, Equipment = BuildEquipment(plate) }
            : null;

    public bool Apply(Guid nativeId, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false)
    {
        if (!TryGetPlate(nativeId, out var plate))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": plate data not currently available - try refreshing.");
            return false;
        }

        return glamourer.RelayApply(nativeId, designName, quiet, "Glamour Plate", state =>
        {
            state["Equipment"] = BuildEquipmentJObject(plate);
        }, s => glamourer.ApplyEquipmentState(s));
    }

    private static JObject BuildEquipmentJObject(PlateSnapshot plate, bool skipEmptySlots = false)
    {
        var equipment = new JObject();
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var apply = !skipEmptySlots || plate.ItemIds[slot] != 0;
            equipment[SlotMap[slot].ToString()] = new JObject
            {
                ["ItemId"] = plate.ItemIds[slot],
                ["Stain"] = plate.Stain0Ids[slot],
                ["Stain2"] = plate.Stain1Ids[slot],
                ["Apply"] = apply,
                ["ApplyStain"] = apply,
            };
        }
        return equipment;
    }

    public JObject? BuildImportPayload(Guid nativeId)
        => TryGetPlate(nativeId, out var plate) ? BuildEquipmentJObject(plate, skipEmptySlots: true) : null;

    private bool TryGetPlate(Guid nativeId, out PlateSnapshot plate)
    {
        if (lastKnownPlates.TryGetValue(nativeId, out plate))
            return true;

        if (configuration.DesignIdentityMap.TryGetValue(DesignSource.GlamourPlate, out var map)
            && map.TryGetValue(nativeId, out var aetherfitId)
            && configuration.CachedOutfits.TryGetValue(aetherfitId, out var outfit))
        {
            var itemIds = new uint[SlotCount];
            var stain0 = new byte[SlotCount];
            var stain1 = new byte[SlotCount];
            foreach (var cachedSlot in outfit.Equipment)
            {
                var index = Array.IndexOf(SlotMap, cachedSlot.Slot);
                if (index < 0) continue;
                itemIds[index] = (uint)cachedSlot.ItemId;
                stain0[index] = cachedSlot.Stain;
                stain1[index] = cachedSlot.Stain2;
            }
            plate = new PlateSnapshot(outfit.Name, itemIds, stain0, stain1);
            return true;
        }

        plate = default;
        return false;
    }

    private static List<CachedEquipmentSlot> BuildEquipment(PlateSnapshot plate)
    {
        var result = new List<CachedEquipmentSlot>();
        for (var slot = 0; slot < SlotCount; slot++)
        {
            if (plate.ItemIds[slot] == 0)
                continue;

            result.Add(new CachedEquipmentSlot
            {
                Slot = SlotMap[slot],
                ItemId = plate.ItemIds[slot],
                Stain = plate.Stain0Ids[slot],
                Stain2 = plate.Stain1Ids[slot],
                Apply = true,
                ApplyStain = true,
            });
        }
        return result;
    }

    // Native plates have no identity of their own (just a 0-19 index per character), so this mints a
    // deterministic id scoped to the character - switching characters never aliases one character's
    // "Plate 1" onto another's.
    private static Guid SyntheticPlateId(int plateIndex)
    {
        var contentId = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"glamourplate:{contentId}:{plateIndex}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    // No layer/native-UI/revert concept of its own - Capabilities has no Apply-adjacent flags for
    // these, so the UI never calls them, but IDesignProvider still requires an implementation.
    public void ApplyLayer(Guid nativeId) { }
    public void OpenInNativeUi(Guid nativeId, string designName) { }
    public void Revert() { }
    public string? GetNativeImagePath(Guid nativeId) => null;

    public event Action<nint, DesignFinalizationType>? OnExternalStateFinalized { add { } remove { } }
    public event Action<nint, DesignFinalizationType>? OnAnyStateFinalized { add { } remove { } }

    public void Dispose() { }
}
