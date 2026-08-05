using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private const string BulkLayerAssignPopupId = "Apply as Layer to Multiple Designs##bulkLayerAssign";
    private const string BulkLayerFilterPopupId = "BulkLayerFilterPopup";
    private const int MaxBulkLayerPreviewRows = 15;

    private Guid bulkLayerSourceId;
    private readonly Dictionary<string, bool> bulkLayerFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, bool> bulkLayerFilterJobs = new();
    private readonly HashSet<DesignSource> bulkLayerFilterSources = new();
    private bool bulkLayerIsBefore = true;
    private readonly DesignLayer bulkLayerJobTemplate = new();
    private string bulkLayerTagSearchText = string.Empty;

    private void OpenBulkLayerAssignPopup(Guid sourceId)
    {
        bulkLayerSourceId = sourceId;
        bulkLayerFilterTags.Clear();
        bulkLayerFilterJobs.Clear();
        bulkLayerFilterSources.Clear();
        foreach (var provider in plugin.DesignProviders)
            bulkLayerFilterSources.Add(provider.Source);
        bulkLayerIsBefore = true;
        bulkLayerJobTemplate.AllJobs = true;
        bulkLayerJobTemplate.Jobs.Clear();
        ImGui.OpenPopup(BulkLayerAssignPopupId);
    }

    private void DrawBulkLayerAssignPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(480, 560) * ImGuiHelpers.GlobalScale, ImGuiCond.Appearing);
        using var modal = ImRaii.PopupModal(BulkLayerAssignPopupId);
        if (!modal.Success)
            return;

        var sourceName = ResolveLinkedDesignName(bulkLayerSourceId);
        ImGui.TextWrapped($"Apply \"{sourceName}\" as a layer to every design matching:");
        ImGui.Spacing();

        DrawSubheader("Filter");
        ImGui.Indent();

        var filterCount = bulkLayerFilterTags.Count + bulkLayerFilterJobs.Count;
        if (ImGui.Button(Pills.TagJobFilterLabel(filterCount)))
        {
            bulkLayerTagSearchText = string.Empty;
            ImGui.OpenPopup(BulkLayerFilterPopupId);
        }
        DrawBulkLayerFilterPopup();

        ImGui.Spacing();
        ImGui.TextDisabled("Sources:");
        foreach (var provider in plugin.DesignProviders)
        {
            var on = bulkLayerFilterSources.Contains(provider.Source);
            if (ImGui.Checkbox($"{provider.DisplayName}##bulkLayerSource{provider.Source}", ref on))
            {
                if (on)
                    bulkLayerFilterSources.Add(provider.Source);
                else
                    bulkLayerFilterSources.Remove(provider.Source);
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.Unindent();
        ImGui.Spacing();

        DrawSubheader("Placement");
        ImGui.Indent();
        if (ImGui.RadioButton("Applied Before", bulkLayerIsBefore))
            bulkLayerIsBefore = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("Applied After", !bulkLayerIsBefore))
            bulkLayerIsBefore = false;
        ImGui.Unindent();
        ImGui.Spacing();

        DrawSubheader("Job Scoping");
        ImGui.Indent();
        DrawJobMultiselect(bulkLayerJobTemplate);
        ImGui.Unindent();
        ImGui.Spacing();

        var candidates = plugin.Configuration.CachedOutfits
            .Where(kv => kv.Key != bulkLayerSourceId
                      && plugin.Configuration.IsProviderEnabled(kv.Value.Source)
                      && bulkLayerFilterSources.Contains(kv.Value.Source)
                      && bulkLayerFilterTags.MatchesFilter((IReadOnlyCollection<string>?)kv.Value.Tags ?? Array.Empty<string>())
                      && bulkLayerFilterJobs.MatchesFilter(plugin.Configuration.GetJobAssociations(kv.Key)))
            .Select(kv => (Id: kv.Key, kv.Value.Name,
                AlreadyLayered: plugin.Configuration.GetLayerSlots(kv.Key)
                    .SelectMany(s => s.Designs).Any(l => l.DesignId == bulkLayerSourceId)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var toApply = candidates.Count(c => !c.AlreadyLayered);
        var skipped = candidates.Count - toApply;

        DrawSubheader($"Matches ({candidates.Count})");
        ImGui.Indent();
        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("No designs match this filter.");
        }
        else
        {
            var listHeight = Math.Min(candidates.Count, MaxBulkLayerPreviewRows) * ImGui.GetTextLineHeightWithSpacing();
            using (ImRaii.Child("##bulkLayerPreview", new Vector2(-1, listHeight), true))
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.AlreadyLayered)
                        ImGui.TextDisabled($"{candidate.Name} (already has this layer)");
                    else
                        ImGui.TextUnformatted(candidate.Name);
                }
            }
        }
        ImGui.Unindent();
        ImGui.Spacing();

        using (ImRaii.Disabled(toApply == 0))
        {
            if (ImGui.Button($"Apply to {toApply} Design{(toApply == 1 ? "" : "s")}"))
            {
                ImGui.CloseCurrentPopup();
                DoBulkAssignLayer(candidates.Where(c => !c.AlreadyLayered).Select(c => c.Id).ToList(), sourceName, skipped);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void DrawBulkLayerFilterPopup()
    {
        using var popup = ImRaii.Popup(BulkLayerFilterPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        var scale = ImGuiHelpers.GlobalScale;
        var allTags = plugin.Configuration.DistinctSortedTags();
        var allJobs = plugin.GameData.GetSelectableJobs();

        ImGui.SetNextItemWidth(260 * scale);
        ImGui.InputTextWithHint("##bulkLayerFilterSearch", "Search tags or jobs...", ref bulkLayerTagSearchText, 64);
        ImGui.Separator();

        var matchingTags = allTags
            .Where(t => bulkLayerTagSearchText.Length == 0 || t.Contains(bulkLayerTagSearchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var matchingJobs = allJobs
            .Where(j => bulkLayerTagSearchText.Length == 0 || j.Name.Contains(bulkLayerTagSearchText, StringComparison.OrdinalIgnoreCase))
            .Select(j => (j.RowId, j.Name, (JobRole?)j.Role))
            .ToList();

        var emptyMessage = allTags.Count == 0 && allJobs.Count == 0
            ? "No tags or jobs to filter by yet."
            : "No matching tags or jobs.";
        Pills.DrawTagJobFilterList(matchingTags, matchingJobs, Array.Empty<(string, string)>(),
            bulkLayerFilterTags, bulkLayerFilterJobs, new Dictionary<string, bool>(),
            plugin.GameData.GetJobIcon, "bulkLayer", 260 * scale, emptyMessage);
    }

    // targetIds already excludes AlreadyLayered candidates - those are left completely untouched.
    private void DoBulkAssignLayer(IReadOnlyList<Guid> targetIds, string sourceName, int skipped)
    {
        foreach (var targetId in targetIds)
        {
            var slots = plugin.Configuration.GetLayerSlots(targetId);
            slots.Add(new DesignLayerSlot
            {
                Designs =
                {
                    new DesignLayer
                    {
                        DesignId = bulkLayerSourceId,
                        AllJobs = bulkLayerJobTemplate.AllJobs,
                        Jobs = new List<uint>(bulkLayerJobTemplate.Jobs),
                    },
                },
                IsBefore = bulkLayerIsBefore,
            });
            plugin.Configuration.SetLayerSlots(targetId, slots);
        }
        plugin.Configuration.Save();

        var applied = targetIds.Count;
        var suffix = skipped > 0 ? $" ({skipped} already had it - skipped)." : ".";
        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Added \"{sourceName}\" as a layer to {applied} design{(applied == 1 ? "" : "s")}{suffix}");
    }
}
