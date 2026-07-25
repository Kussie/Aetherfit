using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private const string AddJobPopupId = "AddJobPopup";

    private void DrawJobAssociations(Guid id)
    {
        if (!DrawCollapsibleSubheader("Job Associations", ref jobAssociationsPanelOpen,
                "Associate this design with one or more jobs. \"/aetherfit job\" applies a random design matching your current job. These are saved by Aetherfit and survive a Refresh."))
            return;

        ImGui.Indent();

        var jobs = plugin.Configuration.GetJobAssociations(id);

        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;
        var first = true;

        uint? toRemove = null;
        foreach (var job in jobs)
        {
            var width = MeasureJobPill(job);
            Pills.PlaceItem(width, ref first, ref lineRight, cursorStart, spacing, availRight);
            if (DrawJobPill(job))
                toRemove = job;
        }

        // Trailing "+" add button, wrapped onto a new line if it would overflow.
        var addWidth = ImGui.GetFrameHeight();
        Pills.PlaceItem(addWidth, ref first, ref lineRight, cursorStart, spacing, availRight);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            ImGui.OpenPopup(AddJobPopupId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add job association");

        if (toRemove is { } remove)
        {
            var updated = jobs.Where(j => j != remove).ToList();
            plugin.Configuration.SetJobAssociations(id, updated);
            plugin.Configuration.Save();
            jobAssociationVersion++;
        }

        DrawAddJobPopup(id);

        ImGui.Unindent();
        ImGui.Spacing();
    }

    private float MeasureJobPill(uint job)
    {
        var style = ImGui.GetStyle();
        var lineH = ImGui.GetTextLineHeight();
        var labelW = ImGui.CalcTextSize($"{plugin.GameData.ResolveJobName(job)}  ×").X;
        return style.FramePadding.X * 2 + lineH + style.ItemInnerSpacing.X + labelW;
    }

    private bool DrawJobPill(uint job)
    {
        var style = ImGui.GetStyle();
        var lineH = ImGui.GetTextLineHeight();
        var iconSize = new Vector2(lineH, lineH);
        var name = plugin.GameData.ResolveJobName(job);
        var label = $"{name}  ×";
        var width = style.FramePadding.X * 2 + iconSize.X + style.ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
        var height = lineH + style.FramePadding.Y * 2;

        var p0 = ImGui.GetCursorScreenPos();

        bool clicked;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding)
                   .Push(ImGuiStyleVar.ButtonTextAlign, new Vector2(1f, 0.5f)))
        using (ImRaii.PushColor(ImGuiCol.Button, UiTheme.PillBase)
                   .Push(ImGuiCol.ButtonHovered, UiTheme.PillHovered)
                   .Push(ImGuiCol.ButtonActive, UiTheme.PillActive))
            clicked = ImGui.Button($"{label}##job{job}", new Vector2(width, height));

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Remove \"{name}\"");

        var icon = plugin.GameData.GetJobIcon(job);
        if (icon != null)
        {
            var iconPos = new Vector2(p0.X + style.FramePadding.X, p0.Y + (height - iconSize.Y) * 0.5f);
            ImGui.GetWindowDrawList().AddImage(icon.Handle, iconPos, iconPos + iconSize);
        }

        return clicked;
    }

    private void DrawAddJobPopup(Guid id)
    {
        using var popup = ImRaii.Popup(AddJobPopupId);
        if (!popup.Success)
            return;

        var existing = new HashSet<uint>(plugin.Configuration.GetJobAssociations(id));
        var selectable = plugin.GameData.GetSelectableJobs().Where(j => !existing.Contains(j.RowId)).ToList();

        if (selectable.Count == 0)
        {
            ImGui.TextDisabled("All jobs are associated.");
            return;
        }

        var lineH = ImGui.GetTextLineHeight();
        JobRole? lastRole = null;
        foreach (var job in selectable)
        {
            if (lastRole != job.Role)
            {
                if (lastRole != null)
                    ImGui.Spacing();
                ImGui.TextDisabled(GameDataService.RoleLabel(job.Role));
                lastRole = job.Role;
            }

            var icon = plugin.GameData.GetJobIcon(job.RowId);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(lineH, lineH));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            }

            if (ImGui.Selectable($"{job.Name}##addjob{job.RowId}"))
            {
                var updated = plugin.Configuration.GetJobAssociations(id).ToList();
                updated.Add(job.RowId);
                plugin.Configuration.SetJobAssociations(id, updated);
                plugin.Configuration.Save();
                jobAssociationVersion++;
                ImGui.CloseCurrentPopup();
            }
        }
    }
}
