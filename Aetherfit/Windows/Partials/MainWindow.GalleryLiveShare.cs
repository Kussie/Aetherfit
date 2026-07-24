using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private void OpenShareLiveDialog() =>
        plugin.ShareLiveWindow.Show(HasAnyFilter, CollectVisibleDesignIds());

    private void OpenReceiveLiveDialog() =>
        plugin.ReceiveLiveWindow.Show();

    // The "Open Shared Gallery" dropdown: a local file, or a live pull from another online player.
    private void DrawOpenGalleryPopup()
    {
        using var popup = ImRaii.Popup("##openGalleryPopup");
        if (!popup.Success)
            return;

        var galleryBusy = plugin.GallerySharing.IsBusy;
        using (ImRaii.Disabled(galleryBusy))
        {
            if (ImGui.Selectable("From File..."))
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
            if (ImGui.Selectable("From Live Share..."))
                OpenReceiveLiveDialog();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!liveSharingEnabled
                ? "Live sharing is temporarily disabled."
                : plugin.LiveShare.IsBusy
                    ? "Reopen the receive in progress."
                    : "Receive a gallery directly from another online player.");
    }
}
