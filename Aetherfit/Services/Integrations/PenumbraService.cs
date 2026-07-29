using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Utils;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Aetherfit.Services.Integrations;

public sealed class PenumbraService
{
    private readonly OpenMainWindow openMainWindow;
    private readonly GetChangedItems getChangedItems;
    private readonly SetTemporaryModSettingsPlayer setTemporaryModSettingsPlayer;
    private readonly RemoveTemporaryModSettingsPlayer removeTemporaryModSettingsPlayer;
    private readonly RemoveAllTemporaryModSettingsPlayer removeAllTemporaryModSettingsPlayer;
    private readonly RedrawObject redrawObject;

    // A mod changes the same items no matter which design pulls it in, so we look them up once per mod
    // directory and reuse that across every design.
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> changedItemsCache = new();

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    public PenumbraService()
    {
        openMainWindow = new OpenMainWindow(Plugin.PluginInterface);
        getChangedItems = new GetChangedItems(Plugin.PluginInterface);
        setTemporaryModSettingsPlayer = new SetTemporaryModSettingsPlayer(Plugin.PluginInterface);
        removeTemporaryModSettingsPlayer = new RemoveTemporaryModSettingsPlayer(Plugin.PluginInterface);
        removeAllTemporaryModSettingsPlayer = new RemoveAllTemporaryModSettingsPlayer(Plugin.PluginInterface);
        redrawObject = new RedrawObject(Plugin.PluginInterface);
    }

    // Penumbra's own reported ApiVersion (BreakingVersion/FeatureVersion in its PenumbraApi.cs) - confirmed
    // against Penumbra's stable branch source. This happens to match the Penumbra.Api package version below
    // it today, but the two are versioned independently by Penumbra's own author; don't assume that holds
    // after bumping the package reference - re-check the actual value in Penumbra's source.
    public static readonly (int Major, int Minor) MinApiVersion = (5, 15);

    public PluginIntegrationInfo CheckIntegration()
    {
        if (PluginIntegrationCheck.CheckInstalledAndLoaded("Penumbra", out var exposed) is { } early)
            return early;

        try
        {
            var (breaking, features) = new ApiVersion(Plugin.PluginInterface).Invoke();
            var ok = breaking == MinApiVersion.Major && features >= MinApiVersion.Minor;
            return new PluginIntegrationInfo(ok ? PluginIntegrationStatus.Ok : PluginIntegrationStatus.VersionTooLow, exposed!.Version, (breaking, features));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Penumbra API version");
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed!.Version, null);
        }
    }

    public void OpenMod(string modDirectory, string modName)
    {
        try
        {
            openMainWindow.Invoke(TabType.Mods, modDirectory ?? string.Empty, modName ?? string.Empty);
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Penumbra is not available — cannot open mod.", "Failed to open Penumbra to mod {Dir} / {Name}", modDirectory, modName);
        }
    }

    /// <summary>
    /// The names of the items a mod changes, straight from Penumbra. This is everything the mod could
    /// touch across all its options, not just what a given design picked, so we can only point at the
    /// whole mod rather than a specific option. Empty set if Penumbra isn't around or the mod changes nothing.
    /// </summary>
    public IReadOnlySet<string> GetChangedItemNames(string directory, string name)
    {
        if (string.IsNullOrEmpty(directory))
            return EmptySet;

        return changedItemsCache.GetOrAdd(directory, _ =>
        {
            try
            {
                var changed = getChangedItems.Invoke(directory, name ?? string.Empty);
                if (changed == null || changed.Count == 0)
                    return EmptySet;
                return changed.Keys.ToHashSet();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to query changed items for mod {Dir} / {Name}", directory, name);
                return EmptySet;
            }
        });
    }

    public void ClearChangedItemsCache() => changedItemsCache.Clear();

    // Temporary mod settings, scoped to the local player and a caller-owned lock key (see
    // SimpleGlamourSwitcherService's own key range) - the same relay mechanism SGS itself uses to
    // activate a design's mods, since Glamourer's own apply never touches Penumbra mod state at all.
    public PenumbraApiEc SetTemporaryModSettingsPlayer(string modDirectory, bool enabled, int priority,
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings, string source, int key)
    {
        try
        {
            return setTemporaryModSettingsPlayer.Invoke(0, modDirectory, false, enabled, priority, settings, source, key);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to set temporary mod settings for {Dir} ({Key})", modDirectory, key);
            return PenumbraApiEc.UnknownError;
        }
    }

    public PenumbraApiEc RemoveTemporaryModSettingsPlayer(string modDirectory, int key)
    {
        try
        {
            return removeTemporaryModSettingsPlayer.Invoke(0, modDirectory, key);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to remove temporary mod settings for {Dir} ({Key})", modDirectory, key);
            return PenumbraApiEc.UnknownError;
        }
    }

    public PenumbraApiEc RemoveAllTemporaryModSettingsPlayer(int key)
    {
        try
        {
            return removeAllTemporaryModSettingsPlayer.Invoke(0, key);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to remove all temporary mod settings for key {Key}", key);
            return PenumbraApiEc.UnknownError;
        }
    }

    public void RedrawLocalPlayer()
    {
        try
        {
            redrawObject.Invoke(0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to redraw local player");
        }
    }
}
