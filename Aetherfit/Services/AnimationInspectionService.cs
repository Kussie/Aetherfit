using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Aetherfit.Services;

// Reads the local player's action timeline state (the animations currently playing) out of the game's
// character struct. Kept static and self-contained so this is the only file touching the unsafe layout.
internal static class AnimationInspectionService
{
    public readonly record struct TimelineSlotInfo(int Slot, ushort TimelineId, float Speed);

    public sealed record TimelineSnapshot(
        List<TimelineSlotInfo> ActiveSlots,
        ushort BaseOverride,
        ushort LipsOverride,
        float OverallSpeed);

    // The sequencer's well-known slot roles; slots without an agreed name are shown numerically.
    private static readonly Dictionary<int, string> SlotLabels = new()
    {
        [0] = "Base",
        [1] = "UpperBody",
        [2] = "Facial",
        [3] = "Add",
        [7] = "Lips",
    };

    public static string SlotLabel(int slot)
        => SlotLabels.TryGetValue(slot, out var label) ? $"{label} (slot {slot})" : $"Slot {slot}";

    // Returns null when there is no local player to inspect.
    public static unsafe TimelineSnapshot? ReadLocalPlayerTimelines()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return null;

        var chara = (Character*)localPlayer.Address;
        ref var timeline = ref chara->Timeline;
        ref var sequencer = ref timeline.TimelineSequencer;

        var active = new List<TimelineSlotInfo>();
        for (var slot = 0; slot < sequencer.TimelineIds.Length; slot++)
        {
            var id = sequencer.TimelineIds[slot];
            if (id != 0)
                active.Add(new TimelineSlotInfo(slot, id, sequencer.TimelineSpeeds[slot]));
        }

        return new TimelineSnapshot(active, timeline.BaseOverride, timeline.LipsOverride, timeline.OverallSpeed);
    }
}
