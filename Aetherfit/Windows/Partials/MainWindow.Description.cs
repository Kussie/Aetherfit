using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// The design detail pane's Description section: view/edit toggle, plus pulling the description back
// from Glamourer for Glamourer-sourced designs.
public partial class MainWindow
{
    private const string PullDescriptionPopupId = "Pull Description from Glamourer?##pullDescConfirm";

    // Reset whenever the selected design changes so edit mode always starts fresh for the new selection.
    private Guid? descriptionEditId;
    private bool descriptionEditing;
    private string descriptionEditBuffer = string.Empty;
    private string? descriptionOriginalValue;

    private void DrawDescriptionEditor(Guid id, CachedOutfit details)
    {
        if (descriptionEditId != id)
        {
            descriptionEditId = id;
            descriptionEditing = false;
        }

        if (descriptionEditing)
        {
            ImGui.SetNextItemWidth(-1);
            var boxHeight = 4 * ImGui.GetTextLineHeightWithSpacing();
            if (ImGui.InputTextMultiline("##description", ref descriptionEditBuffer, 2000, new Vector2(-1, boxHeight)))
            {
                var trimmed = descriptionEditBuffer.Trim();
                plugin.Configuration.SetDescription(id, details, trimmed.Length == 0 ? null : trimmed);
            }

            if (ImGuiComponents.IconButton("descDone", FontAwesomeIcon.Check))
                descriptionEditing = false;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Done");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton("descCancel", FontAwesomeIcon.Times))
            {
                plugin.Configuration.SetDescription(id, details, descriptionOriginalValue);
                descriptionEditing = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancel — restore the previous value");

            return;
        }

        if (!string.IsNullOrWhiteSpace(details.Description))
            ImGui.TextWrapped(details.Description);
        else
            ImGui.TextDisabled("This design has no description set.");

        if (ImGuiComponents.IconButton("descEdit", FontAwesomeIcon.Pen))
        {
            descriptionOriginalValue = details.Description;
            descriptionEditBuffer = details.Description ?? string.Empty;
            descriptionEditing = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Edit description");

        if (details.Source == DesignSource.Glamourer)
        {
            ImGui.SameLine();
            var hasGlamourerDescription = !string.IsNullOrWhiteSpace(details.GlamourerDescription);
            if (ImGuiComponents.IconButton("pullDescription", FontAwesomeIcon.Sync) && hasGlamourerDescription)
                ConfirmDialog.Open(PullDescriptionPopupId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(hasGlamourerDescription
                    ? "Replace the description above with the one currently set in Glamourer"
                    : "Glamourer has no description set for this design");

            if (ConfirmDialog.Draw(PullDescriptionPopupId,
                    $"This will replace your saved description for \"{details.Name}\" with the one currently "
                    + "set on the design in Glamourer. This can't be undone.",
                    "Pull Description"))
            {
                plugin.Configuration.PullDescriptionFromGlamourer(id, details);
            }
        }
    }
}
