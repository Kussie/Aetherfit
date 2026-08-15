using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;
using Aetherfit.Utils;
using Penumbra.Api.Enums;

namespace Aetherfit.Services.Designs;

public sealed class DesignApplyService
{
    private const int RecentHistoryCap = 10;

    private readonly Plugin plugin;
    private bool applyingLayer;

    public DesignApplyService(Plugin plugin) => this.plugin = plugin;

    public readonly record struct ApplyResult(Guid? DesignId, string? Error)
    {
        public static ApplyResult Ok(Guid id) => new(id, null);
        public static ApplyResult Fail(string error) => new(null, error);
    }

    // Every no-op guard clause below both logs and returns a failure - one place to do both.
    private static ApplyResult Fail(string msg)
    {
        Plugin.Log.Info(msg);
        return ApplyResult.Fail(msg);
    }

    public void ApplyDesignById(Guid id, bool recordLastApplied = true)
        => ApplyDesignCore(id,
            applyingLayer ? new List<Guid>() : PickLayers(id, isBefore: true),
            applyingLayer ? new List<Guid>() : PickLayers(id, isBefore: false),
            recordLastApplied: recordLastApplied);

    // Applies just one equipment slot from a design onto the character's current outfit, leaving
    // everything else worn untouched. Bypasses ApplyDesignCore on purpose - none of its "before base
    // apply" side effects (temp-setting resets, layers, variant inheritance, last-worn bookkeeping)
    // belong to a single-slot tweak.
    public void ApplySingleEquipmentSlot(Guid designId, EquipmentSlot slot, string slotLabel)
    {
        var name = plugin.Configuration.ResolveDesignName(designId);
        if (!plugin.Configuration.CachedOutfits.TryGetValue(designId, out var outfit))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply {slotLabel}: design not found.");
            return;
        }

        if (!plugin.Configuration.IsProviderEnabled(outfit.Source))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply {slotLabel} from \"{name}\": its source is currently disabled.");
            return;
        }

        var slotData = outfit.Equipment.FirstOrDefault(e => e.Slot == slot);
        if (slotData == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}\"{name}\" doesn't have a {slotLabel} item saved.");
            return;
        }

        // Applied even if the design's own Apply flag for this slot is off - the user asked for just
        // this slot specifically, which is a more targeted ask than the design's own default.
        var itemName = plugin.GameData.ResolveItemName(slotData.ItemId);
        ApplySingleSlotMod(plugin.Attribution.Build(outfit).Items.GetValueOrDefault(itemName));
        plugin.Glamourer.ApplySingleEquipmentSlot(designId, slotData, $"{slotLabel} from {name}");
    }

    public void ApplySingleBonusItem(Guid designId, string bonusSlotKey, string slotLabel)
    {
        var name = plugin.Configuration.ResolveDesignName(designId);
        if (!plugin.Configuration.CachedOutfits.TryGetValue(designId, out var outfit))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply {slotLabel}: design not found.");
            return;
        }

        if (!plugin.Configuration.IsProviderEnabled(outfit.Source))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply {slotLabel} from \"{name}\": its source is currently disabled.");
            return;
        }

        var bonusData = outfit.BonusItems.FirstOrDefault(b => b.Slot == bonusSlotKey);
        if (bonusData == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}\"{name}\" doesn't have a {slotLabel} item saved.");
            return;
        }

        var itemName = plugin.GameData.ResolveBonusItemName(bonusData.Slot, bonusData.ItemId);
        ApplySingleSlotMod(plugin.Attribution.Build(outfit).Items.GetValueOrDefault(itemName));
        plugin.Glamourer.ApplySingleBonusItem(designId, bonusData, $"{slotLabel} from {name}");
    }

    public void ApplySingleCustomization(Guid designId, string customizeKey, string slotLabel)
    {
        var name = plugin.Configuration.ResolveDesignName(designId);
        if (!plugin.Configuration.CachedOutfits.TryGetValue(designId, out var outfit))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply {slotLabel}: design not found.");
            return;
        }

        if (!plugin.Configuration.IsProviderEnabled(outfit.Source))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply {slotLabel} from \"{name}\": its source is currently disabled.");
            return;
        }

        var customization = outfit.Customizations.FirstOrDefault(c => c.Key == customizeKey);
        if (customization == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}\"{name}\" doesn't have {slotLabel} saved.");
            return;
        }

        ApplySingleSlotMod(customizeKey == "Hairstyle" ? plugin.Attribution.Build(outfit).Hairstyle : null);
        plugin.Glamourer.ApplySingleCustomization(designId, customization, $"{slotLabel} from {name}");
    }

    // Own lock key, distinct from Glamourer's own two (-1610/-6160, see GlamourerService.TemporarySettingsKeys)
    // and SGS's per-slot range (SimpleGlamourSwitcherService.KeyBase) - only ever one slot at a time here,
    // so a single fixed key is enough.
    private const int SingleSlotModKey = -0x41455301;

    // ApplyEquipmentState/ApplySingleEquipmentSlot never gets Glamourer's own automatic mod-association
    // handling the way a full ApplyDesign(by GUID) does, so the associated mod has to be pushed to
    // Penumbra ourselves first - same mechanism SimpleGlamourSwitcherService.ApplySlotMods already uses.
    private void ApplySingleSlotMod(CachedMod? mod)
    {
        plugin.Penumbra.RemoveAllTemporaryModSettingsPlayer(SingleSlotModKey);

        if (mod == null)
            return;

        var settings = mod.Settings.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.Split(", ", StringSplitOptions.RemoveEmptyEntries));
        var result = plugin.Penumbra.SetTemporaryModSettingsPlayer(mod.Directory, true, mod.Priority,
            settings, "Aetherfit (single-slot apply)", SingleSlotModKey);
        if (result != PenumbraApiEc.Success)
            Plugin.Log.Warning("Failed to apply associated mod {Mod} for single-slot apply: {Result}", mod.Directory, result);
    }

    private void ApplyDesignCore(Guid id, List<Guid> beforeLayerIds, List<Guid> afterLayerIds, bool quiet = false,
        bool recordLastApplied = true)
    {
        var name = plugin.Configuration.ResolveDesignName(id);
        if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{name}\": design not found.");
            return;
        }

        if (!plugin.Configuration.IsProviderEnabled(outfit.Source))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{name}\": its source is currently disabled in Aetherfit's settings.");
            return;
        }

        var provider = plugin.DesignProviders.FirstOrDefault(p => p.Source == outfit.Source);
        if (provider == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to apply \"{name}\": its source is no longer available.");
            return;
        }

        if (outfit.Source != DesignSource.SimpleGlamourSwitcher
            && plugin.Configuration.IsProviderEnabled(DesignSource.SimpleGlamourSwitcher))
        {
            plugin.SimpleGlamourSwitcher.ClearAllTemporaryModSettings();
            plugin.SimpleGlamourSwitcher.RevertCustomizePlusTemplates();
        }

        // A leftover single-slot mod association should never bleed into an unrelated full apply.
        plugin.Penumbra.RemoveAllTemporaryModSettingsPlayer(SingleSlotModKey);

        if (plugin.Configuration.ResetTemporarySettingsBeforeApply(outfit.Source))
        {
            plugin.Penumbra.RemoveAllTemporaryModSettingsPlayer(0);
            foreach (var key in GlamourerService.TemporarySettingsKeys)
                plugin.Penumbra.RemoveAllTemporaryModSettingsPlayer(key);
            plugin.Penumbra.RedrawLocalPlayer();
        }

        // Applied before the base so anything it doesn't explicitly manage survives underneath it.
        ApplyLayers(beforeLayerIds, recordLastApplied);

        if (plugin.Configuration.GetVariantInfo(id) is { InheritGear: true } variant
            && plugin.Configuration.CachedOutfits.TryGetValue(variant.ParentId, out var parentOutfit)
            && plugin.Configuration.IsProviderEnabled(parentOutfit.Source)
            && plugin.DesignProviders.FirstOrDefault(p => p.Source == parentOutfit.Source) is { } parentProvider)
        {
            // Applies the parent first so slots this variant doesn't manage survive underneath it - not
            // routed through ApplyLayer (Glamourer-only) and doesn't count toward RecordLastApplied/
            // RecordLastWorn, since only the variant's own apply below is the user's actual choice.
            parentProvider.Apply(parentOutfit.ProviderDesignId, parentOutfit.Name, null, quiet: true);
        }

        var layerNames = beforeLayerIds.Concat(afterLayerIds).Select(plugin.Configuration.ResolveDesignName).ToList();
        if (!provider.Apply(outfit.ProviderDesignId, name, layerNames, quiet))
            return;

        if (!quiet && plugin.GameData.DesignHasAnyIncompatibleItems(outfit))
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}\"{name}\" can only be partially applied on your current character.");

        ApplyLayers(afterLayerIds, recordLastApplied);

        // Only an active pick counts as "worn" - ReapplyLastWorn (restore) and Batch Screenshot (preview)
        // opt out via this flag, gating both last-worn and last-applied bookkeeping together.
        if (recordLastApplied)
        {
            RecordLastWorn(id, beforeLayerIds, afterLayerIds);
            plugin.Configuration.RecordLastApplied(id, outfit);
        }
    }

    // Applies each layer id (in order) via its provider's ApplyLayer IPC. Used for both the before- and
    // after-base layer passes - only the order relative to the base's own Apply call differs.
    // recordLastApplied mirrors the base design's own flag: a layer worn alongside a restored (not
    // actively chosen) base is itself just being restored, not freshly worn.
    private void ApplyLayers(List<Guid> layerIds, bool recordLastApplied)
    {
        if (layerIds.Count == 0)
            return;

        applyingLayer = true;
        try
        {
            foreach (var layerId in layerIds)
            {
                if (plugin.Configuration.CachedOutfits.TryGetValue(layerId, out var layerOutfit)
                    && plugin.DesignProviders.FirstOrDefault(p => p.Source == layerOutfit.Source) is { } layerProvider)
                {
                    layerProvider.ApplyLayer(layerOutfit.ProviderDesignId);
                    if (recordLastApplied)
                        plugin.Configuration.RecordLastApplied(layerId, layerOutfit);
                }
            }
        }
        finally { applyingLayer = false; }
    }

    private void RecordLastWorn(Guid baseId, List<Guid> beforeLayerIds, List<Guid> afterLayerIds)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        settings.LastWornDesign = baseId;
        settings.LastWornBeforeLayers = new List<Guid>(beforeLayerIds);
        settings.LastWornLayers = new List<Guid>(afterLayerIds);

        settings.RecentDesignHistory.Remove(baseId);
        settings.RecentDesignHistory.Insert(0, baseId);
        if (settings.RecentDesignHistory.Count > RecentHistoryCap)
            settings.RecentDesignHistory.RemoveRange(RecentHistoryCap, settings.RecentDesignHistory.Count - RecentHistoryCap);

        plugin.Configuration.Save();
    }

    public Guid PickRandomDesign(IReadOnlyList<Guid> candidates)
    {
        if (candidates.Count == 1)
            return candidates[0];

        var history = Plugin.PlayerState.IsLoaded
            && plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            ? settings.RecentDesignHistory
            : new List<Guid>();

        // With a small pool a long history would suppress everything equally, so only let it reach back far enough to leave at least one full-weight candidate.
        var depth = Math.Min(history.Count, candidates.Count - 1);

        var pool = candidates;
        if (depth > 0)
        {
            var last = history[0];
            var filtered = candidates.Where(id => id != last).ToList();
            if (filtered.Count > 0)
                pool = filtered;
        }

        var weights = new double[pool.Count];
        double total = 0;
        for (var i = 0; i < pool.Count; i++)
        {
            var pos = history.IndexOf(pool[i]);
            var recentWeight = pos < 0 || pos >= depth ? 1.0 : (pos + 1.0) / (depth + 1.0);
            weights[i] = recentWeight * LongTermRecencyFactor(pool[i]);
            total += weights[i];
        }

        var roll = Random.Shared.NextDouble() * total;
        for (var i = 0; i < pool.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0)
                return pool[i];
        }

        return pool[^1];
    }

    // Long-term "haven't worn this in a while" boost, layered on top of the short-term anti-repeat
    // weighting above. Capped so a design that's never been worn doesn't dominate every roll forever.
    private const double LongTermRecencyCapDays = 90.0;

    private double LongTermRecencyFactor(Guid id)
    {
        var days = plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit) && outfit.LastAppliedAt is { } lastWorn
            ? (DateTimeOffset.UtcNow - lastWorn).TotalDays
            : LongTermRecencyCapDays;

        return 1.0 + Math.Clamp(days, 0, LongTermRecencyCapDays) / LongTermRecencyCapDays;
    }

    public ApplyResult ReapplyLastWorn(bool quiet = false)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return ApplyResult.Fail("Log in to a character first.");

        if (!plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            || settings.LastWornDesign is not { } baseId)
            return ApplyResult.Fail("No previously worn design recorded for this character yet.");

        if (!plugin.Configuration.CachedOutfits.ContainsKey(baseId))
            return ApplyResult.Fail("Your previously worn design no longer exists in Glamourer — nothing reapplied.");

        List<Guid> FilterExisting(List<Guid> layers) => plugin.Configuration.EnableRandomLayers
            ? layers.Where(l => plugin.Configuration.CachedOutfits.ContainsKey(l)).ToList()
            : new List<Guid>();

        var beforeLayers = FilterExisting(settings.LastWornBeforeLayers);
        var afterLayers = FilterExisting(settings.LastWornLayers);

        var skipped = (settings.LastWornBeforeLayers.Count - beforeLayers.Count)
            + (settings.LastWornLayers.Count - afterLayers.Count);
        if (skipped > 0 && plugin.Configuration.EnableRandomLayers)
            Plugin.Log.Info($"Skipped {skipped} previously worn layer(s) that no longer exist in Glamourer.");

        ApplyDesignCore(baseId, beforeLayers, afterLayers, quiet, recordLastApplied: false);
        return ApplyResult.Ok(baseId);
    }

    // Walks the base design's layer slots for the given placement, top-down, picking one job-matching
    // design per slot (at random when the slot holds several). Returns the layers to apply, in order.
    private List<Guid> PickLayers(Guid baseId, bool isBefore)
    {
        var picks = new List<Guid>();
        if (!plugin.Configuration.EnableRandomLayers || !Plugin.PlayerState.IsLoaded)
            return picks;

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        foreach (var slot in plugin.Configuration.GetLayerSlots(baseId).Where(s => s.IsBefore == isBefore))
        {
            var candidates = slot.Designs
                .Where(l => (l.AllJobs || l.Jobs.Contains(jobId)) && SupportsLayers(l.DesignId))
                .ToList();

            if (candidates.Count > 0)
                picks.Add(candidates[Random.Shared.Next(candidates.Count)].DesignId);
        }

        return picks;
    }

    // A design can only be picked as a layer if it still exists and its provider supports layering
    // (Glamourer only, for now - a source like Glamaholic has no equivalent apply-on-top mechanism).
    private bool SupportsLayers(Guid id)
        => plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)
           && plugin.DesignProviders.FirstOrDefault(p => p.Source == outfit.Source) is { } provider
           && provider.Capabilities.HasFlag(DesignProviderCapabilities.Layers);

    public bool IsUsable(Guid id)
        => !plugin.Configuration.HiddenDesigns.Contains(id)
           && plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)
           && plugin.Configuration.IsProviderEnabled(outfit.Source);

    public ApplyResult ApplyRandomDesign()
    {
        var ids = plugin.Configuration.CachedOutfits.Keys
            .Where(IsUsable)
            .ToList();
        if (ids.Count == 0)
            return Fail("No cached designs — open Aetherfit and click Refresh first.");

        var pick = PickRandomDesign(ids);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public ApplyResult ApplyRandomByTags(IReadOnlyCollection<string> tags, bool favouritesOnly = false)
    {
        if (tags.Count == 0)
            return Fail("No tags provided.");

        var matching = plugin.Configuration.CachedOutfits
            .Where(kv => IsUsable(kv.Key)
                         && (!favouritesOnly || plugin.Configuration.FavouriteDesigns.Contains(kv.Key))
                         && tags.All(t => TagMatching.AnyMatch(kv.Value.Tags, t)))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
            return Fail($"No {(favouritesOnly ? "favourite designs" : "designs")} match tags: {string.Join(", ", tags)}");

        var pick = PickRandomDesign(matching);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    // Exact (case-insensitive) match only, across every source - a name is expected to be typed or
    // pasted verbatim (see /aetherfit wear's mandatory quoting), so silently guessing at a partial
    // match would be more likely to wear the wrong thing than to help.
    public ApplyResult ApplyByName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return Fail("No design name provided.");

        var matches = plugin.Configuration.CachedOutfits
            .Where(kv => IsUsable(kv.Key) && string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        if (matches.Count == 0)
            return Fail($"No design named \"{name}\" found.");

        if (matches.Count > 1)
            return Fail($"{matches.Count} designs are named \"{name}\" — can't tell which one you mean.");

        var pick = matches[0];
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public ApplyResult ApplyRandomFavourite(bool matchCurrentJob)
    {
        var favourites = plugin.Configuration.FavouriteDesigns
            .Where(IsUsable)
            .ToList();

        if (favourites.Count == 0)
            return Fail("No favourite designs yet — click the ☆ star on a design first.");

        if (matchCurrentJob)
        {
            if (!Plugin.PlayerState.IsLoaded)
                return Fail("Log in to a character first.");

            var jobId = Plugin.PlayerState.ClassJob.RowId;
            favourites = favourites
                .Where(id => plugin.Configuration.DesignJobAssociations.TryGetValue(id, out var jobs) && jobs.Contains(jobId))
                .ToList();

            if (favourites.Count == 0)
            {
                var jobName = plugin.GameData.ResolveJobName(jobId);
                return Fail($"No favourite designs associated with your current job ({jobName}).");
            }
        }

        var pick = PickRandomDesign(favourites);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public ApplyResult ApplyRandomByCurrentJob()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return Fail("Log in to a character first.");

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        var matching = plugin.Configuration.DesignJobAssociations
            .Where(kv => kv.Value.Contains(jobId) && IsUsable(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
        {
            var jobName = plugin.GameData.ResolveJobName(jobId);
            return Fail($"No designs associated with your current job ({jobName}).");
        }

        var pick = PickRandomDesign(matching);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public void RevertAppearance()
    {
        plugin.Glamourer.Revert();
        if (plugin.Configuration.IsProviderEnabled(DesignSource.SimpleGlamourSwitcher))
        {
            plugin.SimpleGlamourSwitcher.ClearAllTemporaryModSettings();
            plugin.SimpleGlamourSwitcher.RevertCustomizePlusTemplates();
        }

        // A deliberate revert means "I want my real gear" — forget the last-worn record so LoginAction.ReapplyLast doesn't re-dress the character on the next login.
        if (Plugin.PlayerState.IsLoaded
            && plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            && settings.LastWornDesign != null)
        {
            settings.LastWornDesign = null;
            settings.LastWornLayers.Clear();
            settings.LastWornBeforeLayers.Clear();
            plugin.Configuration.Save();
        }
    }
}
