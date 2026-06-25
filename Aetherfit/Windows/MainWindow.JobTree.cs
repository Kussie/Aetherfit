using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private void DrawJobTree(bool hasFilter)
    {
        var allLeaves = new List<DesignLeaf>();
        CollectAllLeaves(root, allLeaves);

        var byJob = new Dictionary<uint, List<DesignLeaf>>();
        var unassigned = new List<DesignLeaf>();

        foreach (var leaf in allLeaves)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(leaf.Id, out var cached);
            if (!DesignMatchesFilters(leaf, cached)) continue;

            var jobs = plugin.Configuration.GetJobAssociations(leaf.Id);
            if (jobs.Count == 0)
            {
                unassigned.Add(leaf);
                continue;
            }

            foreach (var job in jobs)
            {
                if (!byJob.TryGetValue(job, out var list))
                {
                    list = new List<DesignLeaf>();
                    byJob[job] = list;
                }
                list.Add(leaf);
            }
        }

        foreach (var list in byJob.Values)
            list.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));

        // GetSelectableJobs is already ordered by role then RowId, so GroupBy preserves the desired display order.
        foreach (var roleGroup in plugin.GameData.GetSelectableJobs().GroupBy(j => j.Role))
        {
            var jobsWithDesigns = roleGroup.Where(j => byJob.ContainsKey(j.RowId)).ToList();
            if (jobsWithDesigns.Count == 0) continue;

            if (!DrawJobGroupHeader($"{GameDataService.RoleLabel(roleGroup.Key)}##role{(int)roleGroup.Key}", hasFilter))
                continue;

            foreach (var job in jobsWithDesigns)
                DrawJobNode(job, byJob[job.RowId], hasFilter);

            ImGui.TreePop();
        }

        if (unassigned.Count > 0 && DrawJobGroupHeader("Unassigned##unassignedJobs", hasFilter))
        {
            DrawTree(BuildFolderTree(unassigned), hasFilter);
            ImGui.TreePop();
        }
    }

    private void DrawJobNode(JobInfo job, List<DesignLeaf> leaves, bool hasFilter)
    {
        var label = $"{job.Name}##job{job.RowId}";

        var icon = plugin.GameData.GetJobIcon(job.RowId);
        if (icon != null)
        {
            var lineH = ImGui.GetTextLineHeight();
            ImGui.Image(icon.Handle, new Vector2(lineH, lineH));
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        }

        ForceOpenIfFiltering(label, hasFilter);
        if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var leaf in leaves)
                DrawDesignLeaf(leaf);
            ImGui.TreePop();
        }
    }

    private bool DrawJobGroupHeader(string label, bool hasFilter)
    {
        ForceOpenIfFiltering(label, hasFilter);
        return ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth);
    }

    // Shared by DrawTree and the job tree. While a filter is active we remember each node's pre-filter
    // open state (so we can put it back once the filter clears) and pop it open on the frame the filter
    // just changed - that reveals the matches without us fighting the user every time they collapse
    // something. GetID(label) lines up with the id TreeNodeEx builds from the same "name##suffix" label;
    // the ## keeps the whole string in the hash.
    private void ForceOpenIfFiltering(string label, bool hasFilter)
    {
        if (!hasFilter) return;

        var id = ImGui.GetID(label);
        if (!treeOpenSnapshot.ContainsKey(id))
            treeOpenSnapshot[id] = ImGui.GetStateStorage().GetInt(id, 0) != 0;

        // Only pop open right after the filter changed, otherwise leave folders alone so they collapse.
        if (expandTreesForFilter)
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
    }

    private static void CollectAllLeaves(FolderNode node, List<DesignLeaf> acc)
    {
        foreach (var folder in node.Folders.Values)
            CollectAllLeaves(folder, acc);
        acc.AddRange(node.Designs);
    }
}
