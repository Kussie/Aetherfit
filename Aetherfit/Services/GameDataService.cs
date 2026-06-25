using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Aetherfit.Services;

public enum JobRole
{
    Tank,
    Healer,
    Melee,
    PhysicalRanged,
    MagicalRanged,
    Crafter,
    Gatherer,
}

public readonly record struct JobInfo(uint RowId, string Name, JobRole Role);

public sealed class GameDataService
{
    private readonly ExcelSheet<Item>? itemSheet;
    private readonly ExcelSheet<Stain>? stainSheet;
    private readonly ExcelSheet<Glasses>? glassesSheet;
    private readonly ExcelSheet<ClassJob>? classJobSheet;

    private readonly ConcurrentDictionary<ulong, string> itemNameCache = new();
    private readonly ConcurrentDictionary<byte, (string Name, uint Color)> stainCache = new();
    private readonly ConcurrentDictionary<ulong, string> glassesNameCache = new();
    private readonly ConcurrentDictionary<uint, string> jobNameCache = new();

    // ClassJob RowId -> role, for the jobs we surface as associations. The job list is fixed, so a static table
    // is more reliable than inferring from sheet columns, and it lets us exclude base classes (Gladiator, etc.).
    private static readonly IReadOnlyDictionary<uint, JobRole> JobRoles = new Dictionary<uint, JobRole>
    {
        // Tanks
        [19] = JobRole.Tank, [21] = JobRole.Tank, [32] = JobRole.Tank, [37] = JobRole.Tank,
        // Healers
        [24] = JobRole.Healer, [28] = JobRole.Healer, [33] = JobRole.Healer, [40] = JobRole.Healer,
        // Melee DPS
        [20] = JobRole.Melee, [22] = JobRole.Melee, [30] = JobRole.Melee,
        [34] = JobRole.Melee, [39] = JobRole.Melee, [41] = JobRole.Melee, [43] = JobRole.Melee,
        // Physical Ranged DPS
        [23] = JobRole.PhysicalRanged, [31] = JobRole.PhysicalRanged, [38] = JobRole.PhysicalRanged,
        // Magical Ranged DPS
        [25] = JobRole.MagicalRanged, [27] = JobRole.MagicalRanged, [35] = JobRole.MagicalRanged,
        [36] = JobRole.MagicalRanged, [42] = JobRole.MagicalRanged,
        // Crafters (Disciples of the Hand)
        [8] = JobRole.Crafter, [9] = JobRole.Crafter, [10] = JobRole.Crafter, [11] = JobRole.Crafter,
        [12] = JobRole.Crafter, [13] = JobRole.Crafter, [14] = JobRole.Crafter, [15] = JobRole.Crafter,
        // Gatherers (Disciples of the Land)
        [16] = JobRole.Gatherer, [17] = JobRole.Gatherer, [18] = JobRole.Gatherer,
    };

    private List<JobInfo>? selectableJobs;

    public GameDataService()
    {
        itemSheet = TryLoadSheet<Item>();
        stainSheet = TryLoadSheet<Stain>();
        glassesSheet = TryLoadSheet<Glasses>();
        classJobSheet = TryLoadSheet<ClassJob>();
    }

    private static ExcelSheet<T>? TryLoadSheet<T>() where T : struct, IExcelRow<T>
    {
        try
        {
            return Plugin.DataManager.GetExcelSheet<T>();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load {Sheet} excel sheet", typeof(T).Name);
            return null;
        }
    }

    // Shared by the Resolve* methods below: hand back the cached value, or look it up once and remember it.
    private static TValue Resolve<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, TKey key, Func<TKey, TValue> lookup)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var value = lookup(key);
        cache[key] = value;
        return value;
    }

    public static string RoleLabel(JobRole role) => role switch
    {
        JobRole.Tank => "Tank",
        JobRole.Healer => "Healer",
        JobRole.Melee => "Melee DPS",
        JobRole.PhysicalRanged => "Physical Ranged DPS",
        JobRole.MagicalRanged => "Magical Ranged DPS",
        JobRole.Crafter => "Crafter (Disciples of the Hand)",
        JobRole.Gatherer => "Gatherer (Disciples of the Land)",
        _ => "Other",
    };

    public IReadOnlyList<JobInfo> GetSelectableJobs()
        => selectableJobs ??= JobRoles
            .Select(kv => new JobInfo(kv.Key, ResolveJobName(kv.Key), kv.Value))
            .OrderBy(j => j.Role)
            .ThenBy(j => j.RowId)
            .ToList();

    public string ResolveJobName(uint rowId) => Resolve(jobNameCache, rowId, LookupJobName);

    // Display names for jobs that may not yet exist in the live ClassJob sheet (e.g. upcoming limited jobs).
    private static readonly IReadOnlyDictionary<uint, string> JobNameFallbacks = new Dictionary<uint, string>
    {
        [43] = "Beastmaster",
    };

    private string LookupJobName(uint rowId)
    {
        // ClassJob.Name is stored lowercase (e.g. "paladin"); title-case it for display.
        if (classJobSheet != null && classJobSheet.TryGetRow(rowId, out var row))
        {
            var text = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
                return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text);
        }
        return JobNameFallbacks.TryGetValue(rowId, out var fallback) ? fallback : $"Job {rowId}";
    }

    public bool IsKnownJob(uint rowId) => JobRoles.ContainsKey(rowId);

    public IDalamudTextureWrap? GetJobIcon(uint rowId)
    {
        // Framed gold job icons live at 62100 + ClassJob RowId.
        try { return Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(62100 + rowId)).GetWrapOrEmpty(); }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load job icon for ClassJob {RowId}", rowId);
            return null;
        }
    }

    public string ResolveItemName(ulong itemId)
    {
        if (itemId == 0)
            return "Nothing";

        return Resolve(itemNameCache, itemId, LookupItemName);
    }

    private string LookupItemName(ulong itemId)
    {
        // Glamourer encodes custom weapon models with bits >32 set, and uses random ItemIds for "nothing" that don't map to real Item rows. Either way: if we can't resolve the row to a name, treat it as Nothing.
        if (itemSheet != null && itemId <= uint.MaxValue
            && itemSheet.TryGetRow((uint)itemId, out var row))
        {
            var text = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return "Nothing";
    }

    public (string Name, uint Color) ResolveStain(byte stainId)
    {
        if (stainId == 0)
            return ("None", 0u);

        return Resolve(stainCache, stainId, LookupStain);
    }

    private (string Name, uint Color) LookupStain(byte stainId)
    {
        if (stainSheet == null)
            return ($"Dye #{stainId}", 0u);

        if (!stainSheet.TryGetRow(stainId, out var row))
            return ($"Dye #{stainId}", 0u);

        var name = row.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name))
            name = $"Dye #{stainId}";
        return (name, row.Color);
    }

    public string ResolveBonusItemName(string slotKey, ulong bonusId)
    {
        if (bonusId == 0)
            return "Nothing";

        return Resolve(glassesNameCache, bonusId, id => LookupBonusItemName(slotKey, id));
    }

    private string LookupBonusItemName(string slotKey, ulong bonusId)
    {
        // Glamourer's only published bonus slot is "Glasses"; the BonusId is a row in
        // FFXIV's Glasses excel sheet, not the regular Item sheet.
        if (slotKey == "Glasses" && glassesSheet != null && bonusId <= uint.MaxValue
            && glassesSheet.TryGetRow((uint)bonusId, out var row))
        {
            var text = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return "Nothing";
    }

    // --- Character-creation colour palette (chara/xls/charamake/human.cmp) ---------------------
    //
    // Colour customizations (skin, hair, eyes, ...) only store a palette index, not an actual colour, so
    // to draw a swatch we have to read the game's human.cmp colour map. Skin and hair also depend on the
    // character's clan (subrace) and gender. The offsets below match the CmpData layout from
    // Penumbra.GameData / Glamourer's ColorParameters; every colour is 4 RGBA bytes.
    // See https://github.com/Ottermandias/Penumbra.GameData (Files/CmpData.cs).

    private const int Rgba = 4;
    private const int FullColors = 256 * Rgba;     // FullColors block: 256 colours
    private const int TonedColors = 128 * Rgba;    // TonedColors block: 128 colours
    private const int HairColorsBlock = 256 * 8;   // HairColors block: 256 * (Main + UnusedSheen)
    private const int ColorParametersSize = 9216;  // size of one ColorParameters block

    private const int ParametersBase = 0;
    private const int InterfaceBase = ColorParametersSize;        // second ColorParameters block
    private const int RacesBase = ColorParametersSize * 2;        // start of the 32 race/gender blocks
    private const int GenderClanSize = FullColors + HairColorsBlock + FullColors + FullColors; // 5120

    // Field offsets within a ColorParameters block.
    private const int EyesOffset = 0;
    private const int HairHighlightsOffset = FullColors;                       // 1024
    private const int LipsDarkOffset = HairHighlightsOffset + FullColors;      // 2048
    private const int FacePaintDarkOffset = LipsDarkOffset + TonedColors;      // 2560
    private const int FeaturesOffset = FacePaintDarkOffset + TonedColors;      // 3072
    private const int LipsLightOffset = FeaturesOffset + FullColors;           // 4096
    private const int FacePaintLightOffset = LipsLightOffset + TonedColors;    // 4608

    // Field offsets within a GenderClanColorParameters block (Skin, Hair, SkinInterface, HairInterface).
    private const int SkinInterfaceOffset = FullColors + HairColorsBlock;          // 3072
    private const int HairInterfaceOffset = SkinInterfaceOffset + FullColors;      // 4096

    private byte[]? cmpData;
    private bool cmpLoadAttempted;

    /// <summary>
    /// Turns a colour customization (skin, hair, eyes, lips, ...) into a packed 0xRRGGBB colour for the
    /// preview swatch. Returns false if it isn't a colour parameter, the value is out of range, or the
    /// cmp file couldn't be read.
    /// </summary>
    public bool TryResolveCustomizeColor(string key, int value, int clan, int gender, out uint rgb)
    {
        rgb = 0;
        var data = LoadCmp();
        if (data == null)
            return false;

        if (!TryGetColorOffset(key, value, clan, gender, out var offset))
            return false;
        if (offset < 0 || offset + Rgba > data.Length)
            return false;

        // The cmp stores RGBA bytes; pack them into 0xRRGGBB so we can reuse the dye swatch helper
        // (same format as Stain.Color).
        rgb = (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]);
        return true;
    }

    private static bool TryGetColorOffset(string key, int value, int clan, int gender, out int offset)
    {
        offset = -1;
        switch (key)
        {
            case "EyeColorLeft":
            case "EyeColorRight":
                return TrySingle(InterfaceBase + EyesOffset, value, out offset);
            case "HighlightsColor":
                return TrySingle(InterfaceBase + HairHighlightsOffset, value, out offset);
            case "TattooColor":
                return TrySingle(InterfaceBase + FeaturesOffset, value, out offset);
            case "LipColor":
                return TryDouble(ParametersBase + LipsDarkOffset, ParametersBase + LipsLightOffset, value, out offset);
            case "FacePaintColor":
                return TryDouble(ParametersBase + FacePaintDarkOffset, ParametersBase + FacePaintLightOffset, value, out offset);
            case "SkinColor":
                return TryRace(clan, gender, SkinInterfaceOffset, value, out offset);
            case "HairColor":
                return TryRace(clan, gender, HairInterfaceOffset, value, out offset);
            default:
                return false;
        }
    }

    // The plain 192-colour tables that everyone shares (eyes, highlights, features).
    private static bool TrySingle(int tableBase, int value, out int offset)
    {
        offset = -1;
        if ((uint)value >= 192)
            return false;
        offset = tableBase + value * Rgba;
        return true;
    }

    // Lips and face paint are split into 96 "dark" colours (values 0-95) and 96 "light" ones (128-223).
    private static bool TryDouble(int darkBase, int lightBase, int value, out int offset)
    {
        offset = -1;
        if (value is >= 0 and < 96)
            offset = darkBase + value * Rgba;
        else if (value is >= 128 and < 224)
            offset = lightBase + (value - 128) * Rgba;
        else
            return false;
        return true;
    }

    // Race/gender-dependent skin & hair palettes.
    private static bool TryRace(int clan, int gender, int typeOffset, int value, out int offset)
    {
        offset = -1;
        if ((uint)value >= 192 || clan is < 1 or > 16)
            return false;
        var index = (clan - 1) * 2 + (gender == 1 ? 1 : 0); // gender: 0 male, 1 female
        if ((uint)index >= 32)
            return false;
        offset = RacesBase + index * GenderClanSize + typeOffset + value * Rgba;
        return true;
    }

    private byte[]? LoadCmp()
    {
        if (cmpLoadAttempted)
            return cmpData;

        cmpLoadAttempted = true;
        try
        {
            var file = Plugin.DataManager.GetFile("chara/xls/charamake/human.cmp");
            cmpData = file?.Data;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load human.cmp; customization colour previews disabled");
            cmpData = null;
        }
        return cmpData;
    }
}
