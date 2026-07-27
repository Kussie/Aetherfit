using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Game;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Ui;

// The little tag/job chips shared across the windows.
internal static class Pills
{
    // The tag/job/mod picker button's label, shared by the local and shared galleries' filter rows.
    public static string TagJobFilterLabel(int count)
        => count == 0 ? "Filter by tag(s), job or mod..." : count == 1 ? "1 tag/job/mod filter active" : $"{count} tag/job/mod filters active";

    public static void DrawTagJobFilterList(
        IReadOnlyList<string> availableTags,
        IReadOnlyList<(uint RowId, string Name, JobRole? Role)> availableJobs,
        IReadOnlyList<(string Directory, string DisplayName)> availableMods,
        Dictionary<string, bool> filterTags,
        Dictionary<uint, bool> filterJobs,
        Dictionary<string, bool> filterMods,
        Func<uint, IDalamudTextureWrap?> getJobIcon,
        string idPrefix,
        float scrollWidth,
        string emptyMessage)
    {
        if (availableTags.Count == 0 && availableJobs.Count == 0 && availableMods.Count == 0)
        {
            ImGui.TextDisabled(emptyMessage);
        }
        else
        {
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            // Account for the "Tags"/"Jobs"/"Mods" headings and any role headings interleaved with the job rows.
            var jobRoleHeadings = availableJobs.Select(j => j.Role).Distinct().Count(r => r != null);
            var totalRows = (availableTags.Count > 0 ? availableTags.Count + 1 : 0)
                          + (availableJobs.Count > 0 ? availableJobs.Count + jobRoleHeadings + 1 : 0)
                          + (availableMods.Count > 0 ? availableMods.Count + 1 : 0);
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

                if (availableMods.Count > 0)
                {
                    if (availableTags.Count > 0 || availableJobs.Count > 0)
                        ImGui.Spacing();
                    ImGui.TextColored(UiTheme.SectionHeader, "Mods");
                    foreach (var mod in availableMods)
                        if (DrawFilterCheckbox(mod.DisplayName, filterMods.GetFilterState(mod.Directory), $"{idPrefix}ModCb{mod.Directory}"))
                            filterMods.CycleFilterState(mod.Directory);
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
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding);
        using var colors = ImRaii.PushColor(ImGuiCol.Button, selected ? UiTheme.PillBase : UiTheme.ToggleOffBg)
            .Push(ImGuiCol.ButtonHovered, UiTheme.PillHovered)
            .Push(ImGuiCol.ButtonActive, UiTheme.PillActive)
            .Push(ImGuiCol.Text, selected ? UiTheme.GoldAccent : UiTheme.PlaceholderText);
        return ImGui.Button($"{label}##pillToggle{id}");
    }

    // Vanilla/Modded are mutually exclusive - turning one on turns the other off (both can be off). Shared
    // by the local and shared galleries' own filter rows; each still places these however it needs
    // (SameLine-adjacent, or wrapped via PlaceItem), so the two toggles stay separately callable rather
    // than being bundled into one "draw both" function.
    public static void DrawVanillaToggle(ref bool vanillaOnly, ref bool moddedOnly, string idPrefix)
    {
        if (DrawToggle("Vanilla", $"{idPrefix}vanillaFilter", vanillaOnly))
        {
            vanillaOnly = !vanillaOnly;
            if (vanillaOnly)
                moddedOnly = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only designs with no mod associations");
    }

    public static void DrawModdedToggle(ref bool vanillaOnly, ref bool moddedOnly, string idPrefix)
    {
        if (DrawToggle("Modded", $"{idPrefix}moddedFilter", moddedOnly))
        {
            moddedOnly = !moddedOnly;
            if (moddedOnly)
                vanillaOnly = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only designs with mod associations");
    }

    // A chip that reads "label ×". Returns true when clicked, i.e. the user wants it gone. id just keeps ImGui happy.
    public static bool DrawRemovable(string label, string id)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, UiTheme.PillRounding);
        using var colors = ImRaii.PushColor(ImGuiCol.Button, UiTheme.PillBase)
            .Push(ImGuiCol.ButtonHovered, UiTheme.PillHovered)
            .Push(ImGuiCol.ButtonActive, UiTheme.PillActive);
        return ImGui.Button($"{label} ×##pill{id}");
    }

    // Custom header to recover CollapsingHeader's framed look while keeping the label near-aligned with the
    // TextColored subheaders above it, otherwise the spacing looks off. Shared by every window that needs a
    // toggleable, framed subsection (the design detail panes, and the gallery grids' tag/job groupings).
    public static bool DrawCollapsibleSubheader(string label, ref bool open, string? helpText = null)
    {
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();

        var avail = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var rectH = lineH + style.FramePadding.Y * 2f;

        var rectMin = ImGui.GetCursorScreenPos();
        var rectMax = new Vector2(rectMin.X + avail, rectMin.Y + rectH);

        if (ImGui.InvisibleButton($"##sub_{label}", new Vector2(avail, rectH)))
            open = !open;

        var bg = ImGui.IsItemActive() ? ImGuiCol.HeaderActive
               : ImGui.IsItemHovered() ? ImGuiCol.HeaderHovered
               : ImGuiCol.Header;
        draw.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(bg), style.FrameRounding);

        var chevron = open ? "▼" : "▶";
        var chevSize = ImGui.CalcTextSize(chevron);
        var textY = rectMin.Y + (rectH - lineH) * 0.5f;
        draw.AddText(new Vector2(rectMax.X - chevSize.X - style.FramePadding.X, textY),
            ImGui.GetColorU32(UiTheme.SectionHeader), chevron);

        // Sit the help marker just left of the chevron.
        DrawSubheaderChrome(rectMin, rectMax, label, helpText, chevSize.X + style.FramePadding.X);

        return open;
    }

    // Shared chrome for both subheader variants: background is drawn by the caller (interactive vs.
    // static), this just places the label and an optional right-aligned "(?)" help marker + tooltip.
    // rightReserve leaves room for a caller-drawn glyph (the collapsible variant's chevron).
    public static void DrawSubheaderChrome(Vector2 rectMin, Vector2 rectMax, string label, string? helpText, float rightReserve = 0f)
    {
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();
        var lineH = ImGui.GetTextLineHeight();
        var textY = rectMin.Y + (rectMax.Y - rectMin.Y - lineH) * 0.5f;

        draw.AddText(new Vector2(rectMin.X + style.FramePadding.X, textY),
            ImGui.GetColorU32(UiTheme.SectionHeader), label);

        if (helpText == null)
            return;

        const string marker = "(?)";
        var markerSize = ImGui.CalcTextSize(marker);
        var markerPos = new Vector2(rectMax.X - rightReserve - markerSize.X - style.FramePadding.X, textY);
        draw.AddText(markerPos, ImGui.GetColorU32(ImGuiCol.TextDisabled), marker);

        var hoverMax = new Vector2(markerPos.X + markerSize.X, markerPos.Y + markerSize.Y);
        if (!ImGui.IsMouseHoveringRect(markerPos, hoverMax))
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
