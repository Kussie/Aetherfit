using System.Collections.Generic;
using System.Linq;
using Aetherfit.Models;
using Aetherfit.Services.Game;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

// "Group by job association" for the shared gallery view - same Role > Job grouping as Cover Mode's
// own job grouping (MainWindow.GalleryJobGroups.cs), built fresh each draw from the filtered design list.
public sealed partial class ForeignGalleryWindow
{
    private void DrawGroupedByJob(List<ForeignDesign> visible)
    {
        var byJob = new Dictionary<uint, List<ForeignDesign>>();
        var unassigned = new List<ForeignDesign>();

        foreach (var design in visible)
        {
            if (design.Jobs.Count == 0)
            {
                unassigned.Add(design);
                continue;
            }

            foreach (var job in design.Jobs)
            {
                if (!byJob.TryGetValue(job, out var list))
                {
                    list = new List<ForeignDesign>();
                    byJob[job] = list;
                }
                list.Add(design);
            }
        }

        var groups = plugin.GameData.GetSelectableJobs()
            .GroupBy(j => j.Role)
            .Select(roleGroup => (Role: roleGroup.Key,
                Jobs: roleGroup.Where(j => byJob.ContainsKey(j.RowId)).Select(j => (j, byJob[j.RowId])).ToList()))
            .Where(g => g.Jobs.Count > 0)
            .ToList();

        var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();

        foreach (var group in groups)
        {
            ImGui.Separator();
            var roleKey = $"role{(int)group.Role}";
            if (!DrawJobSectionHeader(GameDataService.RoleLabel(group.Role), roleKey))
                continue;

            ImGui.Indent();
            ImGui.Spacing();
            foreach (var (job, designs) in group.Jobs)
                DrawJobGroupNode(job, designs, columns, thumbWidth, thumbHeight);
            ImGui.Unindent();
        }

        if (unassigned.Count > 0)
        {
            ImGui.Separator();
            if (DrawJobSectionHeader($"Unassigned ({unassigned.Count})", "unassignedJobs"))
            {
                ImGui.Spacing();
                DrawGridRange(unassigned, 0, unassigned.Count, columns, thumbWidth, thumbHeight);
                ImGui.Spacing();
            }
        }
    }

    private void DrawJobGroupNode(JobInfo job, List<ForeignDesign> designs, int columns, float thumbWidth, float thumbHeight)
    {
        if (!DrawJobSectionHeader($"{job.Name} ({designs.Count})", $"job{job.RowId}"))
            return;

        ImGui.Spacing();
        DrawGridRange(designs, 0, designs.Count, columns, thumbWidth, thumbHeight);
        ImGui.Spacing();
    }
}
