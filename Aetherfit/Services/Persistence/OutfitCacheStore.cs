using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Persistence;

// Persists CachedOutfits in its own file instead of the plugin config. The cache is derived data
// (rebuildable from Glamourer over IPC) and by far the largest thing we store, so keeping it out of
// the config means favourite toggles and apply records don't rewrite megabytes of JSON every time.
public sealed class OutfitCacheStore
{
    // 1 = pre-provider-abstraction (bare `{ guid: outfit }` dictionary, no envelope at all).
    // 2 = adds CachedOutfit.Source/ProviderDesignId, wrapped in CacheEnvelope.
    private const int CurrentCacheVersion = 2;

    private readonly Configuration configuration;
    private readonly string path;

    public OutfitCacheStore(Configuration configuration)
    {
        this.configuration = configuration;
        path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "outfit-cache.json");
    }

    private sealed class CacheEnvelope
    {
        public int Version { get; set; }
        public Dictionary<Guid, CachedOutfit> Outfits { get; set; } = new();
    }

    // Called once at startup. Older versions stored the cache inside the plugin config; if the cache
    // file doesn't exist yet, whatever the config deserialized becomes the initial file (migration).
    public void Load()
    {
        if (!File.Exists(path))
        {
            if (configuration.CachedOutfits.Count > 0)
                Save();
            return;
        }

        try
        {
            var root = JObject.Parse(File.ReadAllText(path));
            Dictionary<Guid, CachedOutfit> outfits;
            int fromVersion;

            if (root.ContainsKey("Version")) // current envelope shape
            {
                var envelope = root.ToObject<CacheEnvelope>()!;
                outfits = envelope.Outfits;
                fromVersion = envelope.Version;
            }
            else // v1: no envelope ever existed, root IS the bare outfit dictionary
            {
                outfits = root.ToObject<Dictionary<Guid, CachedOutfit>>() ?? new();
                fromVersion = 1;
            }

            if (fromVersion < CurrentCacheVersion)
                MigrateOutfits(outfits, fromVersion);
            configuration.CachedOutfits = outfits;
        }
        catch (Exception ex)
        {
            // A refresh rebuilds it from Glamourer, so a corrupt cache is an inconvenience, not data loss.
            Plugin.Log.Warning(ex, "Failed to load the outfit cache; it will be rebuilt on the next refresh");
        }
    }

    // Ordered, explicit migration steps - add one new `if (fromVersion < N)` block per future schema
    // change here, instead of a scattered default-value check.
    private static void MigrateOutfits(Dictionary<Guid, CachedOutfit> outfits, int fromVersion)
    {
        if (fromVersion < 2)
        {
            // v1 -> v2: introduced Source/ProviderDesignId. Every v1 entry is implicitly Glamourer-
            // sourced, and its native provider id has always just been its own dictionary key.
            foreach (var (id, outfit) in outfits)
                outfit.ProviderDesignId = id;
        }
    }

    // Called after a refresh replaces the cache or tags are written into it — not on ordinary config saves.
    public void Save()
    {
        try
        {
            var envelope = new CacheEnvelope { Version = CurrentCacheVersion, Outfits = configuration.CachedOutfits };
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(envelope, Formatting.None));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to save the outfit cache");
        }
    }
}
