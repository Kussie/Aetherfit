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

    // Mirrors DrawTree's behaviour: while a filter is active, force the node open and remember its
    // pre-filter state so it can be restored when filters clear. GetID(label) matches the id TreeNodeEx
    // derives from the same "name##suffix" label (## keeps the full string in the id hash).
    private void ForceOpenIfFiltering(string label, bool hasFilter)
    {
        if (!hasFilter) return;

        var id = ImGui.GetID(label);
        if (!treeOpenSnapshot.ContainsKey(id))
            treeOpenSnapshot[id] = ImGui.GetStateStorage().GetInt(id, 0) != 0;
        ImGui.SetNextItemOpen(true, ImGuiCond.Always);
    }

    private static void CollectAllLeaves(FolderNode node, List<DesignLeaf> acc)
    {
        foreach (var folder in node.Folders.Values)
            CollectAllLeaves(folder, acc);
        acc.AddRange(node.Designs);
    }
}
