using System;
using System.Collections.Concurrent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Aetherfit.Services;

public sealed class GameDataService
{
    private readonly ExcelSheet<Item>? itemSheet;
    private readonly ExcelSheet<Stain>? stainSheet;
    private readonly ExcelSheet<Glasses>? glassesSheet;

    private readonly ConcurrentDictionary<ulong, string> itemNameCache = new();
    private readonly ConcurrentDictionary<byte, (string Name, uint Color)> stainCache = new();
    private readonly ConcurrentDictionary<ulong, string> glassesNameCache = new();

    public GameDataService()
    {
        try { itemSheet = Plugin.DataManager.GetExcelSheet<Item>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load Item excel sheet"); }

        try { stainSheet = Plugin.DataManager.GetExcelSheet<Stain>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load Stain excel sheet"); }

        try { glassesSheet = Plugin.DataManager.GetExcelSheet<Glasses>(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to load Glasses excel sheet"); }
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
