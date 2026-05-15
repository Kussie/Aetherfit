using System;
using System.Runtime.Versioning;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Aetherfit.Services;

[SupportedOSPlatform("windows")]
internal static class Sounds
{
    private const int ApplySoundId = 9;
    private const int RevertSoundId = 8;
    private const int CaptureSoundId = 13;

    public static void PlayApply()   => Play(ApplySoundId);
    public static void PlayRevert()  => Play(RevertSoundId);
    public static void PlayCapture() => Play(CaptureSoundId);

    private static unsafe void Play(int soundId)
    {
        try
        {
            var mgr = RaptureAtkUnitManager.Instance();
            if (mgr == null) return;
            var addon = mgr->GetAddonByName("NamePlate", 1);
            if (addon == null) return;
            addon->PlaySoundEffect(soundId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to play UI sound {Id}", soundId);
        }
    }
}
