using System;
using Dalamud.Plugin.Services;

namespace Aetherfit.Services;

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
