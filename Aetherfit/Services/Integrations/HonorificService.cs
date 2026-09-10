using System;
using Aetherfit.Utils;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;

namespace Aetherfit.Services.Integrations;

// Honorific has no official Api NuGet package (unlike Glamourer.Api/Penumbra.Api), so this talks to it
// via Dalamud's raw named-IPC mechanism directly, the same pattern CustomizePlusService already uses.
// IPC names/shapes confirmed against Honorific's own source (github.com/Caraxi/Honorific,
// IpcProvider.cs, namespace "Honorific", major version 3).
//
// A persona's "name" is a title (prefix/suffix shown next to the nameplate name), not an actual engine-
// level name replacement - that isn't achievable client-side at all. TitleData's JSON shape has more
// fields (IsOriginal, Color, Glow, Color3, GradientColourSet, GradientAnimationStyle) than Aetherfit
// sets - only Title/IsPrefix are included here, the rest default sanely on Honorific's own end.
public sealed class HonorificService
{
    private readonly record struct TitleData(string Title, bool IsPrefix);

    private readonly ICallGateSubscriber<(uint Major, uint Minor)> getApiVersion;
    private readonly ICallGateSubscriber<int, string, object> setCharacterTitle;
    private readonly ICallGateSubscriber<int, object> clearCharacterTitle;

    public HonorificService()
    {
        getApiVersion = Plugin.PluginInterface.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");
        setCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        clearCharacterTitle = Plugin.PluginInterface.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
    }

    public static readonly (int Major, int Minor) MinApiVersion = (3, 0);

    public PluginIntegrationInfo CheckIntegration()
    {
        if (PluginIntegrationCheck.CheckInstalledAndLoaded("Honorific", out var exposed) is { } early)
            return early;

        try
        {
            var (major, minor) = getApiVersion.InvokeFunc();
            var ok = major == MinApiVersion.Major && minor >= MinApiVersion.Minor;
            return new PluginIntegrationInfo(ok ? PluginIntegrationStatus.Ok : PluginIntegrationStatus.VersionTooLow, exposed!.Version, ((int)major, (int)minor));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Honorific API version");
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed!.Version, null);
        }
    }

    // Also doubles as "is the plugin even installed" - GetIpcSubscriber never throws, only InvokeFunc
    // does when nothing is listening.
    public bool IsReady()
    {
        try
        {
            var (major, _) = getApiVersion.InvokeFunc();
            return major == 3;
        }
        catch
        {
            return false;
        }
    }

    // 0 = the local player in Dalamud's object-index convention, matching every other Apply-time IPC
    // call in this codebase.
    public bool SetTitle(string title, bool isPrefix)
    {
        if (!IsReady())
            return false;

        try
        {
            var json = JsonConvert.SerializeObject(new TitleData(title, isPrefix));
            setCharacterTitle.InvokeAction(0, json);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to set Honorific title \"{Title}\"", title);
            return false;
        }
    }

    public bool ClearTitle()
    {
        if (!IsReady())
            return false;

        try
        {
            clearCharacterTitle.InvokeAction(0);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to clear Honorific title");
            return false;
        }
    }
}
