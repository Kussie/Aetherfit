using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Aetherfit.Services.Game;

namespace Aetherfit.Services.Designs;

// The three checks behind the Health Report window - kept independent of the window itself so each
// check is a plain, testable function over whatever's currently cached, not entangled with ImGui.
public sealed class HealthReportService
{
    public const string MissingModCheck = "MissingMod";
    public const string BrokenItemCheck = "BrokenItem";
    public const string DuplicateCheck = "Duplicate";
    public const string IncompatibleCheck = "Incompatible";

    private readonly Configuration configuration;
    private readonly GameDataService gameData;
    private readonly DesignAttributionService attribution;

    public HealthReportService(Configuration configuration, GameDataService gameData, DesignAttributionService attribution)
    {
        this.configuration = configuration;
        this.gameData = gameData;
        this.attribution = attribution;
    }

    public sealed record MissingModFinding(Guid Id, string Name, IReadOnlyList<CachedMod> MissingMods);
    public sealed record BrokenItemFinding(Guid Id, string Name, IReadOnlyList<string> BrokenSlots);
    public sealed record DuplicateGroup(IReadOnlyList<(Guid Id, string Name)> Designs);
    public sealed record IncompatibleItemFinding(Guid Id, string Name, IReadOnlyList<string> IncompatibleSlots);

    public List<MissingModFinding> FindMissingModAssociations()
    {
        var result = new List<MissingModFinding>();
        foreach (var (id, outfit) in configuration.CachedOutfits)
        {
            if (!configuration.IsProviderEnabled(outfit.Source) || configuration.IsHealthCheckIgnored(id, MissingModCheck))
                continue;

            var missing = attribution.GetMissingModAssociations(outfit);
            if (missing.Count > 0)
                result.Add(new MissingModFinding(id, outfit.Name, missing));
        }
        return result;
    }

    public List<BrokenItemFinding> FindBrokenItems()
    {
        var result = new List<BrokenItemFinding>();
        foreach (var (id, outfit) in configuration.CachedOutfits)
        {
            if (!configuration.IsProviderEnabled(outfit.Source) || configuration.IsHealthCheckIgnored(id, BrokenItemCheck))
                continue;

            var broken = new List<string>();
            foreach (var slot in outfit.Equipment)
                if (slot.Apply && !gameData.ItemExists(slot.ItemId))
                    broken.Add(slot.Slot.ToString());
            foreach (var bonus in outfit.BonusItems)
                if (bonus.Apply && !gameData.BonusItemExists(bonus.Slot, bonus.ItemId))
                    broken.Add(bonus.Slot);

            if (broken.Count > 0)
                result.Add(new BrokenItemFinding(id, outfit.Name, broken));
        }
        return result;
    }

    // Designs with equipment that can't be worn by the current character - or, for a design that
    // customizes race/gender as part of itself (see GameDataService.ResolveEffectiveRaceGender),
    // whatever race/gender it applies rather than the live character.
    public List<IncompatibleItemFinding> FindIncompatibleDesigns()
    {
        var result = new List<IncompatibleItemFinding>();
        foreach (var (id, outfit) in configuration.CachedOutfits)
        {
            if (!configuration.IsProviderEnabled(outfit.Source) || configuration.IsHealthCheckIgnored(id, IncompatibleCheck))
                continue;

            var incompatible = gameData.GetIncompatibleItems(outfit);
            if (incompatible.Count > 0)
                result.Add(new IncompatibleItemFinding(id, outfit.Name, incompatible));
        }
        return result;
    }

    public List<DuplicateGroup> FindDuplicates()
    {
        var groups = new Dictionary<string, List<(Guid Id, string Name)>>(StringComparer.Ordinal);
        foreach (var (id, outfit) in configuration.CachedOutfits)
        {
            if (!configuration.IsProviderEnabled(outfit.Source) || configuration.IsHealthCheckIgnored(id, DuplicateCheck))
                continue;

            var key = BuildEquipmentSignature(outfit);
            if (key.Length == 0)
                continue; // nothing applied - not a meaningful "duplicate" of anything

            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<(Guid, string)>();
                groups[key] = list;
            }
            list.Add((id, outfit.Name));
        }

        return groups.Values.Where(g => g.Count > 1).Select(g => new DuplicateGroup(g)).ToList();
    }

    private string BuildEquipmentSignature(CachedOutfit outfit)
    {
        var affectedBy = attribution.Build(outfit).Items;

        var sb = new StringBuilder();
        foreach (var slot in outfit.Equipment.Where(e => e.Apply).OrderBy(e => e.Slot))
        {
            sb.Append((int)slot.Slot).Append(':').Append(slot.ItemId);
            if (slot.ApplyStain)
                sb.Append(':').Append(slot.Stain).Append(':').Append(slot.Stain2);

            var itemName = gameData.ResolveItemName(slot.ItemId);
            if (itemName != GameDataService.NothingItemName && affectedBy.TryGetValue(itemName, out var mod))
                AppendModSignature(sb, mod);

            sb.Append('|');
        }
        foreach (var bonus in outfit.BonusItems.Where(b => b.Apply).OrderBy(b => b.Slot, StringComparer.Ordinal))
        {
            sb.Append(bonus.Slot).Append(':').Append(bonus.ItemId);

            var bonusName = gameData.ResolveBonusItemName(bonus.Slot, bonus.ItemId);
            if (bonusName != GameDataService.NothingItemName && affectedBy.TryGetValue(bonusName, out var bonusMod))
                AppendModSignature(sb, bonusMod);

            sb.Append('|');
        }
        return sb.ToString();
    }

    // Same mod, different option-group selections, can render an item completely differently - not
    // just which mod is responsible, but what it's actually configured to do, has to match for two
    // designs to be considered the same look.
    private static void AppendModSignature(StringBuilder sb, CachedMod mod)
    {
        sb.Append(":mod=").Append(mod.Directory);
        foreach (var (group, value) in mod.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(':').Append(group).Append('=').Append(value);
    }
}
