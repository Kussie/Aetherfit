using System.Numerics;
using Dalamud.Interface.Utility;

namespace Aetherfit.Ui;

// Single source of truth for the colours, rounding, and scales shared across Aetherfit's windows. These were
// previously copy-pasted as raw Vector4 literals (or re-declared per file); keep new shared values here.
internal static class UiTheme
{
    // Warm gold used for headers, selection highlights, and the splitter accent.
    public static readonly Vector4 GoldAccent = new(1.0f, 0.85f, 0.4f, 1.0f);

    // "Pill" chips (tags, job associations, login tags): base / hover (remove) / active.
    public static readonly Vector4 PillBase = new(0.22f, 0.38f, 0.60f, 0.72f);
    public static readonly Vector4 PillHovered = new(0.55f, 0.20f, 0.20f, 0.85f);
    public static readonly Vector4 PillActive = new(0.65f, 0.14f, 0.14f, 1.00f);

    // Gallery thumbnail placeholder (the "No Image" box) background and text.
    public static readonly Vector4 PlaceholderBg = new(0.22f, 0.22f, 0.25f, 1f);
    public static readonly Vector4 PlaceholderText = new(0.65f, 0.65f, 0.68f, 1f);

    // Detail-panel section heading text and the mod "open in Penumbra" link text.
    public static readonly Vector4 SectionHeader = new(0.85f, 0.85f, 0.85f, 1.0f);
    public static readonly Vector4 ModLink = new(0.55f, 0.78f, 1.0f, 1.0f);

    // Font scale used for the large window headers.
    public const float HeaderFontScale = 1.25f;

    // Corner radius for pill chips (scaled to the current global UI scale).
    public static float PillRounding => 8f * ImGuiHelpers.GlobalScale;
}
