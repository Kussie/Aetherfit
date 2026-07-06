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

    // Tri-state status colours used by the equipment/customization detail panels (toggles, applied vs not).
    public static readonly Vector4 StateOn = new(0.30f, 0.78f, 0.30f, 1.0f);
    public static readonly Vector4 StateOff = new(0.88f, 0.32f, 0.32f, 1.0f);
    public static readonly Vector4 StateUnset = new(0.55f, 0.55f, 0.55f, 1.0f);
    public static readonly Vector4 AppliedText = new(1.0f, 1.0f, 1.0f, 1.0f);

    // Inline error text (e.g. a failed screenshot capture).
    public static readonly Vector4 ErrorText = new(1.0f, 0.5f, 0.5f, 1.0f);

    // Favourite star: filled gold when on. The "off" tint differs by context - a softer grey for the star
    // overlaid on a gallery thumbnail, a darker grey for the star button in the detail header.
    public static readonly Vector4 FavouriteStar = new(1.0f, 0.85f, 0.1f, 1.0f);
    public static readonly Vector4 FavouriteStarOff = new(0.7f, 0.7f, 0.72f, 0.9f);
    public static readonly Vector4 FavouriteButtonOff = new(0.45f, 0.45f, 0.48f, 1.0f);

    // Hidden eye: muted red when a design is hidden, otherwise the same greys as the favourite star -
    // a softer tint for the eye overlaid on a gallery thumbnail, a darker one for the detail-header button.
    public static readonly Vector4 HiddenEyeOn = new(0.90f, 0.45f, 0.45f, 1.0f);
    public static readonly Vector4 HiddenEyeOff = new(0.7f, 0.7f, 0.72f, 0.9f);
    public static readonly Vector4 HiddenButtonOff = new(0.45f, 0.45f, 0.48f, 1.0f);

    // Gallery cards: the faint plate behind each cell and the hover border (selection stays gold).
    public static readonly Vector4 CardBg = new(1f, 1f, 1f, 0.04f);
    public static readonly Vector4 CardHoverBorder = new(0.75f, 0.72f, 0.60f, 0.75f);

    // Faint vertical indent guides down the design tree.
    public static readonly Vector4 TreeGuide = new(0.5f, 0.5f, 0.5f, 0.6f);

    // The list/detail splitter bar when idle (turns gold on hover/drag).
    public static readonly Vector4 SplitterIdle = new(0.45f, 0.45f, 0.50f, 0.90f);

    // Translucent black laid over the main window's footer strip so the action buttons read as their own bar.
    public static readonly Vector4 FooterShade = new(0f, 0f, 0f, 0.30f);

    // A disabled / off pill-style toggle background.
    public static readonly Vector4 ToggleOffBg = new(0.20f, 0.20f, 0.22f, 0.6f);

    // Transparent "ghost" button hover/active states (the favourite star button in the detail header).
    public static readonly Vector4 GhostButtonHovered = new(1.0f, 1.0f, 1.0f, 0.08f);
    public static readonly Vector4 GhostButtonActive = new(1.0f, 1.0f, 1.0f, 0.15f);

    // The big "Glamourer Designs" header.
    public const float HeaderFontScale = 1.25f;

    // Pill corner radius, scaled with the UI.
    public static float PillRounding => 8f * ImGuiHelpers.GlobalScale;
}
