using System;
using System.Collections.Generic;
using System.Linq;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services;

public sealed class GlamourerService
{
    private readonly GetDesignListExtended getDesignListExtended;
    private readonly GetDesignJObject getDesignJObject;
    private readonly ApplyDesign applyDesign;
    private readonly RevertState revertState;
    private readonly OpenDesign openDesign;

    public GlamourerService()
    {
        getDesignListExtended = new GetDesignListExtended(Plugin.PluginInterface);
        getDesignJObject = new GetDesignJObject(Plugin.PluginInterface);
        applyDesign = new ApplyDesign(Plugin.PluginInterface);
        revertState = new RevertState(Plugin.PluginInterface);
        openDesign = new OpenDesign(Plugin.PluginInterface);
    }

    public sealed record DesignInfo(Guid Id, string DisplayName, string FullPath, uint Color);

    public sealed record FetchResult(
        IReadOnlyList<DesignInfo> Designs,
        Dictionary<Guid, CachedOutfit> Metadata,
        string? Error);

    public FetchResult FetchDesigns()
    {
        try
        {
            var data = getDesignListExtended.Invoke();
            var designs = new List<DesignInfo>(data.Count);
            var metadata = new Dictionary<Guid, CachedOutfit>(data.Count);

            foreach (var (guid, tuple) in data)
            {
                designs.Add(new DesignInfo(guid, tuple.DisplayName, tuple.FullPath, tuple.DisplayColor));

                try
                {
                    var jobject = getDesignJObject.Invoke(guid);
                    if (jobject != null)
                        metadata[guid] = ParseOutfit(jobject);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to cache metadata for design {Id}", guid);
                }
            }

            return new FetchResult(designs, metadata, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to fetch Glamourer designs");
            return new FetchResult(Array.Empty<DesignInfo>(), new Dictionary<Guid, CachedOutfit>(), ex.Message);
        }
    }

    public void Apply(Guid id, string designName)
    {
        try
        {
            var result = applyDesign.Invoke(id, 0, 0);
            SoundService.PlayApply();
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Applied \"{designName}\": {result}");
            Plugin.Log.Info("Applied design {Name} ({Id}): {Result}", designName, id, result);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Apply failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to apply Glamourer design {Id}", id);
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
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Open in Glamourer failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to open Glamourer design {Id}", id);
        }
    }

    public void Revert()
    {
        try
        {
            var result = revertState.Invoke(0);
            SoundService.PlayRevert();
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Reverted appearance to game state: {result}");
            Plugin.Log.Info("Reverted appearance to game state: {Result}", result);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Revert failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to revert appearance");
        }
    }

    private static CachedOutfit ParseOutfit(JObject j)
    {
        var customize = j["Customize"] as JObject;
        var name = ReadString(j["Name"]) ?? "(unnamed)";
        var description = ReadString(j["Description"]);
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var tags = j["Tags"] is JArray tagArray
            ? tagArray.Select(t => ReadString(t) ?? string.Empty)
                      .Where(t => !string.IsNullOrWhiteSpace(t))
                      .ToList()
            : new List<string>();

        var equipment = j["Equipment"] as JObject;

        return new CachedOutfit
        {
            Name = name,
            Description = description,
            Tags = tags,
            CreatedAt = ReadDateTimeOffset(j["CreationDate"]),
            LastEdit = ReadDateTimeOffset(j["LastEdit"]),
            Equipment = ParseEquipment(equipment),
            BonusItems = ParseBonusItems(j["Bonus"]),
            Customizations = ParseCustomizations(customize),
            CustomizeClan = (int)ReadUInt64(customize?["Clan"]?["Value"]),
            CustomizeGender = (int)ReadUInt64(customize?["Gender"]?["Value"]),
            HatVisible = ParseMetaToggle(equipment?["Hat"], "Show"),
            WeaponVisible = ParseMetaToggle(equipment?["Weapon"], "Show"),
            VisorToggled = ParseMetaToggle(equipment?["Visor"], "IsToggled"),
            ForcedRedraw = ReadBool(j["ForcedRedraw"]),
            ResetTemporarySettings = ReadBool(j["ResetTemporarySettings"]),
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
                ItemId = ReadUInt64(slotObj["ItemId"]),
                Stain = (byte)ReadUInt64(slotObj["Stain"]),
                Stain2 = (byte)ReadUInt64(slotObj["Stain2"]),
                Apply = ReadBool(slotObj["Apply"]),
                ApplyStain = ReadBool(slotObj["ApplyStain"]),
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
                ItemId = ReadUInt64(slotObj["BonusId"]),
                Apply = ReadBool(slotObj["Apply"]),
            });
        }
        return result;
    }

    // Listed in the order we want to show them. Toggle rows hold a flag (0 / non-zero); everything else
    // is a raw index. Race/Gender/Clan get turned into their in-game names - see FormatCustomizeValue.
    private static readonly (string Key, string Label, bool IsToggle)[] CustomizeDisplay =
    {
        ("Race",              "Race",                false),
        ("Gender",            "Gender",              false),
        ("BodyType",          "Body Type",           false),
        ("Clan",              "Clan",                false),
        ("Height",            "Height",              false),
        ("Face",              "Face",                false),
        ("Hairstyle",         "Hairstyle",           false),
        ("Highlights",        "Highlights",          true),
        ("HairColor",         "Hair Color",          false),
        ("HighlightsColor",   "Highlights Color",    false),
        ("SkinColor",         "Skin Color",          false),
        ("Eyebrows",          "Eyebrows",            false),
        ("EyeShape",          "Eye Shape",           false),
        ("SmallIris",         "Small Iris",          true),
        ("EyeColorRight",     "Right Eye Color",     false),
        ("EyeColorLeft",      "Left Eye Color",      false),
        ("Nose",              "Nose",                false),
        ("Jaw",               "Jaw",                 false),
        ("Mouth",             "Mouth",               false),
        ("Lipstick",          "Lipstick",            true),
        ("LipColor",          "Lip Color",           false),
        ("FacialFeature1",    "Facial Feature 1",    true),
        ("FacialFeature2",    "Facial Feature 2",    true),
        ("FacialFeature3",    "Facial Feature 3",    true),
        ("FacialFeature4",    "Facial Feature 4",    true),
        ("FacialFeature5",    "Facial Feature 5",    true),
        ("FacialFeature6",    "Facial Feature 6",    true),
        ("FacialFeature7",    "Facial Feature 7",    true),
        ("LegacyTattoo",      "Legacy Tattoo",       true),
        ("TattooColor",       "Tattoo Color",        false),
        ("FacePaint",         "Face Paint",          false),
        ("FacePaintReversed", "Face Paint Reversed", true),
        ("FacePaintColor",    "Face Paint Color",    false),
        ("MuscleMass",        "Muscle Tone",         false),
        ("TailShape",         "Tail / Ear Shape",    false),
        ("BustSize",          "Bust Size",           false),
        ("Wetness",           "Wetness",             true),
    };

    // The fixed enum customizations have proper in-game names, keyed by their raw customize value.
    private static readonly IReadOnlyDictionary<int, string> RaceNames = new Dictionary<int, string>
    {
        [1] = "Hyur", [2] = "Elezen", [3] = "Lalafell", [4] = "Miqo'te",
        [5] = "Roegadyn", [6] = "Au Ra", [7] = "Hrothgar", [8] = "Viera",
    };

    private static readonly IReadOnlyDictionary<int, string> ClanNames = new Dictionary<int, string>
    {
        [1] = "Midlander", [2] = "Highlander", [3] = "Wildwood", [4] = "Duskwight",
        [5] = "Plainsfolk", [6] = "Dunesfolk", [7] = "Seeker of the Sun", [8] = "Keeper of the Moon",
        [9] = "Sea Wolf", [10] = "Hellsguard", [11] = "Raen", [12] = "Xaela",
        [13] = "Helions", [14] = "The Lost", [15] = "Rava", [16] = "Veena",
    };

    private static string FormatCustomizeValue(string key, int value) => key switch
    {
        "Race"   => RaceNames.GetValueOrDefault(value, value.ToString()),
        "Clan"   => ClanNames.GetValueOrDefault(value, value.ToString()),
        "Gender" => value switch { 0 => "Masculine", 1 => "Feminine", _ => value.ToString() },
        _        => value.ToString(),
    };

    private static List<CachedCustomization> ParseCustomizations(JToken? token)
    {
        var result = new List<CachedCustomization>();
        if (token is not JObject obj)
            return result;

        foreach (var (key, label, isToggle) in CustomizeDisplay)
        {
            if (obj[key] is not JObject entry)
                continue;
            if (!ReadBool(entry["Apply"]))
                continue;

            // Glamourer always writes BodyType out as applied with value 1, even when the toggle is off
            // in its UI, and there's no separate flag to tell the two apart. So treat the default (1) as
            // "not really set" - otherwise it'd show up on every single design. A non-default still shows.
            if (key == "BodyType" && ReadUInt64(entry["Value"]) == 1)
                continue;

            var rawValue = (int)ReadUInt64(entry["Value"]);

            string value;
            if (isToggle)
            {
                // Toggles come through either as a bool (Wetness) or a flag byte (0 / 128).
                var on = ReadBool(entry["Value"]) || rawValue != 0;
                value = on ? "On" : "Off";
            }
            else
            {
                value = FormatCustomizeValue(key, rawValue);
            }

            result.Add(new CachedCustomization
            {
                Key = key,
                Label = label,
                Value = value,
                RawValue = rawValue,
                IsToggle = isToggle,
            });
        }

        return result;
    }

    private static bool? ParseMetaToggle(JToken? token, string valueKey)
    {
        if (token is not JObject obj)
            return null;
        if (!ReadBool(obj["Apply"]))
            return null;
        return ReadBool(obj[valueKey]);
    }

    private static List<CachedMod> ParseMods(JToken? token)
    {
        var result = new List<CachedMod>();
        if (token is not JArray array)
            return result;

        foreach (var entry in array.OfType<JObject>())
        {
            var state = ReadBool(entry["Remove"]) ? ModState.Remove
                      : ReadBool(entry["Inherit"]) ? ModState.Inherit
                      : ReadBool(entry["Enabled"]) ? ModState.Enabled
                      : ModState.Disabled;

            result.Add(new CachedMod
            {
                Name = ReadString(entry["Name"]) ?? string.Empty,
                Directory = ReadString(entry["Directory"]) ?? string.Empty,
                State = state,
                Priority = (int)ReadUInt64(entry["Priority"]),
                Settings = ParseModSettings(entry["Settings"]),
            });
        }
        return result;
    }

    private static Dictionary<string, string> ParseModSettings(JToken? token)
    {
        var result = new Dictionary<string, string>();
        if (token is not JObject obj)
            return result;

        foreach (var prop in obj.Properties())
        {
            result[prop.Name] = prop.Value switch
            {
                JArray arr => string.Join(", ", arr.Select(v => v.ToString())),
                JValue v => v.Value?.ToString() ?? string.Empty,
                _ => prop.Value.ToString(),
            };
        }
        return result;
    }

    private static bool ReadBool(JToken? token)
    {
        if (token is not JValue v || v.Value is null)
            return false;
        return v.Value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => false,
        };
    }

    private static ulong ReadUInt64(JToken? token)
    {
        if (token is not JValue v || v.Value is null)
            return 0;
        return v.Value switch
        {
            ulong u => u,
            long l => l < 0 ? 0 : (ulong)l,
            int i => i < 0 ? 0 : (ulong)i,
            uint u => u,
            string s when ulong.TryParse(s, out var parsed) => parsed,
            _ => 0,
        };
    }

    // Avoid JToken.Value<string>(): it goes through Convert.ChangeType, which throws
    // "Object must implement IConvertible" on token values like DateTimeOffset.
    private static string? ReadString(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
            return null;
        if (token is JValue v)
            return v.Value?.ToString();
        return token.ToString();
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
