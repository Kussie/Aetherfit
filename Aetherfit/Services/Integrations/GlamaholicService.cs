using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfit.Utils;
using Glamourer.Api.Enums;
using Newtonsoft.Json;
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
        if (PluginIntegrationCheck.CheckInstalledAndLoaded("Glamaholic", out var exposed) is { } early)
            return early;

        // No IPC/version to check against - installed, loaded, and a readable config file is as far
        // as this can verify.
        return File.Exists(ConfigPath)
            ? new PluginIntegrationInfo(PluginIntegrationStatus.Ok, exposed!.Version, null)
            : new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed!.Version, null);
    }

    public ProviderDesignListResult FetchDesignList()
    {
        var root = TryReadConfig(out var error);
        if (root == null)
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), error);

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

        var items = plate["Items"] as JObject;
        return glamourer.RelayApply(nativeId, designName, quiet, "Glamaholic", state =>
        {
            state["Equipment"] = BuildEquipmentJObject(items);
        }, s => glamourer.ApplyEquipmentState(s));
    }

    private static JObject BuildEquipmentJObject(JObject? items, bool skipEmptySlots = false)
    {
        var equipment = new JObject();
        foreach (var (glamaholicKey, slot) in SlotMap)
        {
            var item = items?[glamaholicKey] as JObject;
            var itemId = item?["ItemId"]?.Value<long>() ?? 0;
            var apply = !skipEmptySlots || itemId != 0;
            equipment[slot.ToString()] = new JObject
            {
                ["ItemId"] = itemId,
                ["Stain"] = item?["Stain1"]?.Value<int>() ?? 0,
                ["Stain2"] = item?["Stain2"]?.Value<int>() ?? 0,
                ["Apply"] = apply,
                ["ApplyStain"] = apply,
            };
        }
        return equipment;
    }

    // Feeds the "Import into Glamourer" flow (MainWindow.EditMode.cs) - the plate's own name (used to
    // prefill the import dialog) alongside the same equipment shape Apply already builds, minus any
    // slot the plate leaves empty.
    public (string Name, JObject Equipment)? BuildImportPayload(Guid nativeId)
    {
        var plate = FindPlate(nativeId);
        if (plate == null)
            return null;

        var name = plate["Name"]?.ToString() ?? string.Empty;
        return (name, BuildEquipmentJObject(plate["Items"] as JObject, skipEmptySlots: true));
    }

    public sealed record PushTagsResult(bool Success, string? Error);

    // Same direct-file-edit approach as GlamourerDesignFileService - Glamaholic exposes no IPC to
    // write its own Tags. Only the target plate's Tags array is touched; everything else in the
    // config (other plates, folders, favourites) is round-tripped as-is.
    public PushTagsResult PushTagsToGlamaholic(Guid nativeId, IReadOnlyList<string> tags)
    {
        if (!File.Exists(ConfigPath))
            return new PushTagsResult(false, "Glamaholic config file not found.");

        try
        {
            var text = File.ReadAllText(ConfigPath);

            JObject root;
            using (var stringReader = new StringReader(text))
            using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                root = JObject.Load(jsonReader);

            if (root["Plates"] is not JArray plates)
                return new PushTagsResult(false, "Glamaholic config has no plates.");

            var node = FindPlateNode(plates, nativeId);
            if (node?.Node["Plate"] is not JObject plate)
                return new PushTagsResult(false, "Plate not found in Glamaholic — was it deleted?");

            plate["Tags"] = new JArray(tags);

            var usesCrlf = text.Contains("\r\n");
            string output;
            using (var stringWriter = new StringWriter { NewLine = usesCrlf ? "\r\n" : "\n" })
            {
                using (var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented, IndentChar = ' ', Indentation = 2 })
                    root.WriteTo(jsonWriter);
                output = stringWriter.ToString();
            }

            var tempPath = ConfigPath + ".aetherfit-tmp";
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, ConfigPath, overwrite: true);

            // Drop the cached parse so the next list/metadata read picks up this write instead of
            // trusting a copy that's now stale relative to what's on disk.
            cachedRoot = null;
            return new PushTagsResult(true, null);
        }
        catch (IOException ex)
        {
            Plugin.Log.Warning(ex, "Glamaholic config locked while pushing tags for {Id}", nativeId);
            return new PushTagsResult(false, "Couldn't write Glamaholic's config file — it may be open or in use. Try again.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to push tags to Glamaholic plate {Id}", nativeId);
            return new PushTagsResult(false, $"Failed to update Glamaholic's config file: {ex.Message}");
        }
    }

    public sealed record DeletePlateResult(bool Success, string? Error);

    // Same skeleton as PushTagsToGlamaholic, removing the plate node from its containing array
    // instead of editing a field. Glamaholic keeps its own plate list in memory and has no reason to
    // notice this external edit mid-session - its own UI (and Aetherfit's cache, until this class's
    // cachedRoot is invalidated below) won't reflect the removal until Glamaholic reloads its config,
    // i.e. a relog or plugin reload. The caller surfaces that caveat to the user up front.
    public DeletePlateResult DeletePlate(Guid nativeId)
    {
        if (!File.Exists(ConfigPath))
            return new DeletePlateResult(false, "Glamaholic config file not found.");

        try
        {
            var text = File.ReadAllText(ConfigPath);

            JObject root;
            using (var stringReader = new StringReader(text))
            using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                root = JObject.Load(jsonReader);

            if (root["Plates"] is not JArray plates)
                return new DeletePlateResult(false, "Glamaholic config has no plates.");

            var found = FindPlateNode(plates, nativeId);
            if (found == null)
                return new DeletePlateResult(false, "Plate not found in Glamaholic — was it already removed?");

            found.Value.Container.Remove(found.Value.Node);

            var usesCrlf = text.Contains("\r\n");
            string output;
            using (var stringWriter = new StringWriter { NewLine = usesCrlf ? "\r\n" : "\n" })
            {
                using (var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented, IndentChar = ' ', Indentation = 2 })
                    root.WriteTo(jsonWriter);
                output = stringWriter.ToString();
            }

            var tempPath = ConfigPath + ".aetherfit-tmp";
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, ConfigPath, overwrite: true);

            cachedRoot = null;
            return new DeletePlateResult(true, null);
        }
        catch (IOException ex)
        {
            Plugin.Log.Warning(ex, "Glamaholic config locked while deleting plate {Id}", nativeId);
            return new DeletePlateResult(false, "Couldn't write Glamaholic's config file — it may be open or in use. Try again.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete Glamaholic plate {Id}", nativeId);
            return new DeletePlateResult(false, $"Failed to update Glamaholic's config file: {ex.Message}");
        }
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

    private JObject? FindPlate(Guid id)
    {
        var root = TryReadConfig(out _);
        if (root?["Plates"] is not JArray plates)
            return null;

        var node = FindPlateNode(plates, id);
        return node?.Node["Plate"] as JObject;
    }

    private JObject? cachedRoot;
    private DateTime cachedRootWriteTimeUtc;

    // Reads and parses Glamaholic's config JSON - shared by FetchDesignList and FindPlate, the latter
    // called once per design during the metadata drain, so caching by the file's own write time avoids
    // re-parsing the whole (possibly large) plate tree once per design while still picking up live edits.
    // Null means missing or unreadable; error is only set (and only meaningful to the caller) on an
    // actual read/parse failure, not a simply-missing file.
    private JObject? TryReadConfig(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(ConfigPath))
                return null;

            var writeTime = File.GetLastWriteTimeUtc(ConfigPath);
            if (cachedRoot != null && writeTime == cachedRootWriteTimeUtc)
                return cachedRoot;

            cachedRoot = JObject.Parse(File.ReadAllText(ConfigPath));
            cachedRootWriteTimeUtc = writeTime;
            return cachedRoot;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read Glamaholic's config");
            error = ex.Message;
            return null;
        }
    }

    // Returns the matched node alongside the array it lives in - PushTagsToGlamaholic only needs the
    // node, but DeletePlate needs the container too, to remove the node from it.
    private static (JArray Container, JObject Node)? FindPlateNode(JArray nodes, Guid id)
    {
        foreach (var node in nodes.OfType<JObject>())
        {
            var nodeType = node["NodeType"]?.ToString();
            if (nodeType == "Plate" && Guid.TryParse(node["Id"]?.ToString(), out var nodeId) && nodeId == id)
                return (nodes, node);
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
