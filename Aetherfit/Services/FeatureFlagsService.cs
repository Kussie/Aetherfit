using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Aetherfit.Services;

// Remote kill-switches, so a feature can be turned off for everyone without shipping a new build.
// Source of truth is flags.json on the repo's master branch; we keep a local copy so the plugin has a
// sane state before the first fetch completes (or if it never does, e.g. offline).
public sealed class FeatureFlagsService
{
    private const string RemoteUrl = "https://raw.githubusercontent.com/Kussie/Aetherfit/refs/heads/master/flags.json";
    private static readonly HttpClient Http = new();

    private readonly string path;
    private Dictionary<string, bool> flags = new();

    public FeatureFlagsService()
    {
        path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "flags.json");
    }

    public bool EnableLiveSharing => IsEnabled("enable_live_sharing");

    // Unknown/missing keys default to enabled - a stale local copy or an older key removed from the
    // remote file should never silently disable a feature.
    public bool IsEnabled(string key) => !flags.TryGetValue(key, out var value) || value;

    // Called once at startup, before the fetch: loads whatever was cached last session.
    public void Load()
    {
        if (!File.Exists(path))
            return;

        try
        {
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(path));
            if (loaded != null)
                flags = loaded;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load cached flags.json; using defaults until the next fetch");
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var json = await Http.GetStringAsync(RemoteUrl);
            var loaded = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json);
            if (loaded == null)
                return;

            File.WriteAllText(path, json);
            _ = Plugin.Framework.RunOnFrameworkThread(() => flags = loaded);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to fetch flags.json; keeping the last known flags");
        }
    }
}
