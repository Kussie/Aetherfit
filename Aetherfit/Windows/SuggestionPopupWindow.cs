using System;
using System.Numerics;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// Surfaces AutomationService.PendingSuggestion as a small, self-appearing popup near the top of the
// screen - the "ask first" counterpart to a Suggest-mode rule's silent AutoApply sibling. Nothing calls
// Show()/hide on this window; it opens and closes itself via PreOpenCheck (called every frame regardless
// of IsOpen), reacting purely to whether AutomationService currently has a pending suggestion.
public sealed class SuggestionPopupWindow : Window
{
    private const float WidthPt = 340f;
    private static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(15);

    private readonly Plugin plugin;
    private Guid? shownRuleId;
    private DateTime shownAtUtc;

    public SuggestionPopupWindow(Plugin plugin)
        : base("Aetherfit Suggestion##aetherfitSuggestion",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar
               | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
    }

    public override void PreOpenCheck()
    {
        var suggestion = plugin.Automation.PendingSuggestion;
        if (suggestion == null)
        {
            IsOpen = false;
            shownRuleId = null;
            return;
        }

        // A different rule's suggestion replacing an already-shown one restarts the auto-dismiss clock
        // and repositions/refocuses, same as a brand-new suggestion appearing.
        if (shownRuleId != suggestion.Value.RuleId)
        {
            shownRuleId = suggestion.Value.RuleId;
            shownAtUtc = DateTime.UtcNow;

            var display = ImGui.GetIO().DisplaySize;
            Position = new Vector2((display.X - (WidthPt * ImGuiHelpers.GlobalScale)) * 0.5f, display.Y * 0.08f);
            PositionCondition = ImGuiCond.Always;

            IsOpen = true;
            BringToFront();
        }

        if (DateTime.UtcNow - shownAtUtc >= AutoDismissAfter)
        {
            // Ignored long enough counts as a soft dismiss - starts the same cooldown an explicit
            // Dismiss click would, so it doesn't immediately reopen next poll.
            plugin.Automation.DismissSuggestion(suggestion.Value.RuleId);
            IsOpen = false;
        }
    }

    // A gently pulsing gold border and a tinted title bar - a plain window blends straight into the rest
    // of the game's UI chrome, and this is meant to actually get noticed. PreDraw/PostDraw wrap Begin/End,
    // which is the only place a window's own border/title bar colors can be overridden from the outside.
    private const int PushedColorCount = 3;

    public override void PreDraw()
    {
        var elapsed = (float)(DateTime.UtcNow - shownAtUtc).TotalSeconds;
        var pulse = (MathF.Sin(elapsed * MathF.PI * 2f / 1.2f) + 1f) * 0.5f; // 0..1, ~1.2s period
        var gold = UiTheme.GoldAccent;

        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(gold.X, gold.Y, gold.Z, 0.5f + (pulse * 0.5f)));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(gold.X * 0.4f, gold.Y * 0.32f, gold.Z * 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(gold.X * 0.4f, gold.Y * 0.32f, gold.Z * 0.12f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.5f * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(PushedColorCount);
        ImGui.PopStyleVar();
    }

    public override void Draw()
    {
        if (plugin.Automation.PendingSuggestion is not { } suggestion)
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            plugin.Automation.DismissSuggestion(suggestion.RuleId);
            IsOpen = false;
            return;
        }

        var designName = plugin.Configuration.ResolveDesignName(suggestion.DesignId);

        ImGui.SetWindowFontScale(1.2f);
        ImGui.TextColored(UiTheme.GoldAccent, designName);
        ImGui.SetWindowFontScale(1.0f);
        ImGui.TextDisabled(suggestion.RuleName);
        ImGui.Spacing();

        var buttonWidth = 110f * ImGuiHelpers.GlobalScale;
        using (ImRaii.PushColor(ImGuiCol.Button, UiTheme.StateOn))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(UiTheme.StateOn.X, UiTheme.StateOn.Y, UiTheme.StateOn.Z, 0.85f)))
        {
            if (ImGui.Button("Apply", new Vector2(buttonWidth, 0)))
            {
                plugin.DesignApply.ApplyDesignById(suggestion.DesignId);
                IsOpen = false;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Dismiss", new Vector2(buttonWidth, 0)))
        {
            plugin.Automation.DismissSuggestion(suggestion.RuleId);
            IsOpen = false;
        }
    }
}
