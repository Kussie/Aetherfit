using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

internal static class WindowExtensions
{
    // Real windows don't auto-position at the cursor the way a popup does, which reads as
    // "opens in the top-left corner" - this makes them appear where you actually clicked instead,
    // clamped so a fixed-size window never opens partly off-screen.
    public static void PositionNearMouse(this Window window)
    {
        var viewport = ImGui.GetMainViewport();
        var size = window.Size ?? new Vector2(340, 240);
        var mouse = ImGui.GetMousePos();
        var maxX = viewport.WorkPos.X + viewport.WorkSize.X - size.X;
        var maxY = viewport.WorkPos.Y + viewport.WorkSize.Y - size.Y;

        window.Position = new Vector2(
            Math.Clamp(mouse.X, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, maxX)),
            Math.Clamp(mouse.Y, viewport.WorkPos.Y, Math.Max(viewport.WorkPos.Y, maxY)));
        // Appearing (not Always) so a still-open window isn't yanked back to the mouse if Show() is
        // called again while it's already visible, and so the user can drag it afterward.
        window.PositionCondition = ImGuiCond.Appearing;
    }
}
