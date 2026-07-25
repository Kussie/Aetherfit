using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfit.Utils;
using Glamourer.Api.Enums;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using Penumbra.Api.Enums;

namespace Aetherfit.Services.Integrations;

// Reads Simple Glamour Switcher's outfits directly from its own files - SGS exposes no IPC of its own
// (confirmed from its source: github.com/Caraxi/SimpleGlamourSwitcher). Applies equipment/customize by
// relaying through GlamourerService.ApplyEquipmentAndCustomizeState, and separately activates a design's
// mods and Customize+ template toggles directly - SGS itself never routes either of those through
// Glamourer, so a state relay alone would never have activated them (see the plan for the full trace).
public sealed class SimpleGlamourSwitcherService : IDesignProvider
{
    private readonly GlamourerService glamourer;
    private readonly PenumbraService penumbra;
    private readonly CustomizePlusService customizePlus;

    // Outfit native id -> the character folder it lives under, populated by the most recent
    // FetchDesignList() - avoids re-scanning every character folder on every metadata/apply call.
    private readonly Dictionary<Guid, string> lastKnownOutfitCharacters = new();

    // Template id -> (profile, state before Aetherfit toggled it), so a later cleanup can restore it -
    // Customize+ template toggles have no atomic "temporary" concept the way Penumbra's do.
    private readonly Dictionary<Guid, (Guid Profile, bool PreviousState)> lastKnownTemplateStates = new();

    public SimpleGlamourSwitcherService(GlamourerService glamourer, PenumbraService penumbra, CustomizePlusService customizePlus)
    {
        this.glamourer = glamourer;
        this.penumbra = penumbra;
        this.customizePlus = customizePlus;
    }

    public DesignSource Source => DesignSource.SimpleGlamourSwitcher;
    public string DisplayName => "Simple Glamour Switcher";

    public DesignProviderCapabilities Capabilities =>
        DesignProviderCapabilities.Apply | DesignProviderCapabilities.Customizations | DesignProviderCapabilities.Mods;

    // The equipment slots SGS names directly (matching Aetherfit's own EquipmentSlot names exactly, no
    // translation table needed) - MainHand/OffHand come from Weapons.ClassWeapons separately, not here.
    private static readonly EquipmentSlot[] NamedGearSlots =
    {
        EquipmentSlot.Head, EquipmentSlot.Body, EquipmentSlot.Hands, EquipmentSlot.Legs, EquipmentSlot.Feet,
        EquipmentSlot.Ears, EquipmentSlot.Neck, EquipmentSlot.Wrists, EquipmentSlot.RFinger, EquipmentSlot.LFinger,
    };

    // Reserved key range for Aetherfit's own Penumbra temporary mod settings - distinct from SGS's own
    // 0x_53_47_53_0 base so the two never fight over the same (modDirectory, key) lock. One stable key
    // per equipment slot (including weapons) and one per curated customize field.
    private const int KeyBase = 0x_41_45_54_0;
    private static int SlotKey(EquipmentSlot slot) => -(KeyBase + (int)slot);
    private static int CustomizeKey(int customizeDisplayIndex) => -(KeyBase + 100 + customizeDisplayIndex);

    private static readonly Guid SharedCharacterId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static string ConfigDir =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.Parent!.FullName, "SimpleGlamourSwitcher");

    public PluginIntegrationInfo CheckIntegration()
    {
        var exposed = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "SimpleGlamourSwitcher");
        if (exposed == null)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotInstalled, null, null);
        if (!exposed.IsLoaded)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);

        return Directory.Exists(ConfigDir)
            ? new PluginIntegrationInfo(PluginIntegrationStatus.Ok, exposed.Version, null)
            : new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);
    }

    private sealed record CharacterInfo(string Name, Dictionary<Guid, (string Name, Guid Parent)> Folders);

    public ProviderDesignListResult FetchDesignList()
    {
        var charactersDir = Path.Combine(ConfigDir, "characters");
        if (!Directory.Exists(charactersDir))
            return new ProviderDesignListResult(Array.Empty<ProviderDesignInfo>(), null);

        var designs = new List<ProviderDesignInfo>();
        lastKnownOutfitCharacters.Clear();

        foreach (var characterDir in Directory.GetDirectories(charactersDir))
        {
            CharacterInfo? character;
            try
            {
                character = ReadCharacterInfo(characterDir);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to read SGS character folder {Dir}", characterDir);
                continue;
            }
            if (character == null)
                continue;

            var outfitsDir = Path.Combine(characterDir, "outfits");
            if (!Directory.Exists(outfitsDir))
                continue;

            foreach (var outfitFile in Directory.GetFiles(outfitsDir, "*.json"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(outfitFile), out var nativeId))
                    continue;

                try
                {
                    var outfit = JObject.Parse(File.ReadAllText(outfitFile));
                    var name = GlamourerJsonSchema.ReadString(outfit["Name"]);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var folderId = Guid.TryParse(GlamourerJsonSchema.ReadString(outfit["Folder"]), out var fid) ? fid : Guid.Empty;
                    var folderPath = ResolveFolderPath(folderId, character.Folders);
                    var fullPath = folderPath.Length == 0 ? $"{character.Name}/{name}" : $"{character.Name}/{folderPath}/{name}";

                    lastKnownOutfitCharacters[nativeId] = characterDir;
                    designs.Add(new ProviderDesignInfo(nativeId, name, fullPath, Color: 0, SourceSubGroup: character.Name));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to read SGS outfit {File}", outfitFile);
                }
            }
        }

        return new ProviderDesignListResult(designs, null);
    }

    private static CharacterInfo? ReadCharacterInfo(string characterDir)
    {
        var characterJsonPath = Path.Combine(characterDir, "character.json");
        if (!File.Exists(characterJsonPath))
            return null;

        var json = JObject.Parse(File.ReadAllText(characterJsonPath));
        var name = GlamourerJsonSchema.ReadString(json["Name"]) ?? "(unnamed)";

        var folders = new Dictionary<Guid, (string Name, Guid Parent)>();
        if (json["Folders"] is JObject foldersObj)
        {
            foreach (var prop in foldersObj.Properties())
            {
                if (!Guid.TryParse(prop.Name, out var folderId) || prop.Value is not JObject folderObj)
                    continue;

                var folderName = GlamourerJsonSchema.ReadString(folderObj["Name"]) ?? string.Empty;
                var parentId = Guid.TryParse(GlamourerJsonSchema.ReadString(folderObj["Parent"]), out var pid) ? pid : Guid.Empty;
                folders[folderId] = (folderName, parentId);
            }
        }

        return new CharacterInfo(name, folders);
    }

    // Walks the flat parent-pointer map up to the all-zero root, guarding against cycles in malformed data.
    private static string ResolveFolderPath(Guid folderId, Dictionary<Guid, (string Name, Guid Parent)> folders)
    {
        var segments = new List<string>();
        var visited = new HashSet<Guid>();
        var current = folderId;
        while (current != Guid.Empty && folders.TryGetValue(current, out var folder) && visited.Add(current))
        {
            segments.Add(folder.Name);
            current = folder.Parent;
        }
        segments.Reverse();
        return string.Join("/", segments);
    }

    public CachedOutfit? FetchDesignMetadata(Guid nativeId)
    {
        var outfitPath = FindOutfitFile(nativeId);
        if (outfitPath == null)
            return null;

        try
        {
            return ParseOutfit(JObject.Parse(File.ReadAllText(outfitPath)));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to cache metadata for SGS outfit {Id}", nativeId);
            return null;
        }
    }

    // In-session cache first (fast path, populated by the last FetchDesignList()), then a cold-start
    // fallback scan across every character folder - same two-tier shape GlamourPlateService uses.
    private string? FindOutfitFile(Guid nativeId)
    {
        if (lastKnownOutfitCharacters.TryGetValue(nativeId, out var characterDir))
        {
            var path = Path.Combine(characterDir, "outfits", $"{nativeId}.json");
            if (File.Exists(path))
                return path;
        }

        var charactersDir = Path.Combine(ConfigDir, "characters");
        if (!Directory.Exists(charactersDir))
            return null;

        foreach (var dir in Directory.GetDirectories(charactersDir))
        {
            var path = Path.Combine(dir, "outfits", $"{nativeId}.json");
            if (!File.Exists(path))
                continue;

            lastKnownOutfitCharacters[nativeId] = dir;
            return path;
        }

        return null;
    }

    private static CachedOutfit ParseOutfit(JObject j)
    {
        var name = GlamourerJsonSchema.ReadString(j["Name"]) ?? "(unnamed)";
        var equipment = j["Equipment"] as JObject;
        var appearance = j["Appearance"] as JObject;
        var weaponSet = ResolveCurrentWeaponSet(j["Weapons"] as JObject);

        var mods = new Dictionary<string, CachedMod>(StringComparer.OrdinalIgnoreCase);
        CollectModsFromOutfit(equipment, appearance, weaponSet, mods);

        var clan = appearance?["Clan"] as JObject;
        var gender = appearance?["Gender"] as JObject;

        return new CachedOutfit
        {
            Name = name,
            Equipment = ParseEquipment(equipment, weaponSet),
            BonusItems = ParseBonusItems(equipment),
            Customizations = GlamourerJsonSchema.ParseCustomizations(appearance),
            CustomizeClan = (int)GlamourerJsonSchema.ReadUInt64(clan?["Value"]),
            CustomizeGender = (int)GlamourerJsonSchema.ReadUInt64(gender?["Value"]),
            CustomizeClanApplied = GlamourerJsonSchema.ReadBool(clan?["Apply"]),
            CustomizeGenderApplied = GlamourerJsonSchema.ReadBool(gender?["Apply"]),
            HatVisible = GlamourerJsonSchema.ParseMetaToggle(equipment?["HatVisible"], "Toggle"),
            WeaponVisible = GlamourerJsonSchema.ParseMetaToggle(equipment?["WeaponVisible"], "Toggle"),
            VisorToggled = GlamourerJsonSchema.ParseMetaToggle(equipment?["VisorToggle"], "Toggle"),
            // SGS's relay-apply never forces a redraw or resets temporary settings - no equivalent flag exists.
            ForcedRedraw = false,
            ResetTemporarySettings = false,
            Mods = mods.Values.ToList(),
        };
    }

    private static List<CachedEquipmentSlot> ParseEquipment(JObject? equipment, JObject? weaponSet)
    {
        var result = new List<CachedEquipmentSlot>();
        if (equipment != null)
        {
            foreach (var slot in NamedGearSlots)
                if (ParseSlot(slot, equipment[slot.ToString()] as JObject) is { } parsed)
                    result.Add(parsed);
        }

        if (weaponSet != null)
        {
            if (ParseSlot(EquipmentSlot.MainHand, weaponSet["MainHand"] as JObject) is { } mh) result.Add(mh);
            if (ParseSlot(EquipmentSlot.OffHand, weaponSet["OffHand"] as JObject) is { } oh) result.Add(oh);
        }

        return result;
    }

    private static CachedEquipmentSlot? ParseSlot(EquipmentSlot slot, JObject? slotObj)
    {
        if (slotObj == null)
            return null;

        var stain = slotObj["Stain"] as JObject;
        return new CachedEquipmentSlot
        {
            Slot = slot,
            ItemId = GlamourerJsonSchema.ReadUInt64(slotObj["ItemId"]),
            Stain = (byte)GlamourerJsonSchema.ReadUInt64(stain?["Stain"]),
            Stain2 = (byte)GlamourerJsonSchema.ReadUInt64(stain?["Stain2"]),
            Apply = GlamourerJsonSchema.ReadBool(slotObj["Apply"]),
            // SGS never gates the stain separately (ApplicableStain.ApplyToCharacter is a no-op) - it
            // always rides along with the item, so this mirrors the slot's own Apply, not Stain.Apply.
            ApplyStain = GlamourerJsonSchema.ReadBool(slotObj["Apply"]),
        };
    }

    // Aetherfit's ResolveBonusItemName only recognizes "Glasses" (Glamourer's own bonus slot name) -
    // SGS's own JSON key for the same slot is "Face" (HumanSlot.Face), so it's translated here.
    private static List<CachedBonusItem> ParseBonusItems(JObject? equipment)
    {
        var result = new List<CachedBonusItem>();
        if (equipment?["Face"] is not JObject face)
            return result;

        result.Add(new CachedBonusItem
        {
            Slot = "Glasses",
            ItemId = GlamourerJsonSchema.ReadUInt64(face["BonusItemId"]),
            Apply = GlamourerJsonSchema.ReadBool(face["Apply"]),
        });
        return result;
    }

    // SGS's Weapons.ClassWeapons is keyed by the current job's base/parent class ClassJob.RowId (e.g. a
    // Paladin's weapon lives under Gladiator's row id) - confirmed from SGS's own OutfitWeapons.cs. A
    // best-effort live preview for display; Apply() re-resolves this fresh against whatever job is current then.
    private static JObject? ResolveCurrentWeaponSet(JObject? weapons)
    {
        if (weapons?["ClassWeapons"] is not JObject classWeapons || !Plugin.PlayerState.IsLoaded)
            return null;

        var baseClassId = ResolveBaseClassId(Plugin.PlayerState.ClassJob.RowId);
        return baseClassId == 0 ? null : classWeapons[baseClassId.ToString()] as JObject;
    }

    private static uint ResolveBaseClassId(uint classJobId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        return sheet != null && sheet.TryGetRow(classJobId, out var row) ? row.ClassJobParent.RowId : 0;
    }

    private static void CollectModsFromOutfit(JObject? equipment, JObject? appearance, JObject? weaponSet, Dictionary<string, CachedMod> mods)
    {
        if (equipment != null)
        {
            foreach (var slot in NamedGearSlots)
                CollectMods((equipment[slot.ToString()] as JObject)?["ModConfigs"], mods);
            CollectMods((equipment["Face"] as JObject)?["ModConfigs"], mods);
        }

        if (appearance != null)
        {
            foreach (var (key, _, _) in GlamourerJsonSchema.CustomizeDisplay)
                CollectMods((appearance[key] as JObject)?["ModConfigs"], mods);
        }

        if (weaponSet != null)
        {
            CollectMods((weaponSet["MainHand"] as JObject)?["ModConfigs"], mods);
            CollectMods((weaponSet["OffHand"] as JObject)?["ModConfigs"], mods);
        }
    }

    // Deduplicated by directory - the same mod commonly ends up attached to more than one slot with
    // identical settings (e.g. a dress attached to both Body and Legs), which would otherwise show twice.
    private static void CollectMods(JToken? modConfigsToken, Dictionary<string, CachedMod> mods)
    {
        if (modConfigsToken is not JArray array)
            return;

        foreach (var entry in array.OfType<JObject>())
        {
            var directory = GlamourerJsonSchema.ReadString(entry["ModDirectory"]);
            if (string.IsNullOrWhiteSpace(directory) || mods.ContainsKey(directory))
                continue;

            mods[directory] = new CachedMod
            {
                // SGS has no separate mod display name - DesignAttributionService.ModDisplayName already
                // falls back to Directory when Name is empty.
                Name = string.Empty,
                Directory = directory,
                State = GlamourerJsonSchema.ReadBool(entry["Enabled"]) ? ModState.Enabled : ModState.Disabled,
                Priority = (int)GlamourerJsonSchema.ReadUInt64(entry["Priority"]),
                Settings = GlamourerJsonSchema.ParseModSettings(entry["Settings"]),
            };
        }
    }

    public bool Apply(Guid nativeId, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false)
    {
        var outfitPath = FindOutfitFile(nativeId);
        if (outfitPath == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": outfit not found in Simple Glamour Switcher.");
            return false;
        }

        JObject outfit;
        try
        {
            outfit = JObject.Parse(File.ReadAllText(outfitPath));
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Failed to apply \"{designName}\": couldn't read the outfit file.",
                "Failed to read SGS outfit {Id}", nativeId);
            return false;
        }

        var equipment = outfit["Equipment"] as JObject;
        var appearance = outfit["Appearance"] as JObject;
        var weaponSet = ResolveCurrentWeaponSet(outfit["Weapons"] as JObject);

        // Mods and Customize+ templates first - so by the time Glamourer's own state push (below) triggers
        // its automatic redraw, Penumbra/Customize+ already resolve correctly and no follow-up redraw is
        // needed. SGS itself does this in the opposite order, but only because it explicitly stages both
        // across several game ticks with a manual redraw at the end - Aetherfit's relay is one synchronous
        // call, so the safer order for that shape is mods-then-state, not state-then-mods.
        ApplyMods(equipment, weaponSet, appearance);
        ApplyCustomizePlusTemplates(equipment, appearance, weaponSet);

        var (stateResult, state) = glamourer.GetState();
        if (stateResult != GlamourerApiEc.Success || state == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": couldn't read current state ({stateResult}).");
            return false;
        }

        state["Equipment"] = BuildEquipmentJObject(equipment, weaponSet);
        state["Bonus"] = BuildBonusJObject(equipment);
        state["Customize"] = BuildCustomizeJObject(appearance);

        var applyResult = glamourer.ApplyEquipmentAndCustomizeState(state);
        if (applyResult != GlamourerApiEc.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{designName}\": {applyResult}");
            Plugin.Log.Warning("Failed to apply SGS outfit {Name} ({Id}): {Result}", designName, nativeId, applyResult);
            return false;
        }

        if (!quiet)
        {
            SoundService.PlayApply();
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Applied \"{designName}\"");
        }
        Plugin.Log.Info("Applied SGS outfit {Name} ({Id})", designName, nativeId);
        return true;
    }

    private static JObject BuildEquipmentJObject(JObject? equipment, JObject? weaponSet)
    {
        var result = new JObject();
        foreach (var slot in NamedGearSlots)
            AddSlotToEquipment(result, slot, equipment?[slot.ToString()] as JObject);

        AddSlotToEquipment(result, EquipmentSlot.MainHand, weaponSet?["MainHand"] as JObject);
        AddSlotToEquipment(result, EquipmentSlot.OffHand, weaponSet?["OffHand"] as JObject);
        return result;
    }

    // A slot with no Apply (or missing entirely) is passed through as Apply:false rather than defaulting
    // to "nothing equipped" - SGS itself leaves an unapplied slot completely untouched on the character
    // (ApplicableEquipment.ApplyToCharacter's `if (!Apply) return;`), unlike Glamaholic's plates which are
    // always full 12-slot captures.
    private static void AddSlotToEquipment(JObject equipment, EquipmentSlot slot, JObject? sgsSlot)
    {
        var apply = GlamourerJsonSchema.ReadBool(sgsSlot?["Apply"]);
        var stain = sgsSlot?["Stain"] as JObject;
        equipment[slot.ToString()] = new JObject
        {
            ["ItemId"] = (long)GlamourerJsonSchema.ReadUInt64(sgsSlot?["ItemId"]),
            ["Stain"] = (int)GlamourerJsonSchema.ReadUInt64(stain?["Stain"]),
            ["Stain2"] = (int)GlamourerJsonSchema.ReadUInt64(stain?["Stain2"]),
            ["Apply"] = apply,
            ["ApplyStain"] = apply,
        };
    }

    private static JObject BuildBonusJObject(JObject? equipment)
    {
        var face = equipment?["Face"] as JObject;
        return new JObject
        {
            ["Glasses"] = new JObject
            {
                ["BonusId"] = (long)GlamourerJsonSchema.ReadUInt64(face?["BonusItemId"]),
                ["Apply"] = GlamourerJsonSchema.ReadBool(face?["Apply"]),
            },
        };
    }

    private static JObject BuildCustomizeJObject(JObject? appearance)
    {
        var result = new JObject();
        if (appearance == null)
            return result;

        foreach (var (key, _, _) in GlamourerJsonSchema.CustomizeDisplay)
        {
            if (appearance[key] is not JObject entry)
                continue;

            result[key] = new JObject
            {
                ["Value"] = (long)GlamourerJsonSchema.ReadUInt64(entry["Value"]),
                ["Apply"] = GlamourerJsonSchema.ReadBool(entry["Apply"]),
            };
        }
        return result;
    }

    private void ApplyMods(JObject? equipment, JObject? weaponSet, JObject? appearance)
    {
        foreach (var slot in NamedGearSlots)
            ApplySlotMods(SlotKey(slot), (equipment?[slot.ToString()] as JObject)?["ModConfigs"]);
        ApplySlotMods(SlotKey(EquipmentSlot.MainHand), (weaponSet?["MainHand"] as JObject)?["ModConfigs"]);
        ApplySlotMods(SlotKey(EquipmentSlot.OffHand), (weaponSet?["OffHand"] as JObject)?["ModConfigs"]);

        for (var i = 0; i < GlamourerJsonSchema.CustomizeDisplay.Length; i++)
            ApplySlotMods(CustomizeKey(i), (appearance?[GlamourerJsonSchema.CustomizeDisplay[i].Key] as JObject)?["ModConfigs"]);
    }

    // Always clears this key first, then re-sets only what the current design needs - simpler than
    // diffing against whatever a previous SGS-sourced apply left behind, and Penumbra's RemoveAll is a
    // no-op if nothing was set for that key anyway.
    private void ApplySlotMods(int key, JToken? modConfigsToken)
    {
        penumbra.RemoveAllTemporaryModSettingsPlayer(key);
        if (modConfigsToken is not JArray array)
            return;

        const string source = "Aetherfit (via Simple Glamour Switcher)";
        foreach (var entry in array.OfType<JObject>())
        {
            var directory = GlamourerJsonSchema.ReadString(entry["ModDirectory"]);
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var enabled = GlamourerJsonSchema.ReadBool(entry["Enabled"]);
            var priority = (int)GlamourerJsonSchema.ReadUInt64(entry["Priority"]);
            var settings = ParseModSettingsForApply(entry["Settings"]);

            var result = penumbra.SetTemporaryModSettingsPlayer(directory, enabled, priority, settings, source, key);
            if (result != PenumbraApiEc.Success)
                Plugin.Log.Warning("Failed to set temporary mod settings for {Mod} ({Key}): {Result}", directory, key, result);
        }
    }

    // Unlike ParseModSettings (which joins values into one display string), Penumbra's temp-settings IPC
    // needs the raw per-group option list intact.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseModSettingsForApply(JToken? token)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        if (token is not JObject obj)
            return result;

        foreach (var prop in obj.Properties())
            if (prop.Value is JArray arr)
                result[prop.Name] = arr.Select(v => v.ToString()).ToList();
        return result;
    }

    // Clears every key Aetherfit's SGS relay could have used - called by DesignApplyService whenever a
    // non-SGS design is applied afterward, or on revert, so mod overrides don't bleed into a design that
    // never asked for them.
    public void ClearAllTemporaryModSettings()
    {
        foreach (var slot in NamedGearSlots.Append(EquipmentSlot.MainHand).Append(EquipmentSlot.OffHand))
            penumbra.RemoveAllTemporaryModSettingsPlayer(SlotKey(slot));
        for (var i = 0; i < GlamourerJsonSchema.CustomizeDisplay.Length; i++)
            penumbra.RemoveAllTemporaryModSettingsPlayer(CustomizeKey(i));
    }

    // Never assigns/switches the active Customize+ profile itself - only toggles named templates within
    // whichever profile is already active for the local player, matching SGS's own
    // CustomizePlus.ApplyTemplateConfig exactly. Silently does nothing if no profile is active.
    private void ApplyCustomizePlusTemplates(JObject? equipment, JObject? appearance, JObject? weaponSet)
    {
        var profile = customizePlus.GetActiveProfileOnLocalPlayer();
        if (profile is not { } profileId)
            return;

        var available = customizePlus.GetTemplates(profileId).ToDictionary(t => t.UniqueId);

        if (equipment != null)
        {
            foreach (var slot in NamedGearSlots)
                ApplyTemplateConfigs(profileId, available, (equipment[slot.ToString()] as JObject)?["CustomizePlusTemplateConfigs"]);
            ApplyTemplateConfigs(profileId, available, (equipment["Face"] as JObject)?["CustomizePlusTemplateConfigs"]);
        }

        if (appearance != null)
        {
            foreach (var (key, _, _) in GlamourerJsonSchema.CustomizeDisplay)
                ApplyTemplateConfigs(profileId, available, (appearance[key] as JObject)?["CustomizePlusTemplateConfigs"]);
        }

        if (weaponSet != null)
        {
            ApplyTemplateConfigs(profileId, available, (weaponSet["MainHand"] as JObject)?["CustomizePlusTemplateConfigs"]);
            ApplyTemplateConfigs(profileId, available, (weaponSet["OffHand"] as JObject)?["CustomizePlusTemplateConfigs"]);
        }
    }

    private void ApplyTemplateConfigs(Guid profile, Dictionary<Guid, CustomizePlusService.TemplateStatus> available, JToken? templateConfigsToken)
    {
        if (templateConfigsToken is not JArray array)
            return;

        foreach (var entry in array.OfType<JObject>())
        {
            if (!Guid.TryParse(GlamourerJsonSchema.ReadString(entry["TemplateId"]), out var templateId))
                continue;
            if (!available.TryGetValue(templateId, out var template))
                continue;

            var enable = GlamourerJsonSchema.ReadBool(entry["Enable"]);
            if (template.IsEnabled == enable)
                continue;

            lastKnownTemplateStates.TryAdd(templateId, (profile, template.IsEnabled));
            customizePlus.SetTemplateEnabled(profile, templateId, enable);
        }
    }

    // Restores every Customize+ template Aetherfit has toggled back to its pre-apply state - called by
    // DesignApplyService whenever a non-SGS design is applied afterward, or on revert.
    public void RevertCustomizePlusTemplates()
    {
        if (lastKnownTemplateStates.Count == 0)
            return;

        foreach (var (templateId, (profile, previousState)) in lastKnownTemplateStates)
            customizePlus.SetTemplateEnabled(profile, templateId, previousState);
        lastKnownTemplateStates.Clear();
    }

    // No layer/native-UI/revert concept of its own, matching Glamaholic/Glamour Plate.
    public void ApplyLayer(Guid nativeId) { }
    public void OpenInNativeUi(Guid nativeId, string designName) { }
    public void Revert() { }

    public event Action<nint, DesignFinalizationType>? OnExternalStateFinalized { add { } remove { } }
    public event Action<nint, DesignFinalizationType>? OnAnyStateFinalized { add { } remove { } }

    public void Dispose() { }
}
