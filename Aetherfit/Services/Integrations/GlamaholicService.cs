using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfit.Utils;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Integrations;

// Reads Glamaholic's plates directly from its own config JSON - Glamaholic exposes no IPC of its
// own (confirmed from its source: it only consumes Glamourer's IPC, never exposes one). Applies by
// relaying through GlamourerService.ApplyEquipmentState rather than touching Glamaholic at all.
public sealed class GlamaholicService : IDesignProvider
{
    private readonly GlamourerService glamourer;

    public GlamaholicService(GlamourerService glamourer) => this.glamourer = glamourer;

    public DesignSource Source => DesignSource.Glamaholic;
    public string DisplayName => "Glamaholic";
    public DesignProviderCapabilities Capabilities => DesignProviderCapabilities.Apply;

    // Glamaholic's PlateSlot names match Aetherfit's own EquipmentSlot names except for the rings.
    private static readonly (string GlamaholicKey, EquipmentSlot AetherfitSlot)[] SlotMap =
    {
        ("MainHand", EquipmentSlot.MainHand),
        ("OffHand", EquipmentSlot.OffHand),
        ("Head", EquipmentSlot.Head),
        ("Body", EquipmentSlot.Body),
        ("Hands", EquipmentSlot.Hands),
        ("Legs", EquipmentSlot.Legs),
        ("Feet", EquipmentSlot.Feet),
        ("Ears", EquipmentSlot.Ears),
        ("Neck", EquipmentSlot.Neck),
        ("Wrists", EquipmentSlot.Wrists),
        ("RightRing", EquipmentSlot.RFinger),
        ("LeftRing", EquipmentSlot.LFinger),
    };

    // Glamaholic's own IPluginConfiguration file - a flat sibling of Aetherfit's own main config,
    // not inside Aetherfit's ConfigDirectory subfolder (confirmed against a real installed copy).
    private static string ConfigPath =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.Parent!.FullName, "Glamaholic.json");

    public PluginIntegrationInfo CheckIntegration()
    {
        var exposed = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Glamaholic");
        if (exposed == null)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotInstalled, null, null);
        if (!exposed.IsLoaded)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);

        // No IPC/version to check against - installed, loaded, and a readable config file is as far
        // as this can verify.
        return File.Exists(ConfigPath)
            ? new PluginIntegrationInfo(PluginIntegrationStatus.Ok, exposed.Version, null)
            : new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);
    }

    public ProviderDesignListResult FetchDesignList()
    {
        JObject root;
        try
        {
            if (!File.Exists(ConfigPath))
                return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), null);
            root = JObject.Parse(File.ReadAllText(ConfigPath));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read Glamaholic's config");
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), ex.Message);
        }

        var designs = new List<ProviderDesignInfo>();
        if (root["Plates"] is JArray plates)
            WalkNodes(plates, "", designs);
        return new ProviderDesignListResult(designs, null);
    }

    public CachedOutfit? FetchDesignMetadata(Guid nativeId)
    {
        var plate = FindPlate(nativeId);
        if (plate == null)
            return null;

        var name = plate["Name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var tags = plate["Tags"] is JArray tagArray
            ? tagArray.Select(t => t.ToString()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            : new List<string>();

        return new CachedOutfit
        {
            Name = name,
            // Glamaholic has no description field, only tags - MainWindow seeds the locally-owned
            // Tags from GlamourerTags on first sight the same way it does for a real Glamourer design.
            GlamourerTags = tags,
            Equipment = ParseEquipment(plate["Items"] as JObject),
        };
    }

    public bool Apply(Guid nativeId, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false)
    {
        var plate = FindPlate(nativeId);
        if (plate == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": plate not found in Glamaholic.");
            return false;
        }

        var (stateResult, state) = glamourer.GetState();
        if (stateResult != GlamourerApiEc.Success || state == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": couldn't read current state ({stateResult}).");
            return false;
        }

        var items = plate["Items"] as JObject;
        var equipment = new JObject();
        foreach (var (glamaholicKey, slot) in SlotMap)
        {
            var item = items?[glamaholicKey] as JObject;
            equipment[slot.ToString()] = new JObject
            {
                ["ItemId"] = item?["ItemId"]?.Value<long>() ?? 0,
                ["Stain"] = item?["Stain1"]?.Value<int>() ?? 0,
                ["Stain2"] = item?["Stain2"]?.Value<int>() ?? 0,
                ["Apply"] = true,
                ["ApplyStain"] = true,
            };
        }
        state["Equipment"] = equipment;

        var applyResult = glamourer.ApplyEquipmentState(state);
        if (applyResult != GlamourerApiEc.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": {applyResult}");
            Plugin.Log.Warning("Failed to apply Glamaholic design {Name} ({Id}): {Result}", designName, nativeId, applyResult);
            return false;
        }

        if (!quiet)
        {
            SoundService.PlayApply();
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Applied \"{designName}\"");
        }
        Plugin.Log.Info("Applied Glamaholic design {Name} ({Id})", designName, nativeId);
        return true;
    }

    // No layer/native-UI/revert concept of its own - Capabilities has no Apply-adjacent flags for
    // these, so the UI never calls them, but IDesignProvider still requires an implementation.
    public void ApplyLayer(Guid nativeId) { }
    public void OpenInNativeUi(Guid nativeId, string designName) { }
    public void Revert() { }

    public event Action<nint, DesignFinalizationType>? OnExternalStateFinalized { add { } remove { } }
    public event Action<nint, DesignFinalizationType>? OnAnyStateFinalized { add { } remove { } }

    public void Dispose() { }

    private static JObject? FindPlate(Guid id)
    {
        JObject root;
        try
        {
            if (!File.Exists(ConfigPath))
                return null;
            root = JObject.Parse(File.ReadAllText(ConfigPath));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read Glamaholic's config for design {Id}", id);
            return null;
        }

        if (root["Plates"] is not JArray plates)
            return null;

        var node = FindPlateNode(plates, id);
        return node?["Plate"] as JObject;
    }

    private static JObject? FindPlateNode(JArray nodes, Guid id)
    {
        foreach (var node in nodes.OfType<JObject>())
        {
            var nodeType = node["NodeType"]?.ToString();
            if (nodeType == "Plate" && Guid.TryParse(node["Id"]?.ToString(), out var nodeId) && nodeId == id)
                return node;
            if (nodeType == "Folder" && node["Children"] is JArray children
                && FindPlateNode(children, id) is { } found)
                return found;
        }
        return null;
    }

    // Glamaholic's plates are a tree of Folder/Plate nodes (NodeType discriminator). Unknown NodeType
    // or malformed entries are skipped, not thrown - this schema has already changed twice upstream
    // and Aetherfit doesn't control it.
    private static void WalkNodes(JArray nodes, string pathPrefix, List<ProviderDesignInfo> designs)
    {
        foreach (var node in nodes.OfType<JObject>())
        {
            switch (node["NodeType"]?.ToString())
            {
                case "Plate":
                    if (TryParsePlateNode(node, pathPrefix) is { } info)
                        designs.Add(info);
                    break;
                case "Folder":
                    var name = node["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name) || node["Children"] is not JArray children)
                        break;
                    WalkNodes(children, pathPrefix.Length == 0 ? name : $"{pathPrefix}/{name}", designs);
                    break;
            }
        }
    }

    private static ProviderDesignInfo? TryParsePlateNode(JObject node, string pathPrefix)
    {
        if (!Guid.TryParse(node["Id"]?.ToString(), out var id))
            return null;
        if (node["Plate"] is not JObject plate)
            return null;

        var name = plate["Name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var fullPath = pathPrefix.Length == 0 ? name : $"{pathPrefix}/{name}";
        return new ProviderDesignInfo(id, name, fullPath, Color: 0);
    }

    private static List<CachedEquipmentSlot> ParseEquipment(JObject? items)
    {
        var result = new List<CachedEquipmentSlot>();
        if (items == null)
            return result;

        foreach (var (glamaholicKey, slot) in SlotMap)
        {
            if (items[glamaholicKey] is not JObject item)
                continue;

            result.Add(new CachedEquipmentSlot
            {
                Slot = slot,
                ItemId = (ulong)(item["ItemId"]?.Value<long>() ?? 0),
                Stain = (byte)(item["Stain1"]?.Value<int>() ?? 0),
                Stain2 = (byte)(item["Stain2"]?.Value<int>() ?? 0),
                Apply = true,
                ApplyStain = true,
            });
        }

        return result;
    }
}
