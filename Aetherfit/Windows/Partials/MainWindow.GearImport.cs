using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;
using Aetherfit.Services.Designs;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// Everything to do with pulling a selected design's actual worn gear back into Glamourer: the
// "Import into Glamourer" flow for Glamaholic/Glamour Plate designs and staged gear-override captures,
// and the separate "Import current gear" capture flow (update in place, or spin off a new design).
public partial class MainWindow
{
    private const string ImportToGlamourerPopupId = "Import into Glamourer?##importGlamourerConfirm";
    private const string ImportGearPopupId = "Import current gear?##importGearConfirm";
    private string importDesignName = string.Empty;
    private bool importReclaimFocus;
    private bool importDeleteFromGlamaholic;
    private bool importGearCreateNew;
    private string importGearNewName = string.Empty;
    private bool importGearReclaimFocus;

    private void DrawImportToGlamourerPopup(Guid id, CachedOutfit details, bool isGlamaholic, bool isGearOverride)
    {
        // Height recomputed every frame (not just on Appearing) so the window grows to fit the warning
        // below once the delete checkbox is ticked, instead of clipping it behind a size fixed before
        // that text ever existed.
        ImGui.SetNextWindowSize(new Vector2(420, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(ImportToGlamourerPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return;

        var message = isGearOverride
            ? $"This will create a new Glamourer design from \"{details.Name}\" with the gear/customizations/mods you captured, "
              + "keeping everything else (links, description, tags, redraw/reset settings) from the original design."
            : $"This will create a new design in Glamourer from \"{details.Name}\"'s equipment. "
              + "The wearer's current face/body won't be included - only gear.";
        ImGui.TextWrapped(message);
        ImGui.Spacing();

        ImGui.TextUnformatted("Design name");
        if (ImGui.IsWindowAppearing() || importReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            importReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##importDesignName", "Design name (required)", ref importDesignName, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = importDesignName.Trim();

        if (isGlamaholic)
        {
            ImGui.Spacing();
            ImGui.Checkbox("Remove this design from Glamaholic after import", ref importDeleteFromGlamaholic);
            if (importDeleteFromGlamaholic)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, UiTheme.ErrorText);
                ImGui.TextWrapped(
                    "Glamaholic won't see this removal until it's restarted or you relog - it keeps its own "
                    + "plate list in memory and won't notice the change to its file until then.");
            }
        }

        ImGui.Spacing();

        var canConfirm = trimmed.Length > 0;
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Import") || (submitted && canConfirm))
            {
                ImGui.CloseCurrentPopup();
                if (isGearOverride)
                    DoImportGearOverrideToGlamourer(id, details, trimmed);
                else
                    DoImportToGlamourer(details, trimmed, isGlamaholic && importDeleteFromGlamaholic);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void DoImportToGlamourer(CachedOutfit details, string name, bool deleteFromGlamaholic)
    {
        var equipment = details.Source == DesignSource.Glamaholic
            ? plugin.Glamaholic.BuildImportPayload(details.ProviderDesignId)?.Equipment
            : plugin.GlamourPlate.BuildImportPayload(details.ProviderDesignId);

        if (equipment == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: design data not currently available - try refreshing.");
            return;
        }

        var (stateResult, state) = plugin.Glamourer.GetState();
        if (stateResult != GlamourerApiEc.Success || state == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: couldn't read current Glamourer state ({stateResult}).");
            return;
        }

        var designJson = GlamourerJsonSchema.BuildEquipmentOnlyDesign(state, equipment);
        var (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name);
        if (addResult != GlamourerApiEc.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: {addResult}");
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Imported \"{name}\" into Glamourer.");

        if (deleteFromGlamaholic)
        {
            var deleteResult = plugin.Glamaholic.DeletePlate(details.ProviderDesignId);
            if (!deleteResult.Success)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import succeeded, but couldn't remove \"{details.Name}\" from Glamaholic: {deleteResult.Error}");
            else
                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Removed \"{details.Name}\" from Glamaholic.");
        }

        selectedDesign = newId;
        RefreshDesigns();
    }

    private void DrawGearImportPendingBanner(Guid id, CachedOutfit details)
    {
        const string message = "Gear import staged - previewed here, not yet saved to Glamourer.";
        const string discardLabel = "Discard";

        var style = ImGui.GetStyle();
        var pad = 8f * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var discardW = ImGui.CalcTextSize(discardLabel).X + (style.FramePadding.X * 2);
        var wrapW = availW - (pad * 2) - discardW - style.ItemSpacing.X;
        var textH = ImGui.CalcTextSize(message, false, wrapW).Y;
        var boxH = Math.Max(textH, ImGui.GetFrameHeight()) + (pad * 2);

        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(availW, boxH),
            ImGui.ColorConvertFloat4ToU32(UiTheme.ToggleOffBg), 4f);

        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapW);
        using (ImRaii.PushColor(ImGuiCol.Text, UiTheme.CautionText))
            ImGui.TextUnformatted(message);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(new Vector2(
            start.X + availW - discardW - pad, start.Y + (boxH - ImGui.GetFrameHeight()) / 2));
        if (ImGui.Button(discardLabel))
        {
            plugin.Configuration.GearImportOverrides.Remove(id);
            plugin.Configuration.Save();
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Discarded the staged gear import for \"{details.Name}\".");
            RefreshDesigns();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Revert to what's actually saved in Glamourer");

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + boxH));
        ImGui.Spacing();
    }

    private void DrawImportGearPopup(Guid id, CachedOutfit details)
    {
        ImGui.SetNextWindowSize(new Vector2(420, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(ImportGearPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return;

        ImGui.TextWrapped("This reads your currently-equipped gear (including anything Glamourer is forcing), plus any Penumbra "
            + $"mods affecting it. Only customizations \"{details.Name}\" already sets get checked for changes - nothing new is added.");
        ImGui.Spacing();

        if (!importGearCreateNew)
        {
            if (ImGui.Button($"Update \"{details.Name}\""))
            {
                ImGui.CloseCurrentPopup();
                DoImportCurrentGear(id, details);
            }
            ImGui.SameLine();
            if (ImGui.Button("Create new design..."))
            {
                importGearCreateNew = true;
                importGearNewName = string.Empty;
                importGearReclaimFocus = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.TextUnformatted("Design name");
        if (importGearReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            importGearReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##importGearNewName", "Design name (required)", ref importGearNewName, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = importGearNewName.Trim();
        var canConfirm = trimmed.Length > 0;

        ImGui.Spacing();
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Create") || (submitted && canConfirm))
            {
                ImGui.CloseCurrentPopup();
                DoCreateDesignFromCurrentGear(id, details, trimmed);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Back"))
            importGearCreateNew = false;
    }

    private void DoImportCurrentGear(Guid id, CachedOutfit details)
    {
        var capture = plugin.GearImport.Capture(details, plugin.LayerResolution.Resolve(id));
        if (!capture.Success || capture.Override == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import current gear failed: {capture.Error}");
            return;
        }

        plugin.Configuration.GearImportOverrides[id] = capture.Override;
        plugin.Configuration.Save();

        // Reflect immediately rather than waiting for a full RefreshDesigns.
        GearImportService.ApplyOverlay(details, capture.Override);

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Captured current gear for \"{details.Name}\" — click Import to push it into a new Glamourer design.");
    }

    private void DoCreateDesignFromCurrentGear(Guid id, CachedOutfit details, string name)
    {
        var capture = plugin.GearImport.Capture(details, plugin.LayerResolution.Resolve(id));
        if (!capture.Success || capture.Override == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import current gear failed: {capture.Error}");
            return;
        }

        if (!TryBuildGearOverrideDesignJson(id, capture.Override, out var designJson, out var error))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: {error}");
            return;
        }

        var (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name);
        if (addResult != GlamourerApiEc.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: {addResult}");
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Created \"{name}\" from \"{details.Name}\" and currently worn equipment.");
        selectedDesign = newId;
        RefreshDesigns();
    }

    // Bases the new design on the original's own raw JSON, not a gear-only clone, so links/description/tags/etc. carry over.
    private void DoImportGearOverrideToGlamourer(Guid id, CachedOutfit details, string name)
    {
        if (!plugin.Configuration.GearImportOverrides.TryGetValue(id, out var ov))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: no captured gear found for \"{details.Name}\" - try Import current gear again.");
            return;
        }

        if (!TryBuildGearOverrideDesignJson(id, ov, out var designJson, out var error))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: {error}");
            return;
        }

        var (addResult, newId) = plugin.Glamourer.AddDesign(designJson, name);
        if (addResult != GlamourerApiEc.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Import failed: {addResult}");
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Imported \"{name}\" into Glamourer.");

        plugin.Configuration.GearImportOverrides.Remove(id);
        plugin.Configuration.Save();

        selectedDesign = newId;
        RefreshDesigns();
    }

    private bool TryBuildGearOverrideDesignJson(Guid id, GearImportOverride ov, out JObject designJson, out string? error)
    {
        var baseJson = plugin.Glamourer.GetDesignJObject(id);
        if (baseJson == null)
        {
            designJson = null!;
            error = "couldn't read the original Glamourer design.";
            return false;
        }

        designJson = (JObject)baseJson.DeepClone();
        designJson["Equipment"] = GlamourerJsonSchema.BuildEquipmentSection(ov.Equipment);
        designJson["Bonus"] = GlamourerJsonSchema.BuildBonusSection(ov.BonusItems);
        designJson["Customize"] = GlamourerJsonSchema.MergeCustomizeSection(baseJson["Customize"] as JObject ?? new JObject(), ov.Customizations);

        var customize = (JObject)designJson["Customize"]!;
        customize["Clan"] ??= new JObject();
        customize["Clan"]!["Value"] = ov.CustomizeClan;
        customize["Clan"]!["Apply"] = ov.CustomizeClanApplied;
        customize["Gender"] ??= new JObject();
        customize["Gender"]!["Value"] = ov.CustomizeGender;
        customize["Gender"]!["Apply"] = ov.CustomizeGenderApplied;

        designJson["Mods"] = GlamourerJsonSchema.AppendModsSection(baseJson["Mods"], ov.Mods);
        error = null;
        return true;
    }
}
