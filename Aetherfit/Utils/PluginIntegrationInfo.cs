using System;
using System.Linq;
using Dalamud.Plugin;

namespace Aetherfit.Utils;

public enum PluginIntegrationStatus
{
    NotInstalled,
    NotLoaded,
    VersionTooLow,
    Ok,
}

public sealed record PluginIntegrationInfo(PluginIntegrationStatus Status, Version? PluginVersion, (int Major, int Minor)? ApiVersion);

public static class PluginIntegrationCheck
{
    // Null means "installed and loaded, caller continues its own check"; non-null is an early-out result.
    public static PluginIntegrationInfo? CheckInstalledAndLoaded(string internalName, out IExposedPlugin? exposed)
    {
        exposed = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == internalName);
        if (exposed == null)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotInstalled, null, null);
        if (!exposed.IsLoaded)
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed.Version, null);
        return null;
    }
}
