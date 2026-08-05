using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Game.Player;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using ImSharp;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Penumbra.GameData.DataContainers;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Files;
using CustomItemId = Penumbra.GameData.Structs.CustomItemId;
using Job = Penumbra.GameData.Structs.Job;
using JobId = Penumbra.GameData.Structs.JobId;
using Race = Penumbra.GameData.Enums.Race;

namespace Aetherfit.Services.Game;

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
    // What the Resolve*ItemName methods return when there's no item in a slot or the id won't resolve.
    public const string NothingItemName = "Nothing";

    private readonly ExcelSheet<Item>? itemSheet;
    private readonly ExcelSheet<Stain>? stainSheet;
    private readonly ExcelSheet<Glasses>? glassesSheet;
    private readonly ExcelSheet<ClassJobCategory>? classJobCategorySheet;
    private readonly ExcelSheet<EquipRaceCategory>? equipRaceCategorySheet;
    private readonly DictJob dictJob;

    private readonly ConcurrentDictionary<ulong, string> itemNameCache = new();
    private readonly ConcurrentDictionary<byte, (string Name, uint Color)> stainCache = new();
    private readonly ConcurrentDictionary<ulong, string> glassesNameCache = new();

    // ClassJob RowId, for the jobs we surface as associations. The job list is fixed, so a curated set is more
    // reliable than taking every DictJob entry: that would include pre-job-stone base classes (Gladiator, etc.)
    // alongside their advanced-job counterparts, doubling up entries with the same derived role.
    private static readonly IReadOnlySet<uint> SelectableJobIds = new HashSet<uint>
    {
        // Tanks
        19, 21, 32, 37,
        // Healers
        24, 28, 33, 40,
        // Melee DPS
        20, 22, 30, 34, 39, 41, 43,
        // Physical Ranged DPS
        23, 31, 38,
        // Magical Ranged DPS
        25, 27, 35, 36, 42,
        // Crafters (Disciples of the Hand)
        8, 9, 10, 11, 12, 13, 14, 15,
        // Gatherers (Disciples of the Land)
        16, 17, 18,
    };

    private List<JobInfo>? selectableJobs;

    public GameDataService()
    {
        itemSheet = TryLoadSheet<Item>();
        stainSheet = TryLoadSheet<Stain>();
        glassesSheet = TryLoadSheet<Glasses>();
        classJobCategorySheet = TryLoadSheet<ClassJobCategory>();
        equipRaceCategorySheet = TryLoadSheet<EquipRaceCategory>();
        dictJob = new DictJob(Plugin.DataManager);
    }

    // The name of a Glamourer design-link job condition (a ClassJobCategory, e.g. "All Classes" or "Healer").
    // Returns null for "any job" (RowId 0) or when the category has no name / can't be resolved.
    public string? ResolveJobGroupName(int jobGroupId)
    {
        if (jobGroupId <= 0 || classJobCategorySheet == null)
            return null;
        if (!classJobCategorySheet.TryGetRow((uint)jobGroupId, out var row))
            return null;
        var name = row.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public bool? IsItemWearableBy(ulong itemId, uint raceRowId, bool isFemale)
    {
        if (itemId == 0 || itemSheet == null || equipRaceCategorySheet == null)
            return null;
        if (!itemSheet.TryGetRow((uint)Math.Min(itemId, uint.MaxValue), out var item))
            return null;
        if (!equipRaceCategorySheet.TryGetRow(item.EquipRestriction.RowId, out var category))
            return null;

        return GetRaceFlag(category, (Race)raceRowId) && (isFemale ? category.Female : category.Male);
    }

    public (uint RaceRowId, bool IsFemale)? ResolveEffectiveRaceGender(CachedOutfit details)
    {
        uint? race = details.CustomizeClanApplied ? ClanToRace(details.CustomizeClan) : CurrentPlayerRace();
        bool? female = details.CustomizeGenderApplied ? details.CustomizeGender == 1 : CurrentPlayerIsFemale();
        return race is { } r && female is { } f ? (r, f) : null;
    }

    public bool? IsItemWearableByCurrentCharacter(ulong itemId, CachedOutfit details)
        => ResolveEffectiveRaceGender(details) is { } rg ? IsItemWearableBy(itemId, rg.RaceRowId, rg.IsFemale) : null;

    // True only when at least one equipped item is a *known* mismatch - an unresolvable item never counts
    // (see IsItemWearableBy's null case), and a design with no equipment/no resolvable race is never flagged.
    public bool DesignHasAnyIncompatibleItems(CachedOutfit details)
    {
        if (ResolveEffectiveRaceGender(details) is not { } rg)
            return false;
        return details.Equipment.Any(e => IsItemWearableBy(e.ItemId, rg.RaceRowId, rg.IsFemale) == false);
    }

    // "Au Ra, Hrothgar" (and a Male/Female-only suffix if one gender is excluded) - for the incompatibility tooltip.
    public string DescribeWearableRaces(ulong itemId)
    {
        if (itemSheet == null || equipRaceCategorySheet == null
            || !itemSheet.TryGetRow((uint)Math.Min(itemId, uint.MaxValue), out var item)
            || !equipRaceCategorySheet.TryGetRow(item.EquipRestriction.RowId, out var category))
            return "unknown races";

        var names = Enum.GetValues<Race>()
            .Where(r => r != Race.Unknown && GetRaceFlag(category, r))
            .Select(r => r.ToName())
            .ToList();
        var raceText = names.Count == 0 ? "no races" : string.Join(", ", names);

        return (category.Male, category.Female) switch
        {
            (true, false) => $"{raceText} (Male only)",
            (false, true) => $"{raceText} (Female only)",
            _ => raceText,
        };
    }

    private static bool GetRaceFlag(EquipRaceCategory category, Race race) => race switch
    {
        Race.Hyur => category.Hyur,
        Race.Elezen => category.Elezen,
        Race.Lalafell => category.Lalafell,
        Race.Miqote => category.Miqote,
        Race.Roegadyn => category.Roegadyn,
        Race.AuRa => category.AuRa,
        Race.Hrothgar => category.Hrothgar,
        Race.Viera => category.Viera,
        _ => true,
    };

    private static uint ClanToRace(int clan) => (uint)((SubRace)clan).ToRace(); // Glamourer clan 1-16 -> Race 1-8
    private static uint? CurrentPlayerRace() => Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.Race.RowId : null;
    private static bool? CurrentPlayerIsFemale() => Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.Sex == Sex.Female : null;

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
        => selectableJobs ??= SelectableJobIds
            .Select(id => dictJob.TryGetValue((JobId)id, out var job) ? (JobInfo?)ToJobInfo(id, job) : null)
            .Where(j => j != null)
            .Select(j => j!.Value)
            .OrderBy(j => j.Role)
            .ThenBy(j => j.RowId)
            .ToList();

    public string ResolveJobName(uint rowId)
        => dictJob.TryGetValue((JobId)rowId, out var job) ? job.Name.ToString() : $"Job {rowId}";

    private static JobInfo ToJobInfo(uint rowId, Job job) => new(rowId, job.Name.ToString(), ToAetherfitRole(job.Role));

    private static JobRole ToAetherfitRole(Job.JobRole role) => role switch
    {
        Job.JobRole.Tank => JobRole.Tank,
        Job.JobRole.Healer => JobRole.Healer,
        Job.JobRole.Melee => JobRole.Melee,
        Job.JobRole.RangedPhysical => JobRole.PhysicalRanged,
        Job.JobRole.RangedMagical => JobRole.MagicalRanged,
        Job.JobRole.Crafter => JobRole.Crafter,
        Job.JobRole.Gatherer => JobRole.Gatherer,
        _ => JobRole.Melee, // unreachable for the curated SelectableJobIds set
    };

    public bool IsKnownJob(uint rowId) => SelectableJobIds.Contains(rowId);

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
            return NothingItemName;

        return Resolve(itemNameCache, itemId, LookupItemName);
    }

    // Mirrors LookupItemName's existence logic without producing display text - used by the Health
    // Report's broken-item check, which needs to tell "intentionally empty" apart from "references
    // an item that no longer resolves," something ResolveItemName's return value can't do alone
    // (both cases return NothingItemName).
    public bool ItemExists(ulong itemId)
    {
        if (itemId == 0)
            return true;

        if (itemSheet != null && itemId <= uint.MaxValue && itemSheet.TryGetRow((uint)itemId, out var row)
            && !string.IsNullOrWhiteSpace(row.Name.ExtractText()))
            return true;

        // A hand-built custom weapon (Glamourer's advanced editor) is intentionally not a catalog item.
        if (((CustomItemId)itemId) is { IsCustom: true, IsBonusItem: false })
            return true;

        // Glamourer's own "nothing"/"smallclothes" sentinels (ItemManager.NothingId/SmallclothesId in
        // its source: uint.MaxValue minus a small per-slot/per-weapon-type offset) - nowhere near any
        // real item id, but still <= uint.MaxValue, so they reach and correctly fail the sheet lookup
        // above without being custom-flagged either. Without this, every slot a design leaves
        // intentionally empty reads as broken.
        return itemId >= uint.MaxValue - 1000;
    }

    public bool BonusItemExists(string slotKey, ulong bonusId)
    {
        if (bonusId == 0)
            return true;

        if (ParseBonusSlot(slotKey) == BonusItemFlag.Glasses && glassesSheet != null && bonusId <= uint.MaxValue
            && glassesSheet.TryGetRow((uint)bonusId, out var row) && !string.IsNullOrWhiteSpace(row.Name.ExtractText()))
            return true;

        // Glamourer's "nothing" sentinel for bonus slots (EquipItem.BonusItemNothing in its source) is
        // a custom- AND bonus-flagged id, not a real Glasses sheet row.
        return ((CustomItemId)bonusId) is { IsCustom: true, IsBonusItem: true };
    }

    private string LookupItemName(ulong itemId)
    {
        // Glamourer encodes custom weapon models with bits >32 set, and uses random ItemIds for "nothing" that don't map to real Item rows.
        if (itemSheet != null && itemId <= uint.MaxValue
            && itemSheet.TryGetRow((uint)itemId, out var row))
        {
            var text = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        // A hand-built custom weapon (Glamourer's advanced editor) isn't "nothing" - it just has no catalog
        // name to show. Distinguish it so the slot doesn't read as empty.
        if (((CustomItemId)itemId) is { IsCustom: true, IsBonusItem: false })
            return "Custom Item";

        return NothingItemName;
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
            return NothingItemName;

        return Resolve(glassesNameCache, bonusId, id => LookupBonusItemName(slotKey, id));
    }

    private static BonusItemFlag ParseBonusSlot(string slotKey) => slotKey switch
    {
        "Glasses" => BonusItemFlag.Glasses,
        _ => BonusItemFlag.Unknown,
    };

    private string LookupBonusItemName(string slotKey, ulong bonusId)
    {
        // Glamourer's only published bonus slot is "Glasses"; the BonusId is a row in
        // FFXIV's Glasses excel sheet, not the regular Item sheet.
        if (ParseBonusSlot(slotKey) == BonusItemFlag.Glasses && glassesSheet != null && bonusId <= uint.MaxValue
            && glassesSheet.TryGetRow((uint)bonusId, out var row))
        {
            var text = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return NothingItemName;
    }

    // --- Character-creation colour palette (chara/xls/charamake/human.cmp) ---------------------
    //
    // Colour customizations (skin, hair, eyes, ...) only store a palette index, not an actual colour, so
    // to draw a swatch we have to read the game's human.cmp colour map. Skin and hair also depend on the
    // character's clan (subrace) and gender. Penumbra.GameData's CmpData struct maps directly onto the raw
    // file bytes, so this reads through that instead of hand-rolled offsets. See
    // https://github.com/Ottermandias/Penumbra.GameData (Files/CmpData.cs).

    private byte[]? cmpBytes;
    private bool cmpLoadAttempted;

    /// <summary>
    /// Turns a colour customization (skin, hair, eyes, lips, ...) into a packed 0xRRGGBB colour for the
    /// preview swatch. Returns false if it isn't a colour parameter, the value is out of range, or the
    /// cmp file couldn't be read.
    /// </summary>
    public bool TryResolveCustomizeColor(string key, int value, int clan, int gender, out uint rgb)
    {
        rgb = 0;
        if (LoadCmp() is not { } bytes || bytes.Length < Unsafe.SizeOf<CmpData>())
            return false;

        ref readonly var data = ref MemoryMarshal.AsRef<CmpData>((ReadOnlySpan<byte>)bytes);

        Rgba32 color;
        switch (key)
        {
            case "EyeColorLeft":
            case "EyeColorRight":
                if (!TryFull(data.Interface.Eyes, value, out color)) return false;
                break;
            case "HighlightsColor":
                if (!TryFull(data.Interface.HairHighlights, value, out color)) return false;
                break;
            case "TattooColor":
                if (!TryFull(data.Interface.Features, value, out color)) return false;
                break;
            case "LipColor":
                if (!TryToned(data.Parameters.LipsDark, data.Parameters.LipsLight, value, out color)) return false;
                break;
            case "FacePaintColor":
                if (!TryToned(data.Parameters.FacePaintDark, data.Parameters.FacePaintLight, value, out color)) return false;
                break;
            case "SkinColor":
                if (!TryRaceGender(clan, gender, out var subRace, out var genderEnum)
                    || !TryFull(data.GetSkin(subRace, genderEnum, ui: true), value, out color)) return false;
                break;
            case "HairColor":
                if (!TryRaceGender(clan, gender, out var hairSubRace, out var hairGender)
                    || !TryFull(data.GetHairUi(hairSubRace, hairGender), value, out color)) return false;
                break;
            default:
                return false;
        }

        rgb = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        return true;
    }

    // The plain 192-colour tables that everyone shares (eyes, highlights, features, skin, hair).
    private static bool TryFull(in CmpData.FullColors table, int value, out Rgba32 color)
    {
        if ((uint)value >= 192)
        {
            color = default;
            return false;
        }
        color = table[value];
        return true;
    }

    // Lips and face paint are split into 96 "dark" colours (values 0-95) and 96 "light" ones (128-223).
    private static bool TryToned(in CmpData.TonedColors dark, in CmpData.TonedColors light, int value, out Rgba32 color)
    {
        if (value is >= 0 and < 96)
            color = dark[value];
        else if (value is >= 128 and < 224)
            color = light[value - 128];
        else
        {
            color = default;
            return false;
        }
        return true;
    }

    // Glamourer's clan (1-16) maps directly onto Penumbra.GameData's SubRace enum, and its gender (0 male,
    // 1 female) onto Gender.Male/Female.
    private static bool TryRaceGender(int clan, int gender, out SubRace subRace, out Gender genderEnum)
    {
        subRace = default;
        genderEnum = default;
        if (clan is < 1 or > 16)
            return false;
        subRace = (SubRace)clan;
        genderEnum = gender == 1 ? Gender.Female : Gender.Male;
        return true;
    }

    private byte[]? LoadCmp()
    {
        if (cmpLoadAttempted)
            return cmpBytes;

        cmpLoadAttempted = true;
        try
        {
            var file = Plugin.DataManager.GetFile("chara/xls/charamake/human.cmp");
            cmpBytes = file?.Data;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load human.cmp; customization colour previews disabled");
            cmpBytes = null;
        }
        return cmpBytes;
    }
}
