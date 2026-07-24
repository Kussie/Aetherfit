using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;

namespace Aetherfit.Services;

// Works out which enabled mod is responsible for the equipment items and hairstyle a design applies, using
// Penumbra's changed-item data. Extracted from the main window so the gallery exporter can bake the same
// attribution into shared bundles (the recipient has no access to the sharer's Penumbra mods).
public sealed class DesignAttributionService
{
    private readonly GameDataService gameData;
    private readonly PenumbraService penumbra;

    public DesignAttributionService(GameDataService gameData, PenumbraService penumbra)
    {
        this.gameData = gameData;
        this.penumbra = penumbra;
    }

    // Items maps a design item name -> the mod responsible for its look; Hairstyle is the mod changing the
    // applied hairstyle, if any.
    public sealed record Result(IReadOnlyDictionary<string, CachedMod> Items, CachedMod? Hairstyle);

    // A mod's display name, falling back to its directory when Glamourer didn't store a name.
    public static string ModDisplayName(CachedMod mod)
        => string.IsNullOrWhiteSpace(mod.Name) ? mod.Directory : mod.Name;

    // Only enabled mods count, and when more than one changes the same thing the highest priority wins.
    // OrderByDescending is stable, so mods sharing a priority keep the order Glamourer listed them in.
    public Result Build(CachedOutfit details)
    {
        var map = new Dictionary<string, CachedMod>(StringComparer.Ordinal);

        // Gather the item names this design actually uses - those are the only ones worth matching against.
        var designItemNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in details.Equipment)
        {
            var name = gameData.ResolveItemName(e.ItemId);
            if (name != GameDataService.NothingItemName)
                designItemNames.Add(name);
        }
        foreach (var b in details.BonusItems)
        {
            var name = gameData.ResolveBonusItemName(b.Slot, b.ItemId);
            if (name != GameDataService.NothingItemName)
                designItemNames.Add(name);
        }

        // The applied hairstyle, if any. Customizations only holds applied entries, so its presence here
        // already means "set to be changed". HairChangedItemFragment is what a matching Penumbra key contains.
        var hairstyle = details.Customizations.FirstOrDefault(c => c.Key == "Hairstyle");
        var hairFragment = hairstyle == null ? null : HairChangedItemFragment(details);
        var hairValue = hairstyle?.RawValue ?? 0;
        CachedMod? hairstyleMod = null;

        if (designItemNames.Count == 0 && hairFragment == null)
            return new Result(map, null);

        foreach (var mod in details.Mods.Where(m => m.State == ModState.Enabled).OrderByDescending(m => m.Priority))
        {
            var changed = penumbra.GetChangedItemNames(mod.Directory, mod.Name);
            if (changed.Count == 0)
                continue;

            foreach (var itemName in designItemNames)
            {
                if (!map.ContainsKey(itemName) && changed.Contains(itemName))
                    map[itemName] = mod;
            }

            if (hairstyleMod == null && hairFragment != null && changed.Any(k => HairKeyMatches(k, hairFragment, hairValue)))
                hairstyleMod = mod;
        }

        return new Result(map, hairstyleMod);
    }

    private static string? HairChangedItemFragment(CachedOutfit details)
    {
        var clan = details.CustomizeClanApplied ? details.CustomizeClan : CurrentPlayerClan();
        var genderValue = details.CustomizeGenderApplied ? details.CustomizeGender : CurrentPlayerGender();

        var race = clan is { } c ? ClanToModelRace(c) : null;
        var gender = genderValue switch { 0 => "Male", 1 => "Female", _ => null };
        return race == null || gender == null ? null : $"{race} {gender} Hair ";
    }

    // The current character's clan (Tribe RowId, 1-16) and gender (0 male / 1 female), or null when not logged in.
    // The Tribe sheet RowIds line up with Glamourer's clan numbering, and Sex.Male/Female are 0/1.
    private static int? CurrentPlayerClan()
        => Plugin.PlayerState.IsLoaded ? (int)Plugin.PlayerState.Tribe.RowId : null;

    private static int? CurrentPlayerGender()
        => Plugin.PlayerState.IsLoaded ? (int)Plugin.PlayerState.Sex : null;

    // True when a Penumbra changed-item key is the design's hairstyle: the right race/gender/Hair fragment
    // and a trailing model id equal to the hairstyle value (parsed as an int so zero-padding doesn't matter).
    private static bool HairKeyMatches(string key, string fragment, int expectedId)
    {
        if (!key.StartsWith("Customization:", StringComparison.Ordinal))
            return false;

        var at = key.IndexOf(fragment, StringComparison.Ordinal);
        if (at < 0)
            return false;

        var idText = key[(at + fragment.Length)..].Trim();
        // The id is the last token; guard against anything trailing it.
        var space = idText.IndexOf(' ');
        if (space >= 0)
            idText = idText[..space];
        return int.TryParse(idText, out var modelId) && modelId == expectedId;
    }

    // Glamourer clan (subrace, 1-16) -> the ModelRace name Penumbra uses in changed-item keys. Hyur splits
    // into Midlander/Highlander; the other races collapse their two tribes onto one model base.
    private static string? ClanToModelRace(int clan) => clan switch
    {
        1 => "Midlander",
        2 => "Highlander",
        3 or 4 => "Elezen",
        5 or 6 => "Lalafell",
        7 or 8 => "Miqo'te",
        9 or 10 => "Roegadyn",
        11 or 12 => "Au Ra",
        13 or 14 => "Hrothgar",
        15 or 16 => "Viera",
        _ => null,
    };
}
