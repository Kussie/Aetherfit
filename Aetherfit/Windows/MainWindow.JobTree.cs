using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private readonly record struct JobGroup(JobRole Role, List<(JobInfo Job, List<DesignLeaf> Leaves)> Jobs);

    private List<JobGroup> cachedJobGroups = new();
    private FolderNode cachedUnassignedJobFolder = new();
    private int cachedJobTreeGeneration = -1;
    private int cachedJobTreeJobVersion = -1;
    private FilterSnapshot cachedJobTreeFilterSnapshot;
    private Dictionary<string, bool> cachedJobTreeFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, bool> cachedJobTreeFilterJobs = new();

    private bool IsJobTreeCacheStale() =>
        cachedJobTreeGeneration != designListGeneration ||
        cachedJobTreeJobVersion != jobAssociationVersion ||
        cachedJobTreeFilterSnapshot != CaptureFilterSnapshot() ||
        !FiltersEqual(cachedJobTreeFilterTags, filterTags) ||
        !FiltersEqual(cachedJobTreeFilterJobs, filterJobs);

    private void RebuildJobTreeCache()
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

        cachedJobGroups = plugin.GameData.GetSelectableJobs()
            .GroupBy(j => j.Role)
            .Select(roleGroup => new JobGroup(roleGroup.Key,
                roleGroup.Where(j => byJob.ContainsKey(j.RowId)).Select(j => (j, byJob[j.RowId])).ToList()))
            .Where(g => g.Jobs.Count > 0)
            .ToList();
        cachedUnassignedJobFolder = BuildFolderTree(unassigned);

        cachedJobTreeGeneration = designListGeneration;
        cachedJobTreeJobVersion = jobAssociationVersion;
        cachedJobTreeFilterSnapshot = CaptureFilterSnapshot();
        cachedJobTreeFilterTags = new(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedJobTreeFilterJobs = new(filterJobs);
    }

    private void DrawJobTree(bool hasFilter)
    {
        if (IsJobTreeCacheStale())
            RebuildJobTreeCache();

        foreach (var group in cachedJobGroups)
        {
            if (!DrawJobGroupHeader($"{GameDataService.RoleLabel(group.Role)}##role{(int)group.Role}", hasFilter))
                continue;

            foreach (var (job, leaves) in group.Jobs)
                DrawJobNode(job, leaves, hasFilter);

            ImGui.TreePop();
        }

        if ((cachedUnassignedJobFolder.Designs.Count > 0 || cachedUnassignedJobFolder.Folders.Count > 0)
            && DrawJobGroupHeader("Unassigned##unassignedJobs", hasFilter))
        {
            DrawTree(cachedUnassignedJobFolder, hasFilter);
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
