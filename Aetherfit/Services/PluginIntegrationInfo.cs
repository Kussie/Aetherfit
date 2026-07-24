using System;

namespace Aetherfit.Services;

public enum PluginIntegrationStatus
{
    NotInstalled,
    NotLoaded,
    VersionTooLow,
    Ok,
}

public sealed record PluginIntegrationInfo(PluginIntegrationStatus Status, Version? PluginVersion, (int Major, int Minor)? ApiVersion);
