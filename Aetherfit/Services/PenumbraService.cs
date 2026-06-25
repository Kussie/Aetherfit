using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Aetherfit.Services;

public sealed class PenumbraService
{
    private readonly OpenMainWindow openMainWindow;
    private readonly GetChangedItems getChangedItems;

    // A mod changes the same items no matter which design pulls it in, so we look them up once per mod
    // directory and reuse that across every design.
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> changedItemsCache = new();

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    public PenumbraService()
    {
        openMainWindow = new OpenMainWindow(Plugin.PluginInterface);
        getChangedItems = new GetChangedItems(Plugin.PluginInterface);
    }

    public void OpenMod(string modDirectory, string modName)
    {
        try
        {
            openMainWindow.Invoke(TabType.Mods, modDirectory ?? string.Empty, modName ?? string.Empty);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError("[Aetherfit] Penumbra is not available — cannot open mod.");
            Plugin.Log.Warning(ex, "Failed to open Penumbra to mod {Dir} / {Name}", modDirectory, modName);
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
}
