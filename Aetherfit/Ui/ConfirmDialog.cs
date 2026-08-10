using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Ui;

internal static class ConfirmDialog
{
    // Base (unfilled) and fill colors for the hold-to-confirm button - a plain red button is too easy
    // to misclick on a destructive action, so the confirm itself has to be held down to fill.
    private static readonly Vector4 HoldBase = new(0.28f, 0.10f, 0.10f, 1f);
    private static readonly Vector4 HoldFill = new(0.82f, 0.16f, 0.16f, 1f);
    private const float HoldSeconds = 1.1f;

    // Per-popup fill progress, keyed by popup id. Reset whenever the popup (re)opens.
    private static readonly Dictionary<string, float> HoldProgress = new();

    public static void Open(string popupId) => ImGui.OpenPopup(popupId);

    public static bool Draw(string popupId, string message, string confirmLabel = "Confirm", bool holdToConfirm = false)
    {
        var confirmed = false;

        ImGui.SetNextWindowSize(new Vector2(420, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        using var modal = ImRaii.PopupModal(popupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return false;

        if (ImGui.IsWindowAppearing())
            HoldProgress.Remove(popupId);

        ImGui.TextWrapped(message);
        ImGui.Spacing();

        if (holdToConfirm)
        {
            HoldProgress.TryGetValue(popupId, out var progress);
            if (DrawHoldButton(popupId, confirmLabel, ref progress))
            {
                confirmed = true;
                HoldProgress.Remove(popupId);
                ImGui.CloseCurrentPopup();
            }
            else
            {
                HoldProgress[popupId] = progress;
            }
        }
        else if (ImGui.Button(confirmLabel))
        {
            confirmed = true;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            HoldProgress.Remove(popupId);
            ImGui.CloseCurrentPopup();
        }

        return confirmed;
    }

    // A button that has to be held down (not just clicked) to actually trigger, with a red fill
    // sweeping across it as a visual progress bar - a second, deliberate confirmation for the most
    // destructive actions rather than a single misclickable button.
    private static bool DrawHoldButton(string popupId, string label, ref float progress)
    {
        var style = ImGui.GetStyle();
        var textSize = ImGui.CalcTextSize(label);
        var size = new Vector2(System.Math.Max(textSize.X + style.FramePadding.X * 4, 160f), ImGui.GetFrameHeight());

        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##hold{popupId}", size);
        var held = ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();

        var dt = ImGui.GetIO().DeltaTime;
        progress = held
            ? System.Math.Min(1f, progress + dt / HoldSeconds)
            : System.Math.Max(0f, progress - dt / HoldSeconds * 2f);

        var dl = ImGui.GetWindowDrawList();
        var rounding = style.FrameRounding;
        dl.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(HoldBase), rounding);
        if (progress > 0f)
        {
            dl.PushClipRect(pos, pos + new Vector2(size.X * progress, size.Y), true);
            dl.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(HoldFill), rounding);
            dl.PopClipRect();
        }
        if (hovered)
            dl.AddRect(pos, pos + size, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.35f)), rounding);

        var textPos = pos + (size - textSize) / 2;
        dl.AddText(textPos, ImGui.ColorConvertFloat4ToU32(Vector4.One), label);

        if (hovered && progress < 1f)
            ImGui.SetTooltip("Hold to confirm");

        return progress >= 1f;
    }
}
