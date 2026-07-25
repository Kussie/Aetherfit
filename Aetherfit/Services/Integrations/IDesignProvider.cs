using System;
using System.Collections.Generic;
using Aetherfit.Utils;

namespace Aetherfit.Services.Integrations;

// Glamourer = 0 is deliberate: every CachedOutfit written before this field existed deserializes
// as Glamourer with no migration needed.
public enum DesignSource
{
    Glamourer = 0,
    Glamaholic = 1,
    GlamourPlate = 2,
}

[Flags]
public enum DesignProviderCapabilities
{
    None             = 0,
    Apply            = 1 << 0,
    Layers           = 1 << 1, // usable as an Additional-Design-Layers base or layer
    Customizations   = 1 << 2,
    Mods             = 1 << 3,
    DesignLinks      = 1 << 4,
    EditableMetadata = 1 << 5, // Description/Tags can be pushed back into the source's own file
}

// Provider-neutral mirror of Glamourer.Api.Enums.StateFinalizationType.
public enum DesignFinalizationType
{
    Applied,
    Reverted,
    Other,
}

public sealed record ProviderDesignInfo(Guid NativeId, string DisplayName, string FullPath, uint Color);

public sealed record ProviderDesignListResult(IReadOnlyList<ProviderDesignInfo> Designs, string? Error);

// Every method here operates in the provider's own native id space - the same shape
// GlamourerService's existing methods already use. Resolution to an Aetherfit-owned Guid only
// happens once, at the MainWindow.RefreshDesigns boundary (see DesignIdentity).
public interface IDesignProvider : IDisposable
{
    DesignSource Source { get; }
    string DisplayName { get; }
    DesignProviderCapabilities Capabilities { get; }

    PluginIntegrationInfo CheckIntegration();

    ProviderDesignListResult FetchDesignList();
    CachedOutfit? FetchDesignMetadata(Guid nativeId);
    bool Apply(Guid nativeId, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false);
    void ApplyLayer(Guid nativeId);
    void OpenInNativeUi(Guid nativeId, string designName);
    void Revert();

    event Action<nint, DesignFinalizationType>? OnExternalStateFinalized;
    event Action<nint, DesignFinalizationType>? OnAnyStateFinalized;
}
