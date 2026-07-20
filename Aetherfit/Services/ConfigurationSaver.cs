using System;
using Dalamud.Plugin.Services;

namespace Aetherfit.Services;

// Coalesces Configuration.Save() bursts into one disk write. Saves land after a short quiet period
// (bounded by a max latency so steady activity can't postpone them forever) and always on dispose,
// so at most the last few seconds of changes are at risk in a crash. Runs entirely on the framework
// thread — the same thread that mutates the config — so serialization never races a mutation.
public sealed class ConfigurationSaver : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxLatency = TimeSpan.FromSeconds(10);

    private readonly Configuration configuration;
    private bool dirty;
    private DateTime firstDirtyUtc;
    private DateTime lastRequestUtc;

    public ConfigurationSaver(Configuration configuration)
    {
        this.configuration = configuration;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Request()
    {
        var now = DateTime.UtcNow;
        if (!dirty)
            firstDirtyUtc = now;
        dirty = true;
        lastRequestUtc = now;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!dirty)
            return;

        var now = DateTime.UtcNow;
        if (now - lastRequestUtc >= QuietPeriod || now - firstDirtyUtc >= MaxLatency)
            SaveNow();
    }

    public void SaveNow()
    {
        dirty = false;
        Plugin.PluginInterface.SavePluginConfig(configuration);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (dirty)
            SaveNow();
    }
}
