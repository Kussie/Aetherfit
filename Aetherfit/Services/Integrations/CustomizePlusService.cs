using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace Aetherfit.Services.Integrations;

// Customize+ has no official Api NuGet package (unlike Glamourer.Api/Penumbra.Api), so this talks to it
// via Dalamud's raw named-IPC mechanism directly - the same underlying mechanism those packages are
// themselves thin wrappers over. IPC names/shapes confirmed against SimpleGlamourSwitcher's own
// CustomizePlus.cs (github.com/Caraxi/SimpleGlamourSwitcher/blob/main/SimpleGlamourSwitcher/IPC/CustomizePlus.cs),
// which talks to the same plugin.
//
// Only used for SimpleGlamourSwitcherService's per-outfit template toggling - Aetherfit never assigns or
// switches a Customize+ profile itself, only enables/disables named templates within whichever profile
// is already active for the local player.
public sealed class CustomizePlusService
{
    // Matches Customize+'s own IPC tuple shape exactly - the in-process IPC call requires the generic
    // signature to line up, so every element has to be present even though only UniqueId/Name/IsEnabled
    // are actually used here.
    public readonly record struct TemplateBone(string Name, Vector3 Translation, Vector3 Rotation, Vector3 Scale,
        bool PropagateTranslation, bool PropagateRotation, bool PropagateScale);

    public readonly record struct TemplateStatus(Guid UniqueId, string Name, List<TemplateBone> Bones, bool IsEnabled);

    private readonly ICallGateSubscriber<(int Breaking, int Feature)> getApiVersion;
    private readonly ICallGateSubscriber<ushort, (int ErrorCode, Guid? ActiveProfile)> getActiveProfileIdOnCharacter;
    private readonly ICallGateSubscriber<Guid, (int ErrorCode, List<TemplateStatus> Templates)> getTemplates;
    private readonly ICallGateSubscriber<Guid, Guid, int> enableTemplateByUniqueId;
    private readonly ICallGateSubscriber<Guid, Guid, int> disableTemplateByUniqueId;

    public CustomizePlusService()
    {
        getApiVersion = Plugin.PluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        getActiveProfileIdOnCharacter = Plugin.PluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        getTemplates = Plugin.PluginInterface.GetIpcSubscriber<Guid, (int, List<TemplateStatus>)>("CustomizePlus.Profile.GetTemplates");
        enableTemplateByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, Guid, int>("CustomizePlus.Profile.EnableTemplateByUniqueId");
        disableTemplateByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, Guid, int>("CustomizePlus.Profile.DisableTemplateByUniqueId");
    }

    // Same version gate Customize+'s own consumers use (SGS's IsReady() checks Breaking == 6). Also
    // doubles as "is the plugin even installed" - GetIpcSubscriber never throws, only InvokeFunc does
    // when nothing is listening.
    public bool IsReady()
    {
        try
        {
            var (breaking, feature) = getApiVersion.InvokeFunc();
            return breaking == 6 && feature >= 1;
        }
        catch
        {
            return false;
        }
    }

    // 0 = the local player in Dalamud's object-index convention, matching every other Apply-time IPC
    // call in this codebase.
    public Guid? GetActiveProfileOnLocalPlayer()
    {
        if (!IsReady())
            return null;

        try
        {
            var (errorCode, activeProfile) = getActiveProfileIdOnCharacter.InvokeFunc(0);
            return errorCode == 0 && activeProfile is { } id && id != Guid.Empty ? id : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Customize+ active profile");
            return null;
        }
    }

    public IReadOnlyList<TemplateStatus> GetTemplates(Guid profile)
    {
        try
        {
            var (errorCode, templates) = getTemplates.InvokeFunc(profile);
            return errorCode == 0 ? templates : Array.Empty<TemplateStatus>();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Customize+ templates for profile {Profile}", profile);
            return Array.Empty<TemplateStatus>();
        }
    }

    // Returns whether the call succeeded (error code 0), so the caller can decide whether to remember
    // this template for a later revert.
    public bool SetTemplateEnabled(Guid profile, Guid templateId, bool enable)
    {
        try
        {
            var errorCode = enable ? enableTemplateByUniqueId.InvokeFunc(profile, templateId) : disableTemplateByUniqueId.InvokeFunc(profile, templateId);
            return errorCode == 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to {Action} Customize+ template {Template} on profile {Profile}", enable ? "enable" : "disable", templateId, profile);
            return false;
        }
    }
}
