using System;

namespace Aetherfit.Utils;

public enum PluginIntegrationStatus
{
    NotInstalled,
    NotLoaded,
    VersionTooLow,
    Ok,
}

public sealed record PluginIntegrationInfo(PluginIntegrationStatus Status, Version? PluginVersion, (int Major, int Minor)? ApiVersion);
