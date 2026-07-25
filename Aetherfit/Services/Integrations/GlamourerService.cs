using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Utils;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Integrations;

public sealed class GlamourerService : IDisposable
{
    private readonly GetDesignListExtended getDesignListExtended;
    private readonly GetDesignJObject getDesignJObject;
    private readonly ApplyDesign applyDesign;
    private readonly RevertState revertState;
    private readonly OpenDesign openDesign;
    private readonly GetState getState;
    private readonly ApplyState applyState;
    private readonly EventSubscriber<nint, StateFinalizationType> stateFinalized;

    private bool invokingOwnChange;
    private DateTime lastOwnChangeUtc = DateTime.MinValue;

    public event Action<nint, StateFinalizationType>? OnExternalStateFinalized;

    public event Action<nint, StateFinalizationType>? OnAnyStateFinalized;

    public GlamourerService()
    {
        getDesignListExtended = new GetDesignListExtended(Plugin.PluginInterface);
        getDesignJObject = new GetDesignJObject(Plugin.PluginInterface);
        applyDesign = new ApplyDesign(Plugin.PluginInterface);
        revertState = new RevertState(Plugin.PluginInterface);
        openDesign = new OpenDesign(Plugin.PluginInterface);
        getState = new GetState(Plugin.PluginInterface);
        applyState = new ApplyState(Plugin.PluginInterface);
        stateFinalized = StateFinalized.Subscriber(Plugin.PluginInterface, OnStateFinalized);
    }

    public void Dispose() => stateFinalized.Dispose();

    public static readonly (int Major, int Minor) MinApiVersion = (1, 8);

    public PluginIntegrationInfo CheckIntegration()
    {
        var exposed = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Glamourer");
        if (exposed == null)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotInstalled, null, null);
        if (!exposed.IsLoaded)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);

        try
        {
            var version = new ApiVersion(Plugin.PluginInterface).Invoke();
            var ok = version.Major == MinApiVersion.Major && version.Minor >= MinApiVersion.Minor;
            return new PluginIntegrationInfo(ok ? PluginIntegrationStatus.Ok : PluginIntegrationStatus.VersionTooLow, exposed.Version, version);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Glamourer API version");
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);
        }
    }

    private void OnStateFinalized(nint actor, StateFinalizationType type)
    {
        // Glamourer raises this for our own IPC calls too: the flag catches synchronous delivery,
        // the grace window catches anything Glamourer defers past our call. Deferred redraws can
        // take several seconds when many mods are still loading (e.g. right after login), so the
        // window has to be generous or our own apply gets misread as an external change.
        if (invokingOwnChange)
            return;

        OnAnyStateFinalized?.Invoke(actor, type);

        if (DateTime.UtcNow - lastOwnChangeUtc < TimeSpan.FromSeconds(5))
        {
            Plugin.Log.Debug("StateFinalized ({Type}) within the own-change window; not treating it as external.", type);
            return;
        }

        OnExternalStateFinalized?.Invoke(actor, type);
    }

    private GlamourerApiEc InvokeOwnChange(Func<GlamourerApiEc> call)
    {
        invokingOwnChange = true;
        try { return call(); }
        finally
        {
            invokingOwnChange = false;
            lastOwnChangeUtc = DateTime.UtcNow;
        }
    }

    public sealed record DesignInfo(Guid Id, string DisplayName, string FullPath, uint Color);

    public sealed record DesignListResult(IReadOnlyList<DesignInfo> Designs, string? Error);

    // The list is one cheap IPC call; per-design metadata is fetched separately (FetchDesignMetadata)
    // so a refresh can spread those calls over several frames instead of stalling one.
    public DesignListResult FetchDesignList()
    {
        try
        {
            var data = getDesignListExtended.Invoke();
            var designs = new List<DesignInfo>(data.Count);
            foreach (var (guid, tuple) in data)
                designs.Add(new DesignInfo(guid, tuple.DisplayName, tuple.FullPath, tuple.DisplayColor));
            return new DesignListResult(designs, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to fetch Glamourer designs");
            return new DesignListResult(Array.Empty<DesignInfo>(), ex.Message);
        }
    }

    public CachedOutfit? FetchDesignMetadata(Guid id)
    {
        try
        {
            var jobject = getDesignJObject.Invoke(id);
            return jobject != null ? ParseOutfit(jobject) : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to cache metadata for design {Id}", id);
            return null;
        }
    }

    // quiet suppresses the success chat line and sound (e.g. automatic zone-change reapplies);
    // errors still print so background failures don't go unnoticed.
    public bool Apply(Guid id, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false)
    {
        try
        {
            var result = InvokeOwnChange(() => applyDesign.Invoke(id, 0, 0));
            if (result != GlamourerApiEc.Success)
            {
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": {result}");
                Plugin.Log.Warning("Failed to apply design {Name} ({Id}): {Result}", designName, id, result);
                return false;
            }

            if (!quiet)
            {
                SoundService.PlayApply();
                var suffix = layerNames is not { Count: > 0 }
                    ? ""
                    : layerNames.Count == 1
                        ? $" (+ layer \"{layerNames[0]}\")"
                        : $" (+ layers {string.Join(", ", layerNames.Select(n => $"\"{n}\""))})";
                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Applied \"{designName}\"{suffix}");
            }

            Plugin.Log.Info("Applied design {Name} ({Id})", designName, id);
            return true;
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Apply failed: {ex.Message}", "Failed to apply Glamourer design {Id}", id);
            return false;
        }
    }

    // Applies an additional design layer on top of an already-applied base, without the chat line or sound -
    // the base's apply already announced it.
    public void ApplyLayer(Guid id)
    {
        try
        {
            var result = InvokeOwnChange(() => applyDesign.Invoke(id, 0, 0));
            if (result != GlamourerApiEc.Success)
            {
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Layer apply failed: {result}");
                Plugin.Log.Warning("Failed to apply layer design {Id}: {Result}", id, result);
                return;
            }

            Plugin.Log.Info("Applied layer design ({Id})", id);
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Layer apply failed: {ex.Message}", "Failed to apply Glamourer layer design {Id}", id);
        }
    }

    public void OpenInGlamourer(Guid id, string designName)
    {
        try
        {
            openDesign.Invoke(id);
            Plugin.Log.Info("Opened design {Name} ({Id}) in Glamourer", designName, id);
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Open in Glamourer failed: {ex.Message}", "Failed to open Glamourer design {Id}", id);
        }
    }

    public void Revert()
    {
        try
        {
            var result = InvokeOwnChange(() => revertState.Invoke(0));
            SoundService.PlayRevert();
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Reverted appearance to game state: {result}");
            Plugin.Log.Info("Reverted appearance to game state: {Result}", result);
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Revert failed: {ex.Message}", "Failed to revert appearance");
        }
    }

    // Returns the local character's current full state (Glamourer's own JObject shape, the same one
    // GetDesignJObject uses for a saved design) - used as the base for a relay-apply from a
    // non-Glamourer source, so only the "Equipment" section needs touching rather than guessing what
    // else ApplyState's JObject is expected to contain.
    public (GlamourerApiEc Result, JObject? State) GetState(int objectIndex = 0)
        => getState.Invoke(objectIndex, 0);

    // Applies an arbitrary state JObject (typically GetState's own output, with just its "Equipment"
    // section overwritten) in one call - used to relay a non-Glamourer source's item/dye list through
    // Glamourer's own apply engine. ApplyState(JObject, int objectIndex, uint key, ApplyFlag flags) is
    // a real IPC (confirmed against Glamourer.Api.dll directly), separate from ApplyDesign which only
    // accepts a saved design's GUID. Scoping flags to ApplyFlag.Equipment means only Equipment is touched.
    public GlamourerApiEc ApplyEquipmentState(JObject state, int objectIndex = 0)
        => InvokeOwnChange(() => applyState.Invoke(state, objectIndex, 0, ApplyFlag.Equipment));

    // Same as ApplyEquipmentState but also applies the "Customize" section - used by SimpleGlamourSwitcherService,
    // the only relay source whose designs carry customize values as well as equipment.
    public GlamourerApiEc ApplyEquipmentAndCustomizeState(JObject state, int objectIndex = 0)
        => InvokeOwnChange(() => applyState.Invoke(state, objectIndex, 0, ApplyFlag.Equipment | ApplyFlag.Customization));

    private static CachedOutfit ParseOutfit(JObject j)
    {
        var customize = j["Customize"] as JObject;
        var name = GlamourerJsonSchema.ReadString(j["Name"]) ?? "(unnamed)";
        var description = GlamourerJsonSchema.ReadString(j["Description"]);
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var tags = j["Tags"] is JArray tagArray
            ? tagArray.Select(t => GlamourerJsonSchema.ReadString(t) ?? string.Empty)
                      .Where(t => !string.IsNullOrWhiteSpace(t))
                      .ToList()
            : new List<string>();

        var equipment = j["Equipment"] as JObject;

        return new CachedOutfit
        {
            Name = name,
            // Description/Tags are locally-owned (see Configuration.DesignMeta) - left at their defaults
            // here and overlaid by the caller. Glamourer's current value rides along for the sync actions.
            GlamourerDescription = description,
            GlamourerTags = tags,
            CreatedAt = ReadDateTimeOffset(j["CreationDate"]),
            LastEdit = ReadDateTimeOffset(j["LastEdit"]),
            Equipment = ParseEquipment(equipment),
            BonusItems = ParseBonusItems(j["Bonus"]),
            Customizations = GlamourerJsonSchema.ParseCustomizations(customize),
            CustomizeClan = (int)GlamourerJsonSchema.ReadUInt64(customize?["Clan"]?["Value"]),
            CustomizeGender = (int)GlamourerJsonSchema.ReadUInt64(customize?["Gender"]?["Value"]),
            CustomizeClanApplied = GlamourerJsonSchema.ReadBool(customize?["Clan"]?["Apply"]),
            CustomizeGenderApplied = GlamourerJsonSchema.ReadBool(customize?["Gender"]?["Apply"]),
            Links = ParseLinks(j["Links"]),
            HatVisible = GlamourerJsonSchema.ParseMetaToggle(equipment?["Hat"], "Show"),
            WeaponVisible = GlamourerJsonSchema.ParseMetaToggle(equipment?["Weapon"], "Show"),
            VisorToggled = GlamourerJsonSchema.ParseMetaToggle(equipment?["Visor"], "IsToggled"),
            ForcedRedraw = GlamourerJsonSchema.ReadBool(j["ForcedRedraw"]),
            ResetTemporarySettings = GlamourerJsonSchema.ReadBool(j["ResetTemporarySettings"]),
            Mods = ParseMods(j["Mods"]),
        };
    }

    private static List<CachedEquipmentSlot> ParseEquipment(JObject? equipment)
    {
        var result = new List<CachedEquipmentSlot>();
        if (equipment == null)
            return result;

        foreach (EquipmentSlot slot in Enum.GetValues<EquipmentSlot>())
        {
            if (equipment[slot.ToString()] is not JObject slotObj)
                continue;

            result.Add(new CachedEquipmentSlot
            {
                Slot = slot,
                ItemId = GlamourerJsonSchema.ReadUInt64(slotObj["ItemId"]),
                Stain = (byte)GlamourerJsonSchema.ReadUInt64(slotObj["Stain"]),
                Stain2 = (byte)GlamourerJsonSchema.ReadUInt64(slotObj["Stain2"]),
                Apply = GlamourerJsonSchema.ReadBool(slotObj["Apply"]),
                ApplyStain = GlamourerJsonSchema.ReadBool(slotObj["ApplyStain"]),
            });
        }
        return result;
    }

    private static List<CachedBonusItem> ParseBonusItems(JToken? token)
    {
        var result = new List<CachedBonusItem>();
        if (token is not JObject obj)
            return result;

        foreach (var prop in obj.Properties())
        {
            if (prop.Value is not JObject slotObj)
                continue;
            result.Add(new CachedBonusItem
            {
                Slot = prop.Name,
                ItemId = GlamourerJsonSchema.ReadUInt64(slotObj["BonusId"]),
                Apply = GlamourerJsonSchema.ReadBool(slotObj["Apply"]),
            });
        }
        return result;
    }

    private static List<CachedMod> ParseMods(JToken? token)
    {
        var result = new List<CachedMod>();
        if (token is not JArray array)
            return result;

        foreach (var entry in array.OfType<JObject>())
        {
            var state = GlamourerJsonSchema.ReadBool(entry["Remove"]) ? ModState.Remove
                      : GlamourerJsonSchema.ReadBool(entry["Inherit"]) ? ModState.Inherit
                      : GlamourerJsonSchema.ReadBool(entry["Enabled"]) ? ModState.Enabled
                      : ModState.Disabled;

            result.Add(new CachedMod
            {
                Name = GlamourerJsonSchema.ReadString(entry["Name"]) ?? string.Empty,
                Directory = GlamourerJsonSchema.ReadString(entry["Directory"]) ?? string.Empty,
                State = state,
                Priority = (int)GlamourerJsonSchema.ReadUInt64(entry["Priority"]),
                Settings = GlamourerJsonSchema.ParseModSettings(entry["Settings"]),
            });
        }
        return result;
    }

    private static List<CachedDesignLink> ParseLinks(JToken? token)
    {
        var result = new List<CachedDesignLink>();
        if (token is not JObject obj)
            return result;

        AddLinks(obj["Before"], isBefore: true, result);
        AddLinks(obj["After"], isBefore: false, result);
        return result;
    }

    private static void AddLinks(JToken? token, bool isBefore, List<CachedDesignLink> result)
    {
        if (token is not JArray array)
            return;

        foreach (var entry in array.OfType<JObject>())
        {
            if (!Guid.TryParse(GlamourerJsonSchema.ReadString(entry["Design"]), out var designId))
                continue;

            var conditions = entry["Conditions"] as JObject;
            result.Add(new CachedDesignLink
            {
                DesignId = designId,
                LinkType = (int)GlamourerJsonSchema.ReadUInt64(entry["Type"]),
                Gearset = conditions?["Gearset"] == null ? -1 : GlamourerJsonSchema.ReadInt32(conditions["Gearset"], -1),
                JobGroup = (int)GlamourerJsonSchema.ReadUInt64(conditions?["JobGroup"]),
                IsBefore = isBefore,
            });
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(JToken? token)
    {
        if (token is not JValue v || v.Value is null)
            return null;
        return v.Value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }
}
