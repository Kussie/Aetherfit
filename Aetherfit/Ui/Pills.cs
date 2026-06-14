using Dalamud.Bindings.ImGui;

namespace Aetherfit.Ui;

// The little tag/job chips shared across the windows.
internal static class Pills
{
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
