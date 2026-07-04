using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Ui;

// The little tag/job chips shared across the windows.
internal static class Pills
{
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
