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

    private string importDesignInput = string.Empty;
    private string? importDesignInputError;
    private bool importDesignPopupRequested;

    private void OpenImportDesignDialog()
    {
        importDesignInput = string.Empty;
        importDesignInputError = null;
        plugin.EorzeaCollection.Reset();
        importDesignPopupRequested = true;
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

        if (ImGui.Selectable("Design from Code / Eorzea Collection..."))
            OpenImportDesignDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Paste a design code (gear only) shared by another Aetherfit user, or an Eorzea Collection glamour link.");
    }

    // One dialog for both "paste a design code" and "paste an Eorzea Collection link" - detected from
    // the pasted text itself rather than asking the user to pick which kind of input they have.
    private void DrawImportDesignPopup()
    {
        if (importDesignPopupRequested)
        {
            importDesignPopupRequested = false;
            ImGui.OpenPopup("##importDesign");
        }

        using var popup = ImRaii.Popup("##importDesign");
        if (!popup.Success)
            return;

        ImGui.TextDisabled("Paste a design code or an Eorzea Collection glamour link below. Only gear");
        ImGui.TextDisabled("(and facewear) comes across - tags, description and customizations aren't included.");
        ImGui.Spacing();

        var fetching = plugin.EorzeaCollection.Phase == EorzeaCollectionImportPhase.Fetching;
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(350 * ImGuiHelpers.GlobalScale);
        bool submitted;
        using (ImRaii.Disabled(fetching))
            submitted = ImGui.InputTextWithHint("##importDesignInput", "Paste code or link here...", ref importDesignInput, 8192,
                ImGuiInputTextFlags.EnterReturnsTrue);

        var error = importDesignInputError
            ?? (plugin.EorzeaCollection.Phase == EorzeaCollectionImportPhase.Error ? plugin.EorzeaCollection.ErrorMessage : null);
        if (error != null)
            ImGui.TextColored(UiTheme.ErrorText, error);
        else if (fetching)
            ImGui.TextDisabled("Fetching...");

        using (ImRaii.Disabled(fetching))
        {
            if ((submitted || ImGui.Button("Import")) && !string.IsNullOrWhiteSpace(importDesignInput))
                DoImportDesignInput();
        }
        ImGui.SameLine();
        // Not gated on fetching - an in-flight Eorzea Collection fetch has no cancellation of its own,
        // so this just closes the popup and lets it finish quietly in the background.
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        if (plugin.EorzeaCollection.Phase == EorzeaCollectionImportPhase.Closed)
            ImGui.CloseCurrentPopup();
    }

    private void DoImportDesignInput()
    {
        importDesignInputError = null;
        var input = importDesignInput.Trim();

        if (EorzeaCollectionService.IsEorzeaCollectionUrl(input))
        {
            _ = plugin.EorzeaCollection.ImportAsync(plugin, input);
            return;
        }

        DoImportDesignCode(input);
    }

    private void DoImportDesignCode(string code)
    {
        if (!DesignShareCode.TryDecode(code, out var name, out var equipment, out var error))
        {
            importDesignInputError = error;
            return;
        }

        var (stateResult, state) = plugin.Glamourer.GetState();
        if (stateResult != GlamourerApiEc.Success || state == null)
        {
            importDesignInputError = $"Couldn't read current Glamourer state ({stateResult}).";
            return;
        }

        var designJson = GlamourerJsonSchema.BuildEquipmentOnlyDesign(state, GlamourerJsonSchema.BuildEquipmentSection(equipment!));
        var (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name!);
        if (addResult != GlamourerApiEc.Success)
        {
            importDesignInputError = addResult.ToString();
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Imported \"{name}\" from a design code.");
        selectedDesign = newId;
        RefreshDesigns();
        ImGui.CloseCurrentPopup();
    }
}
