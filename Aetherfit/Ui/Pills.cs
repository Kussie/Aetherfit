using Dalamud.Bindings.ImGui;

namespace Aetherfit.Ui;

// Shared helpers for the small "pill" chips used for tags and jobs across the windows.
internal static class Pills
{
    // Wrapping placement: keeps the next pill on the current line if it fits within availRight, otherwise wraps.
    // `first`/`lineRight` are threaded across calls by the caller to track the running line width.
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

    // Draws a removable text pill rendered as "label ×" and returns true when clicked. `id` keeps the ImGui id unique.
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
