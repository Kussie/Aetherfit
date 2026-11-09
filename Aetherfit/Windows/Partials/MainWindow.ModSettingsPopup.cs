using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Ui;
// Aetherfit's own GroupType (Configuration.cs, party-size conditions) lives in the enclosing namespace
// and would otherwise shadow Penumbra's - alias it explicitly here.
using PenumbraGroupType = Penumbra.Api.Enums.GroupType;

namespace Aetherfit.Windows;

// Edits a mod's option-group selections from the Mod Associations panel. Reads and writes the design's
// own Glamourer file, not Penumbra's live collection - "live" is just a temporary apply-time override.
public partial class MainWindow
{
    private const string ModSettingsPopupId = "Edit Mod Settings##modSettingsPopup";

    private sealed class ModSettingGroupVm
    {
        public required string GroupName;
        public required PenumbraGroupType Type;
        public required IReadOnlyList<string> Options;
        public required HashSet<string> Original;
        public required HashSet<string> Selected;
    }

    private Guid? modSettingsDesignId;
    private string? modSettingsDirectory;
    private string? modSettingsModName;
    private List<ModSettingGroupVm>? modSettingsGroups;
    private bool modSettingsPopupRequested;

    private void RequestEditModSettings(Guid designId, CachedMod mod)
    {
        modSettingsDesignId = designId;
        modSettingsDirectory = mod.Directory;
        modSettingsModName = mod.Name;
        modSettingsGroups = BuildModSettingsVm(mod);
        modSettingsPopupRequested = true;
    }

    // Groups/options/types come from Penumbra (the mod's definition); current selection is seeded from
    // the design's own stored mod.Settings, not a live query. Null if the mod has no option groups.
    private List<ModSettingGroupVm>? BuildModSettingsVm(CachedMod mod)
    {
        var available = plugin.Penumbra.GetAvailableModSettings(mod.Directory, mod.Name);
        if (available == null || available.Count == 0)
            return null;

        var result = new List<ModSettingGroupVm>(available.Count);
        foreach (var (group, (options, type)) in available)
        {
            var selected = mod.Settings.TryGetValue(group, out var csv) && !string.IsNullOrEmpty(csv)
                ? new HashSet<string>(csv.Split(", ", StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            result.Add(new ModSettingGroupVm
            {
                GroupName = group,
                Type = type,
                Options = options,
                Original = new HashSet<string>(selected, StringComparer.Ordinal),
                Selected = selected,
            });
        }
        return result;
    }

    // Above this many groups the body splits into two side-by-side scrolling columns instead of one
    // long list - a heavily-optioned mod can otherwise run the popup off the bottom of the screen.
    private const int TwoColumnGroupThreshold = 6;

    private void DrawModSettingsPopup()
    {
        if (modSettingsPopupRequested)
        {
            modSettingsPopupRequested = false;
            ImGui.OpenPopup(ModSettingsPopupId);
        }

        var scale = ImGuiHelpers.GlobalScale;
        var groups = modSettingsGroups;
        var useTwoColumns = groups is { Count: > TwoColumnGroupThreshold };
        var defaultWidth = (useTwoColumns ? 760f : 420f) * scale;
        var defaultHeight = (groups is { Count: > 4 } ? 520f : 320f) * scale;
        ImGui.SetNextWindowSize(new Vector2(defaultWidth, defaultHeight), ImGuiCond.Appearing);
        var stillOpen = true;
        using var modal = ImRaii.PopupModal(ModSettingsPopupId, ref stillOpen);
        if (!stillOpen)
            ImGui.CloseCurrentPopup();
        if (!modal.Success)
            return;

        var displayName = string.IsNullOrWhiteSpace(modSettingsModName) ? modSettingsDirectory : modSettingsModName;
        ImGui.TextUnformatted(displayName ?? "(unnamed mod)");
        ImGui.Spacing();

        using (ImRaii.PushColor(ImGuiCol.Text, UiTheme.CautionText))
            ImGui.TextWrapped("Changes are saved to this design's own file - they won't affect your current "
                + "Penumbra collection or any other design using this mod.");
        ImGui.Spacing();
        ImGui.Separator();

        // The footer (Apply/Cancel) sits outside this scrolling child, so it stays put - "sticky" -
        // regardless of how tall the group list gets.
        var footerH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        var bodyH = Math.Max(ImGui.GetFrameHeight() * 3, ImGui.GetContentRegionAvail().Y - footerH);
        using (var body = ImRaii.Child("ModSettingsBody", new Vector2(0, bodyH)))
        {
            if (body.Success)
                DrawModSettingsBody(groups, useTwoColumns);
        }

        ImGui.Separator();
        var canApply = modSettingsDesignId != null && groups is { Count: > 0 };
        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.Button("Apply"))
            {
                ImGui.CloseCurrentPopup();
                ApplyModSettings();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        using (ImRaii.Disabled(groups is not { Count: > 0 }))
        {
            ImGui.SameLine();
            if (ImGui.Button("Load from Penumbra"))
                LoadModSettingsFromPenumbra();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Discard these edits and load whatever this mod's settings currently are in "
                + "Penumbra instead - still requires Apply to save onto the design.");
    }

    // Overwrites the working selection (not Original, so Apply still diffs correctly) - lets you pull in
    // settings you tweaked live in Penumbra without hunting through groups by hand.
    private void LoadModSettingsFromPenumbra()
    {
        if (modSettingsGroups == null || modSettingsDirectory == null)
            return;

        if (plugin.Penumbra.GetLocalPlayerCollectionId() is not { } collectionId)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Penumbra's collection for your character couldn't be resolved.");
            return;
        }

        var current = plugin.Penumbra.GetCurrentModSettingsWithTemp(collectionId, modSettingsDirectory, modSettingsModName ?? string.Empty);
        if (current == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Couldn't read this mod's current settings from Penumbra.");
            return;
        }

        foreach (var group in modSettingsGroups)
        {
            group.Selected.Clear();
            if (current.Value.Settings.TryGetValue(group.GroupName, out var selected))
                foreach (var option in selected)
                    group.Selected.Add(option);
        }
    }

    private void DrawModSettingsBody(List<ModSettingGroupVm>? groups, bool useTwoColumns)
    {
        if (groups == null || groups.Count == 0)
        {
            ImGui.TextDisabled("This mod has no option groups to configure.");
            return;
        }
        if (!useTwoColumns)
        {
            foreach (var group in groups)
                DrawModSettingGroupRow(group);
            return;
        }

        // Two independent scroll regions, not ImGui.Columns - group heights vary too much to row-align.
        var half = (groups.Count + 1) / 2;
        var colWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        using (var left = ImRaii.Child("ModSettingsColLeft", new Vector2(colWidth, 0)))
        {
            if (left.Success)
                foreach (var group in groups.Take(half))
                    DrawModSettingGroupRow(group);
        }
        ImGui.SameLine();
        using (var right = ImRaii.Child("ModSettingsColRight", Vector2.Zero))
        {
            if (right.Success)
                foreach (var group in groups.Skip(half))
                    DrawModSettingGroupRow(group);
        }
    }

    private void DrawModSettingGroupRow(ModSettingGroupVm group)
    {
        ImGui.TextColored(UiTheme.SectionHeader, group.GroupName);

        if (group.Type == PenumbraGroupType.Single)
        {
            var current = group.Selected.FirstOrDefault() ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            using var combo = ImRaii.Combo($"##{group.GroupName}", current);
            if (combo.Success)
                foreach (var option in group.Options)
                    if (ImGui.Selectable(option, option == current))
                    {
                        group.Selected.Clear();
                        group.Selected.Add(option);
                    }
        }
        else
        {
            foreach (var option in group.Options)
            {
                var @checked = group.Selected.Contains(option);
                if (ImGui.Checkbox($"{option}##{group.GroupName}", ref @checked))
                {
                    if (@checked)
                        group.Selected.Add(option);
                    else
                        group.Selected.Remove(option);
                }
            }
        }
        ImGui.Spacing();
    }

    // Only writes groups that actually changed from what was already on the design.
    private void ApplyModSettings()
    {
        if (modSettingsDesignId is not { } designId || modSettingsGroups == null || modSettingsDirectory == null)
            return;

        var directory = modSettingsDirectory;
        var displayName = string.IsNullOrWhiteSpace(modSettingsModName) ? directory : modSettingsModName;

        var changedGroups = modSettingsGroups
            .Where(g => !g.Selected.SetEquals(g.Original))
            .ToDictionary(g => g.GroupName, IReadOnlyCollection<string> (g) => g.Selected.ToList());
        if (changedGroups.Count == 0)
            return;

        var result = plugin.GlamourerDesignFile.PushModSettingsToGlamourer(designId, directory, changedGroups);
        if (result.Success)
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Updated mod settings for \"{displayName}\" on this design.");
        else
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
    }
}
