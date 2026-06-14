using System.Numerics;
using Dalamud.Interface.Utility;

namespace Aetherfit.Ui;

// Colours and sizes the windows share. Kept in one place so they don't slowly drift apart when reused.
internal static class UiTheme
{
    // Gold for headers, the selected-cell outline, and the splitter when you grab it.
    public static readonly Vector4 GoldAccent = new(1.0f, 0.85f, 0.4f, 1.0f);

    // Tag/job chips. Hover turns red because hovering a chip means "click to remove me".
    public static readonly Vector4 PillBase = new(0.22f, 0.38f, 0.60f, 0.72f);
    public static readonly Vector4 PillHovered = new(0.55f, 0.20f, 0.20f, 0.85f);
    public static readonly Vector4 PillActive = new(0.65f, 0.14f, 0.14f, 1.00f);

    // The "No Image" box behind empty thumbnails.
    public static readonly Vector4 PlaceholderBg = new(0.22f, 0.22f, 0.25f, 1f);
    public static readonly Vector4 PlaceholderText = new(0.65f, 0.65f, 0.68f, 1f);

    // Section headings, and the blue mod link in the detail panel.
    public static readonly Vector4 SectionHeader = new(0.85f, 0.85f, 0.85f, 1.0f);
    public static readonly Vector4 ModLink = new(0.55f, 0.78f, 1.0f, 1.0f);

    // The big "Glamourer Designs" header.
    public const float HeaderFontScale = 1.25f;

    // Pill corner radius, scaled with the UI.
    public static float PillRounding => 8f * ImGuiHelpers.GlobalScale;
}
