using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// The design detail pane's variant relationship: the picker that assigns a parent, and the two
// read-only sections showing "Variant of" / "Variants" once a relationship exists.
public partial class MainWindow
{
    private const string AddVariantPopupId = "AddVariantPopup";
    private string variantPickerFilter = string.Empty;

    // Single-level nesting only: a design that's already a variant of something can't itself be
    // picked as a parent, which transitively also excludes id's own existing variants (they already
    // carry a VariantInfo entry pointing at id).
    private void DrawAddVariantPopup(Guid id)
    {
        using var popup = ImRaii.Popup(AddVariantPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##variantFilter", "Filter by name...", ref variantPickerFilter, 64);
        ImGui.Separator();

        var matches = plugin.Configuration.CachedOutfits
            .Where(kv => kv.Key != id && plugin.Configuration.GetVariantInfo(kv.Key) == null
                        && (variantPickerFilter.Length == 0 || kv.Value.Name.Contains(variantPickerFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => (Id: kv.Key, kv.Value.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No matching designs.");
            return;
        }

        var listHeight = Math.Min(matches.Count, MaxVisibleDesignRows) * ImGui.GetTextLineHeightWithSpacing();
        using var scroll = ImRaii.Child("##variantPickerList", new Vector2(250 * ImGuiHelpers.GlobalScale, listHeight), false);
        foreach (var (parentId, name) in matches)
        {
            if (ImGui.Selectable($"{name}##variant{parentId}"))
            {
                var existing = plugin.Configuration.GetVariantInfo(id);
                plugin.Configuration.SetVariantParent(id, parentId,
                    existing?.InheritTagsAndDescription ?? true, existing?.InheritGear ?? false);
                plugin.Configuration.Save();
                variantVersion++;
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void DrawVariantSection(Guid id, VariantInfo variant)
    {
        if (!Pills.DrawCollapsibleSubheader("Variant", ref variantPanelOpen))
            return;
        ImGui.Indent();

        var parentName = ResolveLinkedDesignName(variant.ParentId);
        ImGui.TextDisabled("Variant of:");
        ImGui.SameLine();
        DesignDetailView.TextColoredUnformatted(ModLinkColor, parentName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Click to open in Aetherfit");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                OpenDesign(variant.ParentId);
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Unlink))
        {
            plugin.Configuration.RemoveVariant(id);
            plugin.Configuration.Save();
            variantVersion++;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove variant relationship");

        var inheritTags = variant.InheritTagsAndDescription;
        if (ImGui.Checkbox("Inherit tags and description from parent when unset", ref inheritTags))
        {
            variant.InheritTagsAndDescription = inheritTags;
            plugin.Configuration.ApplyVariantTagDescriptionFallback(id);
            plugin.Configuration.Save();
            variantVersion++;
        }

        var inheritGear = variant.InheritGear;
        if (ImGui.Checkbox("Inherit gear and customizations (applies parent first)", ref inheritGear))
        {
            variant.InheritGear = inheritGear;
            plugin.Configuration.Save();
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawVariantsOfSection(List<Guid> variantIds)
    {
        if (!Pills.DrawCollapsibleSubheader("Variants", ref variantsOfPanelOpen))
            return;
        ImGui.Indent();

        foreach (var variantId in variantIds.OrderBy(ResolveLinkedDesignName, StringComparer.OrdinalIgnoreCase))
        {
            DesignDetailView.TextColoredUnformatted(ModLinkColor, ResolveLinkedDesignName(variantId));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Click to open in Aetherfit");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    OpenDesign(variantId);
            }
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }
}
