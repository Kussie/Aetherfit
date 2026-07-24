using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Utils;

namespace Aetherfit.Services;

// Picking and applying designs (including random picks and additional layers) - split out of MainWindow
// so it's testable independent of ImGui, and callable from both the UI and Plugin's slash commands.
public sealed class DesignApplyService
{
    private const int RecentHistoryCap = 10;

    private readonly Plugin plugin;
    private bool applyingLayer;

    public DesignApplyService(Plugin plugin) => this.plugin = plugin;

    // The id of the design actually applied, so the caller can update its own selection UI; null if
    // nothing was applied (see Error).
    public readonly record struct ApplyResult(Guid? DesignId, string? Error)
    {
        public static ApplyResult Ok(Guid id) => new(id, null);
        public static ApplyResult Fail(string error) => new(null, error);
    }

    public void ApplyDesignById(Guid id)
        => ApplyDesignCore(id, applyingLayer ? new List<Guid>() : PickLayers(id));

    private void ApplyDesignCore(Guid id, List<Guid> layerIds, bool quiet = false)
    {
        var name = plugin.Configuration.ResolveDesignName(id);
        if (!plugin.Glamourer.Apply(id, name, layerIds.Select(plugin.Configuration.ResolveDesignName).ToList(), quiet))
            return;

        if (layerIds.Count > 0)
        {
            applyingLayer = true;
            try
            {
                foreach (var layerId in layerIds)
                    plugin.Glamourer.ApplyLayer(layerId);
            }
            finally { applyingLayer = false; }
        }

        RecordLastWorn(id, layerIds);
    }

    private void RecordLastWorn(Guid baseId, List<Guid> layerIds)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        settings.LastWornDesign = baseId;
        settings.LastWornLayers = new List<Guid>(layerIds);

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
            weights[i] = pos < 0 || pos >= depth ? 1.0 : (pos + 1.0) / (depth + 1.0);
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

    public ApplyResult ReapplyLastWorn(bool quiet = false)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return ApplyResult.Fail("Log in to a character first.");

        if (!plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            || settings.LastWornDesign is not { } baseId)
            return ApplyResult.Fail("No previously worn design recorded for this character yet.");

        if (!plugin.Configuration.CachedOutfits.ContainsKey(baseId))
            return ApplyResult.Fail("Your previously worn design no longer exists in Glamourer — nothing reapplied.");

        var layers = plugin.Configuration.EnableRandomLayers
            ? settings.LastWornLayers.Where(l => plugin.Configuration.CachedOutfits.ContainsKey(l)).ToList()
            : new List<Guid>();
        if (layers.Count < settings.LastWornLayers.Count && plugin.Configuration.EnableRandomLayers)
            Plugin.Log.Info($"Skipped {settings.LastWornLayers.Count - layers.Count} previously worn layer(s) that no longer exist in Glamourer.");

        ApplyDesignCore(baseId, layers, quiet);
        return ApplyResult.Ok(baseId);
    }

    // Walks the base design's layer slots top-down, picking one job-matching design per slot (at random when the slot holds several). Returns the layers to apply, in application order.
    private List<Guid> PickLayers(Guid baseId)
    {
        var picks = new List<Guid>();
        if (!plugin.Configuration.EnableRandomLayers || !Plugin.PlayerState.IsLoaded)
            return picks;

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        foreach (var slot in plugin.Configuration.GetLayerSlots(baseId))
        {
            var candidates = slot.Designs
                .Where(l => (l.AllJobs || l.Jobs.Contains(jobId))
                            && plugin.Configuration.CachedOutfits.ContainsKey(l.DesignId))
                .ToList();

            if (candidates.Count > 0)
                picks.Add(candidates[Random.Shared.Next(candidates.Count)].DesignId);
        }

        return picks;
    }

    public ApplyResult ApplyRandomDesign()
    {
        var ids = plugin.Configuration.CachedOutfits.Keys
            .Where(id => !plugin.Configuration.HiddenDesigns.Contains(id))
            .ToList();
        if (ids.Count == 0)
        {
            const string msg = "No cached designs — open Aetherfit and click Refresh first.";
            Plugin.Log.Info(msg);
            return ApplyResult.Fail(msg);
        }

        var pick = PickRandomDesign(ids);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public ApplyResult ApplyRandomByTags(IReadOnlyCollection<string> tags, bool favouritesOnly = false)
    {
        if (tags.Count == 0)
        {
            const string msg = "No tags provided.";
            Plugin.Log.Info(msg);
            return ApplyResult.Fail(msg);
        }

        var matching = plugin.Configuration.CachedOutfits
            .Where(kv => !plugin.Configuration.HiddenDesigns.Contains(kv.Key)
                         && (!favouritesOnly || plugin.Configuration.FavouriteDesigns.Contains(kv.Key))
                         && tags.All(t => TagMatching.AnyMatch(kv.Value.Tags, t)))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
        {
            var msg = $"No {(favouritesOnly ? "favourite designs" : "designs")} match tags: {string.Join(", ", tags)}";
            Plugin.Log.Info(msg);
            return ApplyResult.Fail(msg);
        }

        var pick = PickRandomDesign(matching);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public ApplyResult ApplyRandomFavourite(bool matchCurrentJob)
    {
        var favourites = plugin.Configuration.FavouriteDesigns
            .Where(id => plugin.Configuration.CachedOutfits.ContainsKey(id)
                         && !plugin.Configuration.HiddenDesigns.Contains(id))
            .ToList();

        if (favourites.Count == 0)
        {
            const string msg = "No favourite designs yet — click the ☆ star on a design first.";
            Plugin.Log.Info(msg);
            return ApplyResult.Fail(msg);
        }

        if (matchCurrentJob)
        {
            if (!Plugin.PlayerState.IsLoaded)
            {
                const string msg = "Log in to a character first.";
                Plugin.Log.Info(msg);
                return ApplyResult.Fail(msg);
            }

            var jobId = Plugin.PlayerState.ClassJob.RowId;
            favourites = favourites
                .Where(id => plugin.Configuration.DesignJobAssociations.TryGetValue(id, out var jobs) && jobs.Contains(jobId))
                .ToList();

            if (favourites.Count == 0)
            {
                var jobName = plugin.GameData.ResolveJobName(jobId);
                var msg = $"No favourite designs associated with your current job ({jobName}).";
                Plugin.Log.Info(msg);
                return ApplyResult.Fail(msg);
            }
        }

        var pick = PickRandomDesign(favourites);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public ApplyResult ApplyRandomByCurrentJob()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            const string msg = "Log in to a character first.";
            Plugin.Log.Info(msg);
            return ApplyResult.Fail(msg);
        }

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        var matching = plugin.Configuration.DesignJobAssociations
            .Where(kv => kv.Value.Contains(jobId)
                         && plugin.Configuration.CachedOutfits.ContainsKey(kv.Key)
                         && !plugin.Configuration.HiddenDesigns.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
        {
            var jobName = plugin.GameData.ResolveJobName(jobId);
            var msg = $"No designs associated with your current job ({jobName}).";
            Plugin.Log.Info(msg);
            return ApplyResult.Fail(msg);
        }

        var pick = PickRandomDesign(matching);
        ApplyDesignById(pick);
        return ApplyResult.Ok(pick);
    }

    public void RevertAppearance()
    {
        plugin.Glamourer.Revert();

        // A deliberate revert means "I want my real gear" — forget the last-worn record so LoginAction.ReapplyLast doesn't re-dress the character on the next login.
        if (Plugin.PlayerState.IsLoaded
            && plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            && settings.LastWornDesign != null)
        {
            settings.LastWornDesign = null;
            settings.LastWornLayers.Clear();
            plugin.Configuration.Save();
        }
    }
}
