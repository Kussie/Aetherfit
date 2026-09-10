using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// Small drawing helpers with no feature of their own - reused across several other MainWindow partials
// (design detail header, tree/Personas tooltips, layer/bulk-assign section headers), so they live here
// rather than under whichever feature happened to need one first.
public partial class MainWindow
{
    private const float TooltipImageMax = 160f;

    // Ghost icon button for a detail header. Every caller renders through here with the same font and
    // frame height so the glyphs line up; tint null keeps the normal text colour.
    private static bool HeaderIconButton(string id, FontAwesomeIcon icon, Vector4? tint, Vector2 size)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
            .Push(ImGuiCol.ButtonHovered, UiTheme.GhostButtonHovered)
            .Push(ImGuiCol.ButtonActive, UiTheme.GhostButtonActive)
            .Push(ImGuiCol.Text, tint);
        bool clicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);
        return clicked;
    }

    private static void DrawSubheader(string label, string? helpText = null)
    {
        // Mirrors Pills.DrawCollapsibleSubheader's framed look but is static (no chevron, no toggle).
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();

        var avail = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var rectH = lineH + style.FramePadding.Y * 2f;

        var rectMin = ImGui.GetCursorScreenPos();
        var rectMax = new Vector2(rectMin.X + avail, rectMin.Y + rectH);

        ImGui.Dummy(new Vector2(avail, rectH));
        draw.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(ImGuiCol.Header), style.FrameRounding);

        Pills.DrawSubheaderChrome(rectMin, rectMax, label, helpText);
    }

    private static bool DrawImageScaled(string absolutePath, float maxSide, bool clickable = false, string? title = null)
    {
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading image...");
            return false;
        }

        var (size, _) = GalleryDraw.ComputeFitSize(new Vector2(maxSide, maxSide), tex.Width, tex.Height);
        ImGui.Image(tex.Handle, size);

        if (!clickable)
            return false;

        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (title != null)
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(UiTheme.GoldAccent, title);
                ImGui.TextUnformatted("Left-click to view full size");
                ImGui.TextUnformatted("Shift + right-click to remove");
                ImGui.EndTooltip();
            }
            else
            {
                ImGui.SetTooltip("Left-click to view full size\nShift + right-click to remove");
            }
        }
        // Fire on release rather than press so grabbing the cover to drag it doesn't also open the viewer.
        return hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
    }
}
