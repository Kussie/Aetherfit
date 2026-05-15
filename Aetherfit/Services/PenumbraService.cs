using System;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Aetherfit.Services;

public sealed class PenumbraService
{
    private readonly OpenMainWindow openMainWindow;

    public PenumbraService()
    {
        openMainWindow = new OpenMainWindow(Plugin.PluginInterface);
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
}
