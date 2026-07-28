using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Game;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

// Cover Mode's "Group by job association" view: same Role > Job grouping as the Edit Mode job tree
// (MainWindow.JobTree.cs), rendered as collapsible sections with a flat cover grid per job instead
// of tree rows. Built from cachedVisible, same reasoning as GalleryTagGroups.
public partial class MainWindow
{
    private readonly record struct CoverJobGroup(JobRole Role, List<(JobInfo Job, List<DesignLeaf> Designs)> Jobs);

    private List<CoverJobGroup> cachedCoverJobGroups = new();
    private List<DesignLeaf> cachedCoverUnassignedJob = new();
    private int cachedCoverJobGroupVersion = -1;
    private int cachedCoverJobAssociationVersion = -1;
    private readonly Dictionary<string, bool> coverJobSectionOpen = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildCoverJobGroupCache()
    {
        var byJob = new Dictionary<uint, List<DesignLeaf>>();
        var unassigned = new List<DesignLeaf>();

        foreach (var leaf in cachedVisible)
        {
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

        cachedCoverJobGroups = plugin.GameData.GetSelectableJobs()
            .GroupBy(j => j.Role)
            .Select(roleGroup => new CoverJobGroup(roleGroup.Key,
                roleGroup.Where(j => byJob.ContainsKey(j.RowId)).Select(j => (j, byJob[j.RowId])).ToList()))
            .Where(g => g.Jobs.Count > 0)
            .ToList();
        cachedCoverUnassignedJob = unassigned;

        cachedCoverJobGroupVersion = galleryCacheVersion;
        cachedCoverJobAssociationVersion = jobAssociationVersion;
    }

    private void DrawCoverGroupedByJob()
    {
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        if (cachedCoverJobGroupVersion != galleryCacheVersion || cachedCoverJobAssociationVersion != jobAssociationVersion)
            RebuildCoverJobGroupCache();

        foreach (var group in cachedCoverJobGroups)
        {
            ImGui.Separator();
            var roleKey = $"role{(int)group.Role}";
            if (!DrawCoverJobSectionHeader(GameDataService.RoleLabel(group.Role), roleKey))
                continue;

            ImGui.Indent();
            ImGui.Spacing();
            foreach (var (job, designs) in group.Jobs)
                DrawCoverJobNode(job, designs);
            ImGui.Unindent();
        }

        if (cachedCoverUnassignedJob.Count > 0)
        {
            ImGui.Separator();
            if (DrawCoverJobSectionHeader($"Unassigned ({cachedCoverUnassignedJob.Count})", "unassignedJobs"))
            {
                ImGui.Spacing();
                var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
                DrawCoverGridRange(cachedCoverUnassignedJob, 0, cachedCoverUnassignedJob.Count, columns, thumbWidth, thumbHeight);
                ImGui.Spacing();
            }
        }
    }

    private void DrawCoverJobNode(JobInfo job, List<DesignLeaf> designs)
    {
        if (!DrawCoverJobSectionHeader($"{job.Name} ({designs.Count})", $"job{job.RowId}"))
            return;

        ImGui.Spacing();
        var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
        DrawCoverGridRange(designs, 0, designs.Count, columns, thumbWidth, thumbHeight);
        ImGui.Spacing();
    }

    private bool DrawCoverJobSectionHeader(string label, string key)
    {
        if (!coverJobSectionOpen.TryGetValue(key, out var open))
            open = true;
        using var id = ImRaii.PushId(key);
        var result = Pills.DrawCollapsibleSubheader(label, ref open);
        coverJobSectionOpen[key] = open;
        return result;
    }
}
