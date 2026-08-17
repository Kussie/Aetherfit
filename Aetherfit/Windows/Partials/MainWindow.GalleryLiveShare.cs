using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Glamourer.Api.Enums;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private void OpenShareLiveDialog() =>
        plugin.ShareLiveWindow.Show(HasAnyFilter, CollectVisibleDesignIds());

    private void OpenReceiveLiveDialog() =>
        plugin.ReceiveLiveWindow.Show();

    private string importDesignCode = string.Empty;
    private string? importDesignCodeError;
    private bool importDesignCodePopupRequested;

    private void OpenImportDesignCodeDialog()
    {
        importDesignCode = string.Empty;
        importDesignCodeError = null;
        importDesignCodePopupRequested = true;
    }

    // The "Open Shared Gallery" dropdown: a local file, a live pull from another player, or a pasted design code.
    private void DrawOpenGalleryPopup()
    {
        using var popup = ImRaii.Popup("##openGalleryPopup");
        if (!popup.Success)
            return;

        var galleryBusy = plugin.GallerySharing.IsBusy;
        using (ImRaii.Disabled(galleryBusy))
        {
            if (ImGui.Selectable("Gallery from File..."))
                OpenImportGalleryDialog();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(galleryBusy
                ? "An export or import is already running."
                : "Open another user's exported .afgallery file in a read-only viewer.");

        ImGui.Separator();

        var liveSharingEnabled = plugin.FeatureFlags.EnableLiveSharing;

        // Not gated on IsBusy: if a receive is already running, clicking this just brings the existing
        // window back to the front instead of starting a new one.
        using (ImRaii.Disabled(!liveSharingEnabled))
        {
            if (ImGui.Selectable("Gallery from Live Share..."))
                OpenReceiveLiveDialog();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!liveSharingEnabled
                ? "Live sharing is temporarily disabled."
                : plugin.LiveShare.IsBusy
                    ? "Reopen the receive in progress."
                    : "Receive a gallery directly from another online player.");

        ImGui.Separator();

        if (ImGui.Selectable("Design from Code..."))
            OpenImportDesignCodeDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Paste a single design code (gear only) shared by another Aetherfit user.");
    }

    private void DrawImportDesignCodePopup()
    {
        if (importDesignCodePopupRequested)
        {
            importDesignCodePopupRequested = false;
            ImGui.OpenPopup("##importDesignCode");
        }

        using var popup = ImRaii.Popup("##importDesignCode");
        if (!popup.Success)
            return;

        ImGui.TextDisabled("Paste a design code below. Only gear comes across - tags, description");
        ImGui.TextDisabled("and customizations aren't included.");
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(350 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##importDesignCodeInput", "Paste code here...", ref importDesignCode, 8192,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (importDesignCodeError != null)
            ImGui.TextColored(UiTheme.ErrorText, importDesignCodeError);

        if ((submitted || ImGui.Button("Import")) && !string.IsNullOrWhiteSpace(importDesignCode))
            DoImportDesignCode();
    }

    private void DoImportDesignCode()
    {
        if (!DesignShareCode.TryDecode(importDesignCode, out var name, out var equipment, out var error))
        {
            importDesignCodeError = error;
            return;
        }

        var (stateResult, state) = plugin.Glamourer.GetState();
        if (stateResult != GlamourerApiEc.Success || state == null)
        {
            importDesignCodeError = $"Couldn't read current Glamourer state ({stateResult}).";
            return;
        }

        var designJson = GlamourerJsonSchema.BuildEquipmentOnlyDesign(state, GlamourerJsonSchema.BuildEquipmentSection(equipment!));
        var (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name!);
        if (addResult != GlamourerApiEc.Success)
        {
            importDesignCodeError = addResult.ToString();
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Imported \"{name}\" from a design code.");
        selectedDesign = newId;
        RefreshDesigns();
        ImGui.CloseCurrentPopup();
    }
}
