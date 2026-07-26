using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Utils;
using Glamourer.Api.Enums;

namespace Aetherfit.Services.Integrations;

// Thin IDesignProvider adapter over GlamourerService. GlamourerService keeps its own richer public
// surface (ApplyLayer, OpenInGlamourer, Revert, events) for the several call sites that are
// genuinely Glamourer-specific and don't generalize - this wrapper only exists so the refresh/apply
// pipeline can treat Glamourer as one of several providers.
public sealed class GlamourerDesignProvider : IDesignProvider
{
    private readonly GlamourerService glamourer;

    // Needed to remove the exact same translating delegate that was added for a given subscriber.
    private readonly Dictionary<Action<nint, DesignFinalizationType>, Action<nint, StateFinalizationType>> externalHandlers = new();
    private readonly Dictionary<Action<nint, DesignFinalizationType>, Action<nint, StateFinalizationType>> anyHandlers = new();

    public GlamourerDesignProvider(GlamourerService glamourer) => this.glamourer = glamourer;

    public DesignSource Source => DesignSource.Glamourer;
    public string DisplayName => "Glamourer";

    public DesignProviderCapabilities Capabilities =>
        DesignProviderCapabilities.Apply | DesignProviderCapabilities.Layers |
        DesignProviderCapabilities.Customizations | DesignProviderCapabilities.Mods |
        DesignProviderCapabilities.DesignLinks | DesignProviderCapabilities.EditableMetadata;

    public PluginIntegrationInfo CheckIntegration() => glamourer.CheckIntegration();

    public ProviderDesignListResult FetchDesignList()
    {
        var result = glamourer.FetchDesignList();
        var designs = result.Designs
            .Select(d => new ProviderDesignInfo(d.Id, d.DisplayName, d.FullPath, d.Color))
            .ToList();
        return new ProviderDesignListResult(designs, result.Error);
    }

    public CachedOutfit? FetchDesignMetadata(Guid nativeId) => glamourer.FetchDesignMetadata(nativeId);

    public bool Apply(Guid nativeId, string designName, IReadOnlyList<string>? layerNames = null, bool quiet = false)
        => glamourer.Apply(nativeId, designName, layerNames, quiet);

    public void ApplyLayer(Guid nativeId) => glamourer.ApplyLayer(nativeId);

    public void OpenInNativeUi(Guid nativeId, string designName) => glamourer.OpenInGlamourer(nativeId, designName);

    public void Revert() => glamourer.Revert();

    public string? GetNativeImagePath(Guid nativeId) => null;

    public event Action<nint, DesignFinalizationType>? OnExternalStateFinalized
    {
        add
        {
            if (value == null) return;
            void Handler(nint actor, StateFinalizationType type) => value(actor, Translate(type));
            externalHandlers[value] = Handler;
            glamourer.OnExternalStateFinalized += Handler;
        }
        remove
        {
            if (value == null || !externalHandlers.Remove(value, out var handler)) return;
            glamourer.OnExternalStateFinalized -= handler;
        }
    }

    public event Action<nint, DesignFinalizationType>? OnAnyStateFinalized
    {
        add
        {
            if (value == null) return;
            void Handler(nint actor, StateFinalizationType type) => value(actor, Translate(type));
            anyHandlers[value] = Handler;
            glamourer.OnAnyStateFinalized += Handler;
        }
        remove
        {
            if (value == null || !anyHandlers.Remove(value, out var handler)) return;
            glamourer.OnAnyStateFinalized -= handler;
        }
    }

    private static DesignFinalizationType Translate(StateFinalizationType type) => type switch
    {
        StateFinalizationType.DesignApplied => DesignFinalizationType.Applied,
        StateFinalizationType.Revert or StateFinalizationType.RevertCustomize
            or StateFinalizationType.RevertEquipment or StateFinalizationType.RevertAdvanced
            or StateFinalizationType.RevertAutomation => DesignFinalizationType.Reverted,
        _ => DesignFinalizationType.Other,
    };

    // GlamourerService's lifecycle is owned by Plugin directly (Plugin.Glamourer) - nothing to
    // dispose here.
    public void Dispose() { }
}
