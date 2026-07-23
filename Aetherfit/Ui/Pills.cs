using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Ui;

// The little tag/job chips shared across the windows.
internal static class Pills
{
    // Shared body of a tri-state tag/job filter popup: a scrolling list of tag and job checkboxes
    // (jobs get role headings interleaved when Role is non-null), sized to fit up to ~12 rows, plus
    // the trailing Done button. Callers own opening the popup and drawing the search box above it -
    // this just renders whatever the caller has already filtered down to.
    public static void DrawTagJobFilterList(
        IReadOnlyList<string> availableTags,
        IReadOnlyList<(uint RowId, string Name, JobRole? Role)> availableJobs,
        Dictionary<string, bool> filterTags,
        Dictionary<uint, bool> filterJobs,
        Func<uint, IDalamudTextureWrap?> getJobIcon,
        string idPrefix,
        float scrollWidth,
        string emptyMessage)
    {
        if (availableTags.Count == 0 && availableJobs.Count == 0)
        {
            ImGui.TextDisabled(emptyMessage);
        }
        else
        {
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            // Account for the "Tags"/"Jobs" headings and any role headings interleaved with the job rows.
            var jobRoleHeadings = availableJobs.Select(j => j.Role).Distinct().Count(r => r != null);
            var totalRows = (availableTags.Count > 0 ? availableTags.Count + 1 : 0)
                          + (availableJobs.Count > 0 ? availableJobs.Count + jobRoleHeadings + 1 : 0);
            var listHeight = Math.Min(totalRows, 12) * rowHeight;

            using var scroll = ImRaii.Child($"{idPrefix}List", new Vector2(scrollWidth, listHeight), false);
            if (scroll.Success)
            {
                if (availableTags.Count > 0)
                {
                    ImGui.TextColored(UiTheme.SectionHeader, "Tags");
                    foreach (var tag in availableTags)
                        if (DrawFilterCheckbox(tag, filterTags.GetFilterState(tag), $"{idPrefix}TagCb{tag}"))
                            filterTags.CycleFilterState(tag);
                }

                if (availableJobs.Count > 0)
                {
                    if (availableTags.Count > 0)
                        ImGui.Spacing();
                    ImGui.TextColored(UiTheme.SectionHeader, "Jobs");

                    var lineH = ImGui.GetTextLineHeight();
                    JobRole? lastRole = null;
                    foreach (var job in availableJobs)
                    {
                        if (job.Role != null && lastRole != job.Role)
                        {
                            ImGui.TextDisabled(GameDataService.RoleLabel(job.Role.Value));
                            lastRole = job.Role;
                        }

                        var icon = getJobIcon(job.RowId);
                        if (DrawJobFilterCheckbox(job.Name, filterJobs.GetFilterState(job.RowId), icon, lineH, $"{idPrefix}JobCb{job.RowId}"))
                            filterJobs.CycleFilterState(job.RowId);
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 0)))
            ImGui.CloseCurrentPopup();
    }

    // Draws a wrapping row of removable "label ×" chips. Each chip shows a "Remove" tooltip on hover and,
    // when clicked, fires onRemove for that item (deferred until after the loop so the source isn't mutated
    // mid-iteration). Callers pass the items already in their desired order.
    public static void DrawRemovableRow<T>(IEnumerable<T> items, Func<T, string> label, Action<T> onRemove)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var framePadX = style.FramePadding.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;

        var first = true;
        var removed = false;
        T? toRemove = default;

        foreach (var item in items)
        {
            var text = label(item);
            var btnWidth = ImGui.CalcTextSize($"{text} ×").X + (framePadX * 2);
            PlaceItem(btnWidth, ref first, ref lineRight, cursorStart, spacing, availRight);

            if (DrawRemovable(text, text))
            {
                toRemove = item;
                removed = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Remove \"{text}\"");
        }

        if (removed)
            onRemove(toRemove!);
    }

    public static (FontAwesomeIcon Icon, Vector4 Color) FilterStateIcon(FilterState state) => state switch
    {
        FilterState.Include => (FontAwesomeIcon.CheckSquare, UiTheme.StateOn),
        FilterState.Exclude => (FontAwesomeIcon.MinusSquare, UiTheme.StateOff),
        _                   => (FontAwesomeIcon.Square, UiTheme.StateUnset),
    };

    public static string FilterStateTooltip(string label, FilterState state) => state switch
    {
        FilterState.Include => $"Including \"{label}\" — click to exclude it instead",
        FilterState.Exclude => $"Excluding \"{label}\" — click to clear",
        _                   => $"Click to require \"{label}\"",
    };

    // Draws a full-width Selectable and overlays drawContent on top of it, so the whole row (icon
    // included, not just whatever text drawContent renders) is the click target. Returns true when clicked.
    public static bool DrawOverlaySelectable(string id, Action drawContent, string? tooltip = null)
    {
        var rowStart = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable($"##{id}");
        var hovered = ImGui.IsItemHovered();
        var rowEnd = ImGui.GetCursorPos();

        ImGui.SetCursorPos(rowStart);
        drawContent();
        ImGui.SetCursorPos(rowEnd);

        if (hovered && tooltip != null)
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    // A tri-state checkbox (unset/include/exclude) with `label` as its text. Returns true when clicked,
    // i.e. the caller should cycle the filter state.
    public static bool DrawFilterCheckbox(string label, FilterState state, string id)
    {
        var (icon, color) = FilterStateIcon(state);
        return DrawOverlaySelectable(id, () =>
        {
            DesignDetailView.DrawFontAwesome(icon, color);
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextUnformatted(label);
        }, FilterStateTooltip(label, state));
    }

    // Same as DrawFilterCheckbox, with a job icon between the checkbox and the name.
    public static bool DrawJobFilterCheckbox(string name, FilterState state, IDalamudTextureWrap? icon, float lineHeight, string id)
    {
        var (stateIcon, color) = FilterStateIcon(state);
        return DrawOverlaySelectable(id, () =>
        {
            DesignDetailView.DrawFontAwesome(stateIcon, color);
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(lineHeight, lineHeight));
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            }
            ImGui.TextUnformatted(name);
        }, FilterStateTooltip(name, state));
    }

    // Lays pills out left to right and wraps to a new line when the next one won't fit. The caller hangs onto
    // first/lineRight between calls so we know where the current line ended.
    public static void PlaceItem(float width, ref bool first, ref float lineRight,
        float cursorStart, float spacing, float availRight)
    {
        if (first)
        {
            lineRight = cursorStart + width;
            first = false;
        }
        else if (lineRight + spacing + width <= availRight)
        {
            ImGui.SameLine();
            lineRight += spacing + width;
        }
        else
        {
            lineRight = cursorStart + width;
        }
    }

    // A chip that toggles on/off, coloured like the D/M/E scope toggles. Returns true when clicked.
    public static bool DrawToggle(string label, string id, bool selected)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding);
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? UiTheme.PillBase : UiTheme.ToggleOffBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.PillHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.PillActive);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? UiTheme.GoldAccent : UiTheme.PlaceholderText);
        var clicked = ImGui.Button($"{label}##pillToggle{id}");
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
        return clicked;
    }

    // A chip that reads "label ×". Returns true when clicked, i.e. the user wants it gone. id just keeps ImGui happy.
    public static bool DrawRemovable(string label, string id)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding);
        ImGui.PushStyleColor(ImGuiCol.Button, UiTheme.PillBase);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiTheme.PillHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UiTheme.PillActive);
        var clicked = ImGui.Button($"{label} ×##pill{id}");
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        return clicked;
    }
}
