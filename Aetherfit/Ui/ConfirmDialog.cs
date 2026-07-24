using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Ui;

internal static class ConfirmDialog
{
    public static void Open(string popupId) => ImGui.OpenPopup(popupId);

    public static bool Draw(string popupId, string message, string confirmLabel = "Confirm")
    {
        var confirmed = false;

        ImGui.SetNextWindowSize(new Vector2(420, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        using var modal = ImRaii.PopupModal(popupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return false;

        ImGui.TextWrapped(message);
        ImGui.Spacing();

        if (ImGui.Button(confirmLabel))
        {
            confirmed = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        return confirmed;
    }
}
