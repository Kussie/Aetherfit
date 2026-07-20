using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Aetherfit.Services;

// Persists CachedOutfits in its own file instead of the plugin config. The cache is derived data
// (rebuildable from Glamourer over IPC) and by far the largest thing we store, so keeping it out of
// the config means favourite toggles and apply records don't rewrite megabytes of JSON every time.
public sealed class OutfitCacheStore
{
    private readonly Configuration configuration;
    private readonly string path;

    public OutfitCacheStore(Configuration configuration)
    {
        this.configuration = configuration;
        path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "outfit-cache.json");
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
            var loaded = JsonConvert.DeserializeObject<Dictionary<Guid, CachedOutfit>>(File.ReadAllText(path));
            if (loaded != null)
                configuration.CachedOutfits = loaded;
        }
        catch (Exception ex)
        {
            // A refresh rebuilds it from Glamourer, so a corrupt cache is an inconvenience, not data loss.
            Plugin.Log.Warning(ex, "Failed to load the outfit cache; it will be rebuilt on the next refresh");
        }
    }

    // Called after a refresh replaces the cache or tags are written into it — not on ordinary config saves.
    public void Save()
    {
        try
        {
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(configuration.CachedOutfits, Formatting.None));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to save the outfit cache");
        }
    }
}
