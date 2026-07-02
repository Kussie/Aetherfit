using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string LoginTagsPopupId = "LoginTagsPopup";
    private string loginTagSearchText = string.Empty;
    private List<string> availableLoginTags = [];

    public ConfigWindow(Plugin plugin)
        : base("Aetherfit Settings###AetherfitConfig")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 340),
            MaximumSize = new Vector2(640, 800),
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        DrawCharacterLine();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(UiTheme.SectionHeader, "General");
        ImGui.Spacing();

        var showThumb = cfg.ShowThumbnailOnHover;
        if (ImGui.Checkbox("Show outfit thumbnail on mouse-over", ref showThumb))
        {
            cfg.ShowThumbnailOnHover = showThumb;
            cfg.Save();
        }

        var defaultCover = cfg.DefaultToCoverMode;
        if (ImGui.Checkbox("Open the main window in Gallery Mode by default", ref defaultCover))
        {
            cfg.DefaultToCoverMode = defaultCover;
            cfg.Save();
        }

        ImGui.TextDisabled("Gallery image fit:");
        ImGui.SameLine();
        var fitIdx = (int)cfg.GalleryFitMode;
        var fitOptions = new[] { "Crop", "Letterbox", "Stretch" };
        ImGui.PushItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.Combo("##galleryFit", ref fitIdx, fitOptions, fitOptions.Length))
        {
            cfg.GalleryFitMode = (GalleryFitMode)fitIdx;
            cfg.Save();
        }
        ImGui.PopItemWidth();

        var enableLayers = cfg.EnableRandomLayers;
        if (ImGui.Checkbox("Enable Random Layer Designs", ref enableLayers))
        {
            cfg.EnableRandomLayers = enableLayers;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on, applying a base design also rolls one of its configured layers at random.\nWhen off, the Random Layer Designs panel is hidden and no layers are applied.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawLoginSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCommandsSection();
    }

    private static void DrawCommandsSection()
    {
        ImGui.TextColored(UiTheme.SectionHeader, "Chat commands");
        ImGui.TextDisabled("These can also be used in game macros.");
        ImGui.Spacing();

        DrawCommand("/aetherfit", "Open or close the main Aetherfit window.");
        DrawCommand("/aetherfit random", "Apply a random design from your entire collection.");
        DrawCommand("/aetherfit tag <tag1,tag2,...>",
            "Apply a random design that has all of the listed tags. Separate multiple tags with commas.");
        DrawCommand("/aetherfit job",
            "Apply a random design associated with your current job. Set associations per-design in the design details pane.");
        DrawCommand("/aetherfit revert", "Revert your character's appearance back to the game's state.");
    }

    private static void DrawCommand(string command, string description)
    {
        ImGui.TextColored(UiTheme.GoldAccent, command);
        ImGui.Indent();
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.TextWrapped(description);
        ImGui.PopStyleColor();
        ImGui.Unindent();
        ImGui.Spacing();
    }

    private void DrawLoginSection()
    {
        var ps = Plugin.PlayerState;
        ImGui.TextColored(UiTheme.SectionHeader, "On login");

        if (!ps.IsLoaded)
        {
            ImGui.TextDisabled("Log in to a character to configure login actions.");
            return;
        }

        ImGui.TextDisabled("Settings below apply to this character only.");
        ImGui.Spacing();

        var settings = plugin.Configuration.GetOrCreateLoginSettings(ps.ContentId);

        if (ImGui.RadioButton("Do nothing", settings.LoginAction == LoginAction.None))
            SetLoginAction(settings, LoginAction.None);

        if (ImGui.RadioButton("Apply a random outfit", settings.LoginAction == LoginAction.ApplyRandom))
            SetLoginAction(settings, LoginAction.ApplyRandom);

        if (ImGui.RadioButton("Apply a random outfit by tag", settings.LoginAction == LoginAction.ApplyRandomByTag))
            SetLoginAction(settings, LoginAction.ApplyRandomByTag);

        if (settings.LoginAction == LoginAction.ApplyRandomByTag)
        {
            ImGui.Indent();
            DrawLoginTagPicker(settings);
            ImGui.Unindent();
        }

        // Popup must be called every frame so ImGui can manage its open/close state.
        DrawLoginTagsPopup(settings);
    }

    private static void DrawCharacterLine()
    {
        var ps = Plugin.PlayerState;
        ImGui.TextColored(UiTheme.SectionHeader, "Character:");
        ImGui.SameLine();
        if (!ps.IsLoaded)
        {
            ImGui.TextDisabled("(not logged in)");
            return;
        }

        var name = ps.CharacterName.ToString();
        var world = ps.HomeWorld.ValueNullable?.Name.ExtractText();
        var line = string.IsNullOrEmpty(world) ? name : $"{name} @ {world}";
        ImGui.TextUnformatted(line);
    }

    private void SetLoginAction(CharacterLoginSettings settings, LoginAction action)
    {
        if (settings.LoginAction == action) return;
        settings.LoginAction = action;
        plugin.Configuration.Save();
    }

    private void DrawLoginTagPicker(CharacterLoginSettings settings)
    {
        availableLoginTags = plugin.Configuration.DistinctSortedTags();

        if (availableLoginTags.Count == 0)
        {
            ImGui.TextDisabled("No tags available — refresh outfits in the main window first.");
            return;
        }

        ImGui.TextDisabled("Outfit must have all selected tags:");

        DrawLoginTagPills(settings);

        var btnLabel = settings.LoginTags.Count == 0 ? "Add tag..." : "Add another tag...";
        if (ImGui.Button(btnLabel, new Vector2(-1, 0)))
        {
            loginTagSearchText = string.Empty;
            ImGui.OpenPopup(LoginTagsPopupId);
        }
    }

    private void DrawLoginTagPills(CharacterLoginSettings settings)
    {
        if (settings.LoginTags.Count == 0) return;

        Pills.DrawRemovableRow(
            settings.LoginTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
            tag => tag,
            tag =>
            {
                settings.LoginTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                plugin.Configuration.Save();
            });
    }

    private void DrawLoginTagsPopup(CharacterLoginSettings settings)
    {
        using var popup = ImRaii.Popup(LoginTagsPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##loginTagSearch", "Search tags...", ref loginTagSearchText, 64);
        ImGui.Separator();

        var unselected = availableLoginTags
            .Where(t => !settings.LoginTags.Contains(t, StringComparer.OrdinalIgnoreCase) &&
                        (loginTagSearchText.Length == 0 ||
                         t.Contains(loginTagSearchText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (unselected.Count == 0)
        {
            ImGui.TextDisabled(loginTagSearchText.Length > 0 ? "No matching tags." : "All tags are selected.");
        }
        else
        {
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            var listHeight = Math.Min(unselected.Count, 8) * rowHeight;
            using var scroll = ImRaii.Child("LoginTagList", new Vector2(220 * ImGuiHelpers.GlobalScale, listHeight), false);
            if (scroll.Success)
            {
                foreach (var tag in unselected)
                {
                    if (ImGui.Selectable(tag))
                    {
                        settings.LoginTags.Add(tag);
                        plugin.Configuration.Save();
                        loginTagSearchText = string.Empty;
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 0)))
            ImGui.CloseCurrentPopup();
    }
}
