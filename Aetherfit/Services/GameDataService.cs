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
        // Melee DPS ([43] = Beastmaster, the upcoming limited job; provisional role until officially classified)
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
        try { itemSheet = Plugin.DataManager.GetExcelSheet<Item>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load Item excel sheet"); }

        try { stainSheet = Plugin.DataManager.GetExcelSheet<Stain>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load Stain excel sheet"); }

        try { glassesSheet = Plugin.DataManager.GetExcelSheet<Glasses>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load Glasses excel sheet"); }

        try { classJobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load ClassJob excel sheet"); }
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

    public string ResolveJobName(uint rowId)
    {
        if (jobNameCache.TryGetValue(rowId, out var cached))
            return cached;

        var name = LookupJobName(rowId);
        jobNameCache[rowId] = name;
        return name;
    }

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

        if (itemNameCache.TryGetValue(itemId, out var cached))
            return cached;

        var name = LookupItemName(itemId);
        itemNameCache[itemId] = name;
        return name;
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

        if (stainCache.TryGetValue(stainId, out var cached))
            return cached;

        var info = LookupStain(stainId);
        stainCache[stainId] = info;
        return info;
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

        if (glassesNameCache.TryGetValue(bonusId, out var cached))
            return cached;

        var name = LookupBonusItemName(slotKey, bonusId);
        glassesNameCache[bonusId] = name;
        return name;
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
}
