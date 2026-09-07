using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;

namespace Aetherfit.Services.Designs;

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

    // A mod association (State == Enabled) forces its own enabled state via a temporary Penumbra
    // override at apply time, so the mod's state in the permanent collection never matters - the only
    // way an association can actually fail is the mod no longer being installed at all.
    public List<CachedMod> GetMissingModAssociations(CachedOutfit details)
        => details.Mods.Where(m => m.State == ModState.Enabled
                                 && !penumbra.GetInstalledModDirectories().Contains(m.Directory))
            .ToList();

    public bool HasMissingModAssociation(CachedOutfit details)
        => GetMissingModAssociations(details).Count > 0;

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
        var clan = details.CustomizeClanApplied ? details.CustomizeClan : HairAttribution.CurrentPlayerClan();
        var genderValue = details.CustomizeGenderApplied ? details.CustomizeGender : HairAttribution.CurrentPlayerGender();
        return HairAttribution.HairFragment(clan, genderValue);
    }

    // True when a Penumbra changed-item key is the design's hairstyle.
    private static bool HairKeyMatches(string key, string fragment, int expectedId)
        => HairAttribution.KeyMatches(key, fragment, expectedId);
}
