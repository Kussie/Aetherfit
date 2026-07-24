using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
        DrawCharacterLine();
        ImGui.Spacing();

        using var tabBar = ImRaii.TabBar("##settingsTabs");
        if (!tabBar.Success)
            return;

        DrawTab("General", DrawGeneralTab);
        DrawTab("Login & Zoning", DrawLoginSection);
        DrawTab("Commands", DrawCommandsSection);
        DrawTab("Integrations", DrawIntegrationsTab);
    }

    private static void DrawTab(string label, Action drawContent)
    {
        using var tab = ImRaii.TabItem(label);
        if (!tab.Success)
            return;

        using var child = ImRaii.Child($"##{label}Scroll", Vector2.Zero, false);
        if (!child.Success)
            return;

        ImGui.Spacing();
        drawContent();
    }

    private void DrawGeneralTab()
    {
        var cfg = plugin.Configuration;

        var showThumb = cfg.ShowThumbnailOnHover;
        if (ImGui.Checkbox("Show outfit thumbnail on mouse-over", ref showThumb))
        {
            cfg.ShowThumbnailOnHover = showThumb;
            cfg.Save();
        }

        var followSelection = cfg.ImageViewerFollowsSelection;
        if (ImGui.Checkbox("Image viewer follows the selected design", ref followSelection))
        {
            cfg.ImageViewerFollowsSelection = followSelection;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("While the image viewer window is open, selecting a different design\nswitches it to that design's cover image.");

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
        if (ImGui.Checkbox("Enable Additional Design Layers feature (Glamourer Designs only)", ref enableLayers))
        {
            cfg.EnableRandomLayers = enableLayers;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on, applying a base design also applies its configured layers top to bottom,\npicking one design at random from any layer that holds several.\nWhen off, the Additional Design Layers panel is hidden and no layers are applied.");
    }

    private static void DrawCommandsSection()
    {
        ImGui.TextDisabled("These can also be used in game macros.");
        ImGui.Spacing();

        DrawCommand("/aetherfit", "Open or close the main Aetherfit window.");
        DrawCommand("/aetherfit random", "Apply a random design from your entire collection.");
        DrawCommand("/aetherfit tag [favourite] <tag1,tag2,...>",
            "Apply a random design that has all of the listed tags. Separate multiple tags with commas. "
            + "Add \"favourite\" before the tags to only pick from your favourites.");
        DrawCommand("/aetherfit job",
            "Apply a random design associated with your current job. Set associations per-design in the design details pane.");
        DrawCommand("/aetherfit favourite [job]",
            "Apply a random favourite design. Add \"job\" to only pick favourites associated with your current job.");
        DrawCommand("/aetherfit last", "Reapply the last design you had worn.");
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

    private void DrawIntegrationsTab()
    {
        ImGui.TextDisabled("Status of the plugins Aetherfit integrates with.");
        ImGui.Spacing();

        DrawIntegrationRow("Penumbra", plugin.Penumbra.CheckIntegration(), PenumbraService.MinApiVersion);
        ImGui.Spacing();
        DrawIntegrationRow("Glamourer", plugin.Glamourer.CheckIntegration(), GlamourerService.MinApiVersion);
        ImGui.Spacing();
        DrawHardcodedIntegrationRow("Simple Glamour Switcher", "Integration coming soon");
    }

    private static void DrawIntegrationRow(string label, PluginIntegrationInfo info, (int Major, int Minor) required)
    {
        var ok = info.Status == PluginIntegrationStatus.Ok;
        DesignDetailView.DrawFontAwesome(ok ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, ok ? UiTheme.StateOn : UiTheme.StateOff);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);

        var status = info.Status switch
        {
            PluginIntegrationStatus.NotInstalled  => $"{label} is not installed.",
            PluginIntegrationStatus.NotLoaded     => $"{label} is installed but not enabled.",
            PluginIntegrationStatus.VersionTooLow => $"{label} API v{info.ApiVersion?.Major}.{info.ApiVersion?.Minor} found — v{required.Major}.{required.Minor}+ required.",
            _                                      => $"{label} v{info.PluginVersion} — OK.",
        };
        DrawIndentedDisabledText(status);
    }

    private static void DrawHardcodedIntegrationRow(string label, string status)
    {
        DesignDetailView.DrawFontAwesome(FontAwesomeIcon.Times, UiTheme.StateOff);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        DrawIndentedDisabledText(status);
    }

    private static void DrawIndentedDisabledText(string text)
    {
        ImGui.Indent();
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        ImGui.Unindent();
    }

    private void DrawLoginSection()
    {
        var ps = Plugin.PlayerState;

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

        if (ImGui.RadioButton("Reapply the last worn outfit", settings.LoginAction == LoginAction.ReapplyLast))
            SetLoginAction(settings, LoginAction.ReapplyLast);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts back exactly what you were wearing when you last applied a design with Aetherfit,\n"
                           + "including the same layers (no re-roll).\n"
                           + "Only designs applied via Aetherfit are tracked: applying a design directly in Glamourer\n"
                           + "resets the known design, as does reverting your appearance.");

        if (settings.LoginAction == LoginAction.ReapplyLast)
        {
            ImGui.Indent();
            DrawLastWornStatus(settings);
            ImGui.Unindent();
        }

        ImGui.Spacing();
        var reapplyOnZone = settings.ReapplyOnZoneChange;
        if (ImGui.Checkbox("Reapply last worn outfit after zone changes", ref reapplyOnZone))
        {
            settings.ReapplyOnZoneChange = reapplyOnZone;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Glamourer reverts manually applied designs when you change zones.\n"
                           + "When on, Aetherfit puts back the last design (and exact layers) you applied\n"
                           + "through Aetherfit after every zone change.\n"
                           + "Only designs applied via Aetherfit are restored: applying a design directly\n"
                           + "in Glamourer, or reverting, clears the record and stops the reapplying.");

        // Popup must be called every frame so ImGui can manage its open/close state.
        DrawLoginTagsPopup(settings);
    }

    private static void DrawCharacterLine()
    {
        var ps = Plugin.PlayerState;
        ImGui.TextColored(UiTheme.SectionHeader, "Current Character:");
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

    private void DrawLastWornStatus(CharacterLoginSettings settings)
    {
        if (settings.LastWornDesign is not { } lastWorn)
        {
            ImGui.TextDisabled("Nothing recorded yet — apply a design first.");
            return;
        }

        if (!plugin.Configuration.CachedOutfits.TryGetValue(lastWorn, out var outfit))
        {
            ImGui.TextDisabled("Last worn design no longer exists in Glamourer.");
            return;
        }

        ImGui.TextDisabled("Last worn:");
        ImGui.SameLine();
        DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, outfit.Name);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Open this design in Aetherfit");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                plugin.OpenDesignInMain(lastWorn);
        }

        if (settings.LastWornLayers.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(+{settings.LastWornLayers.Count} layer{(settings.LastWornLayers.Count == 1 ? "" : "s")})");
        }
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
