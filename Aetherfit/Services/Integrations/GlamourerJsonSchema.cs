using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Integrations;

// Parsing helpers for Glamourer's own JSON shape - originally lived only in GlamourerService, but
// SimpleGlamourSwitcherService's outfit files use the exact same Customize {Value, Apply} shape (SGS's
// own data model is derived directly from Glamourer's), so these are shared rather than re-derived.
internal static class GlamourerJsonSchema
{
    // Listed in the order we want to show them. Toggle rows hold a flag (0 / non-zero); everything else
    // is a raw index. Race/Gender/Clan get turned into their in-game names - see FormatCustomizeValue.
    public static readonly (string Key, string Label, bool IsToggle)[] CustomizeDisplay =
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

    public static List<CachedCustomization> ParseCustomizations(JToken? token)
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

    public static bool? ParseMetaToggle(JToken? token, string valueKey)
    {
        if (token is not JObject obj)
            return null;
        if (!ReadBool(obj["Apply"]))
            return null;
        return ReadBool(obj[valueKey]);
    }

    public static Dictionary<string, string> ParseModSettings(JToken? token)
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

    // Used to build a gear-only design for Glamourer's AddDesign IPC (import from Glamaholic/Glamour
    // Plate) - Customize/Bonus Apply flags are zeroed since those providers have no face/body concept,
    // and carrying the importer's live customize into a saved design would be surprising.
    public static JObject BuildEquipmentOnlyDesign(JObject baseState, JObject equipment)
    {
        var design = (JObject)baseState.DeepClone();
        design["Equipment"] = equipment;
        ZeroApplyFlags(design["Customize"] as JObject);
        ZeroApplyFlags(design["Bonus"] as JObject);
        return design;
    }

    private static void ZeroApplyFlags(JObject? section)
    {
        if (section == null)
            return;
        foreach (var prop in section.Properties())
            if (prop.Value is JObject entry && entry["Apply"] != null)
                entry["Apply"] = false;
    }

    public static bool ReadBool(JToken? token)
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

    public static ulong ReadUInt64(JToken? token)
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

    // A signed reader for fields that legitimately go negative (e.g. a gearset index of -1 = "any").
    public static int ReadInt32(JToken? token, int fallback)
    {
        if (token is not JValue v || v.Value is null)
            return fallback;
        return v.Value switch
        {
            long l => (int)l,
            int i => i,
            uint u => (int)u,
            ulong u => (int)u,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }

    // Avoid JToken.Value<string>(): it goes through Convert.ChangeType, which throws
    // "Object must implement IConvertible" on token values like DateTimeOffset.
    public static string? ReadString(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
            return null;
        if (token is JValue v)
            return v.Value?.ToString();
        return token.ToString();
    }
}
