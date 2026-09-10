using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Utils;
using Dalamud.Plugin.Ipc;

namespace Aetherfit.Services.Integrations;

// Customize+ has no official Api NuGet package (unlike Glamourer.Api/Penumbra.Api), so this talks to it
// via Dalamud's raw named-IPC mechanism directly - the same underlying mechanism those packages are
// themselves thin wrappers over. IPC names/shapes confirmed against SimpleGlamourSwitcher's own
// CustomizePlus.cs (github.com/Caraxi/SimpleGlamourSwitcher/blob/main/SimpleGlamourSwitcher/IPC/CustomizePlus.cs),
// which talks to the same plugin.
//
// Used for SimpleGlamourSwitcherService's per-outfit template toggling (enable/disable named templates
// within whichever profile is already active for the local player) and for PersonaApplyService's
// whole-profile switching (GetProfiles/EnableProfile/DisableProfile).
public sealed class CustomizePlusService
{
    // Matches Customize+'s own IPC tuple shape exactly - the in-process IPC call requires the generic
    // signature to line up, so every element has to be present even though only UniqueId/Name/IsEnabled
    // are actually used here.
    public readonly record struct TemplateBone(string Name, Vector3 Translation, Vector3 Rotation, Vector3 Scale,
        bool PropagateTranslation, bool PropagateRotation, bool PropagateScale);

    public readonly record struct TemplateStatus(Guid UniqueId, string Name, List<TemplateBone> Bones, bool IsEnabled);

    // Matches Customize+'s own Profile.GetList IPC tuple shape exactly, same reasoning as TemplateBone
    // above - CharacterType/WorldId/CharacterSubType are part of the wire shape but unused here.
    public readonly record struct ProfileCharacter(string Name, byte CharacterType, ushort WorldId, ushort CharacterSubType);
    public readonly record struct ProfileInfo(Guid UniqueId, string Name, string FullPath, List<ProfileCharacter> Characters, int Priority, bool Enabled);

    private readonly ICallGateSubscriber<(int Breaking, int Feature)> getApiVersion;
    private readonly ICallGateSubscriber<ushort, (int ErrorCode, Guid? ActiveProfile)> getActiveProfileIdOnCharacter;
    private readonly ICallGateSubscriber<Guid, (int ErrorCode, List<TemplateStatus> Templates)> getTemplates;
    private readonly ICallGateSubscriber<Guid, Guid, int> enableTemplateByUniqueId;
    private readonly ICallGateSubscriber<Guid, Guid, int> disableTemplateByUniqueId;
    private readonly ICallGateSubscriber<IList<ProfileInfo>> getProfileList;
    private readonly ICallGateSubscriber<Guid, int> enableProfileByUniqueId;
    private readonly ICallGateSubscriber<Guid, int> disableProfileByUniqueId;

    public CustomizePlusService()
    {
        getApiVersion = Plugin.PluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        getActiveProfileIdOnCharacter = Plugin.PluginInterface.GetIpcSubscriber<ushort, (int, Guid?)>("CustomizePlus.Profile.GetActiveProfileIdOnCharacter");
        getTemplates = Plugin.PluginInterface.GetIpcSubscriber<Guid, (int, List<TemplateStatus>)>("CustomizePlus.Profile.GetTemplates");
        enableTemplateByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, Guid, int>("CustomizePlus.Profile.EnableTemplateByUniqueId");
        disableTemplateByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, Guid, int>("CustomizePlus.Profile.DisableTemplateByUniqueId");
        getProfileList = Plugin.PluginInterface.GetIpcSubscriber<IList<ProfileInfo>>("CustomizePlus.Profile.GetList");
        enableProfileByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.EnableByUniqueId");
        disableProfileByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DisableByUniqueId");
    }

    public static readonly (int Major, int Minor) MinApiVersion = (6, 1);

    public PluginIntegrationInfo CheckIntegration()
    {
        if (PluginIntegrationCheck.CheckInstalledAndLoaded("CustomizePlus", out var exposed) is { } early)
            return early;

        try
        {
            var (breaking, feature) = getApiVersion.InvokeFunc();
            var ok = breaking == MinApiVersion.Major && feature >= MinApiVersion.Minor;
            return new PluginIntegrationInfo(ok ? PluginIntegrationStatus.Ok : PluginIntegrationStatus.VersionTooLow, exposed!.Version, (breaking, feature));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Customize+ API version");
            return new PluginIntegrationInfo(PluginIntegrationStatus.NotLoaded, exposed!.Version, null);
        }
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

    // Every user-created profile a persona could reference - id + name only, ignoring priority/enabled/
    // character-association fields that don't matter for a persona picker.
    public IReadOnlyList<(Guid Id, string Name)> GetProfiles()
    {
        try
        {
            return getProfileList.InvokeFunc().Select(p => (p.UniqueId, p.Name)).ToList();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to query Customize+ profile list");
            return Array.Empty<(Guid, string)>();
        }
    }

    // Distinct names from SetTemplateEnabled above - these switch a whole profile's enabled state
    // (used by a persona apply), not a template within an already-active one.
    public bool EnableProfile(Guid profileId)
    {
        try
        {
            return enableProfileByUniqueId.InvokeFunc(profileId) == 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to enable Customize+ profile {Profile}", profileId);
            return false;
        }
    }

    public bool DisableProfile(Guid profileId)
    {
        try
        {
            return disableProfileByUniqueId.InvokeFunc(profileId) == 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to disable Customize+ profile {Profile}", profileId);
            return false;
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
