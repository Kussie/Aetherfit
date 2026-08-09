using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Integrations;
using Aetherfit.Services.Tagging;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Components;
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
        DrawTab("Tag Suggestions", DrawTagSuggestionsSection);
        DrawTab("Commands", DrawCommandsSection);
        DrawTab("Keybinds", DrawKeybindsSection);
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
        if (ImGui.Checkbox("Enable Additional Design Layers feature", ref enableLayers))
        {
            cfg.EnableRandomLayers = enableLayers;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on, applying a base design also applies its configured layers top to bottom,\npicking one design at random from any layer that holds several.\nWhen off, the Additional Design Layers panel is hidden and no layers are applied.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushColor(ImGuiCol.Button, UiTheme.KofiBase)
                   .Push(ImGuiCol.ButtonHovered, UiTheme.KofiHovered)
                   .Push(ImGuiCol.ButtonActive, UiTheme.KofiActive))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Coffee, "Support on Ko-fi", Vector2.Zero))
                Dalamud.Utility.Util.OpenLink("https://ko-fi.com/kussie");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tips and donations are entirely voluntary.");

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, UiTheme.PatreonBase)
                   .Push(ImGuiCol.ButtonHovered, UiTheme.PatreonHovered)
                   .Push(ImGuiCol.ButtonActive, UiTheme.PatreonActive))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.HandHoldingHeart, "Support on Patreon", Vector2.Zero))
                Dalamud.Utility.Util.OpenLink("https://www.patreon.com/Kussie");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Tips and donations are entirely voluntary.");

        ImGui.Spacing();
        ImGui.TextDisabled($"Aetherfit v{Plugin.Version}");
    }

    private void DrawTagSuggestionsSection()
    {
        var cfg = plugin.Configuration;
        var store = plugin.TagModel;

        var enabled = cfg.TagSuggestionEnabled;
        if (ImGui.Checkbox("Enable Tag Suggestion Feature", ref enabled))
        {
            cfg.TagSuggestionEnabled = enabled;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When off, the Suggest Tags button is hidden on designs and the options below are collapsed.");
        ImGui.Spacing();

        if (enabled)
        {
            ImGui.TextDisabled("Suggests tags for a design from its screenshots. Everything runs locally on your CPU;\nno images ever leave your machine.");
            ImGui.Spacing();

            DrawTagModelPicker(store);
            ImGui.Spacing();

            switch (store.CurrentState)
            {
                case TagModelStore.State.NotDownloaded:
                    ImGui.TextWrapped($"Requires a one-time download of ~{FormatMb(store.RemainingDownloadBytes)}.");
                    if (ImGui.Button("Download model"))
                        store.StartDownload();
                    break;

                case TagModelStore.State.Downloading:
                    var label = store.CurrentFile ?? "Preparing...";
                    ImGui.ProgressBar(store.Progress, new Vector2(-80 * ImGuiHelpers.GlobalScale, 0), label);
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel"))
                        store.CancelDownload();
                    break;

                case TagModelStore.State.Ready:
                    ImGui.TextDisabled($"Model ready — {FormatMb(store.TotalBytesOnDisk)} on disk in total.");

                    var threshold = cfg.TagSuggestionThreshold;
                    ImGui.PushItemWidth(200 * ImGuiHelpers.GlobalScale);
                    if (ImGui.SliderFloat("Suggestion confidence", ref threshold, 0.15f, 0.60f, "%.2f"))
                    {
                        cfg.TagSuggestionThreshold = threshold;
                        cfg.Save();
                    }
                    ImGui.PopItemWidth();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Lower values produce more (but noisier) suggestions.");

                    var composites = cfg.TagSuggestionComposites;
                    if (ImGui.Checkbox("Suggest composite tags", ref composites))
                    {
                        cfg.TagSuggestionComposites = composites;
                        cfg.Save();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Also suggests grouped \"category/type\" tags such as swimsuit/bikini\n"
                                       + "or japanese clothes/kimono, plus colour/... tags for coloured\n"
                                       + "garments (e.g. blue bikini also suggests colour/blue).");

                    using (ImRaii.Disabled(!composites))
                    {
                        var hideSources = cfg.TagSuggestionHideCompositeSources;
                        if (ImGui.Checkbox("Hide single tags that make up a composite tag", ref hideSources))
                        {
                            cfg.TagSuggestionHideCompositeSources = hideSources;
                            cfg.Save();
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("If \"tops/crop top\" is suggested, don't also suggest the plain\n"
                                       + "\"crop top\" tag it was built from.");
                    break;

                case TagModelStore.State.Failed:
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(UiTheme.ErrorText, store.LastError ?? "The download failed.");
                    ImGui.PopTextWrapPos();
                    if (ImGui.Button("Retry download"))
                        store.StartDownload();
                    break;
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawTagBlacklistEditor();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (store.CurrentState == TagModelStore.State.Ready)
        {
            var busy = plugin.TagSuggestions.IsBusy;
            using (ImRaii.Disabled(busy))
            {
                if (MainWindow.IconTextButton(FontAwesomeIcon.Trash, "Delete model files") && ImGui.GetIO().KeyShift)
                    store.DeleteAll();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(busy
                    ? "A suggestion is currently running."
                    : "Hold Shift and click to delete all downloaded models and the runtime.");
            ImGui.SameLine();
        }

        if (enabled)
            ImGui.TextDisabled("Models by SmilingWolf.");
    }

    private string blacklistAddText = string.Empty;

    private void DrawTagBlacklistEditor()
    {
        var cfg = plugin.Configuration;

        ImGui.TextDisabled("Never suggest these tags:");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("You can also Shift+right-click a suggested tag on a design to add it here.");

        if (cfg.TagSuggestionBlacklist.Count > 0)
        {
            void DrawPills() => Pills.DrawRemovableRow(
                cfg.TagSuggestionBlacklist.OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
                tag => tag,
                tag =>
                {
                    cfg.TagSuggestionBlacklist.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                    cfg.Save();
                });

            // Long lists scroll inside a fixed-height box instead of stretching the whole tab.
            if (cfg.TagSuggestionBlacklist.Count > 10)
            {
                var boxHeight = 5 * ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y;
                using var scroll = ImRaii.Child("##blacklistScroll", new Vector2(-1, boxHeight), true);
                if (scroll.Success)
                    DrawPills();
            }
            else
            {
                DrawPills();
            }
        }

        ImGui.PushItemWidth(180 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##blacklistAdd", "Add a tag to ignore...",
            ref blacklistAddText, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.PopItemWidth();
        ImGui.SameLine();
        submitted |= ImGui.SmallButton("Add##blacklist");

        if (submitted)
        {
            var tag = blacklistAddText.Trim();
            if (tag.Length > 0 && !cfg.TagSuggestionBlacklist.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                cfg.TagSuggestionBlacklist.Add(tag);
                cfg.Save();
            }
            blacklistAddText = string.Empty;
        }
    }

    private static void DrawTagModelPicker(TagModelStore store)
    {
        var selected = store.SelectedModel;

        ImGui.TextDisabled("Model:");
        ImGui.SameLine();
        // Switching mid-download would leave the progress UI pointing at the wrong model's state.
        using (ImRaii.Disabled(store.CurrentState == TagModelStore.State.Downloading))
        {
            ImGui.PushItemWidth(280 * ImGuiHelpers.GlobalScale);
            using (var combo = ImRaii.Combo("##tagModelPicker", ModelComboLabel(store, selected)))
            {
                if (combo.Success)
                {
                    foreach (var model in TagModelStore.Models)
                    {
                        if (ImGui.Selectable(ModelComboLabel(store, model), model.Id == selected.Id))
                            store.SelectModel(model.Id);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(model.Description);
                    }
                }
            }
            ImGui.PopItemWidth();
        }

        ImGui.TextDisabled(selected.Description);
    }

    private static string ModelComboLabel(TagModelStore store, TagModelStore.ModelInfo model)
    {
        var label = $"{model.DisplayName} — {FormatMb(model.ModelBytes)}";
        return store.IsModelDownloaded(model) ? $"{label} (downloaded)" : label;
    }

    private static string FormatMb(long bytes)
        => bytes >= 1000L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0 * 1024.0):0.0} GB"
            : $"{bytes / (1024.0 * 1024.0):0} MB";

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
        DrawCommand("/aetherfit wear \"design name\"",
            "Apply the design with this exact name. The name must be in quotes, even if it's a single word.");
        DrawCommand("/aetherfit last", "Reapply the last design you had worn.");
        DrawCommand("/aetherfit revert", "Revert your character's appearance back to the game's state.");
    }

    private static void DrawCommand(string command, string description)
    {
        ImGui.TextColored(UiTheme.GoldAccent, command);
        ImGui.Indent();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped(description);
        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Which of the four KeyBind instances (if any) is currently waiting for a key press. Reference
    // equality against Configuration's own KeyBind objects, not a copy.
    private KeyBind? recordingKeybind;

    private void DrawKeybindsSection()
    {
        var cfg = plugin.Configuration;

        ImGui.TextWrapped("Global hotkeys, detected while FFXIV itself has focus (not specifically an "
            + "Aetherfit window). Aetherfit only detects these key presses - it can't stop the game or "
            + "another plugin from also reacting to the same key, so pick one that isn't already bound "
            + "elsewhere.");
        ImGui.Spacing();

        DrawKeybindRow("Wear Random", cfg.WearRandomKeybind);
        DrawKeybindRow("Wear Favourite", cfg.WearFavouriteKeybind);
        DrawKeybindRow("Wear Last", cfg.WearLastKeybind);
        DrawKeybindRow("Revert", cfg.RevertKeybind);

        if (recordingKeybind != null)
            CaptureKeybind();
    }

    private void DrawKeybindRow(string label, KeyBind bind)
    {
        using var id = ImRaii.PushId(label);
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine(140 * scale);

        var recording = recordingKeybind == bind;
        if (ImGui.Button(recording ? "Press a key... (Esc to cancel)" : bind.ToString(), new Vector2(220 * scale, 0)))
            recordingKeybind = recording ? null : bind;

        ImGui.SameLine();
        using (ImRaii.Disabled(!bind.IsSet))
        {
            if (ImGui.Button("Clear"))
            {
                bind.Key = VirtualKey.NO_KEY;
                bind.Ctrl = bind.Alt = bind.Shift = false;
                plugin.Configuration.Save();
                if (recordingKeybind == bind)
                    recordingKeybind = null;
            }
        }
    }

    // Modifiers are read live (whatever's held when the first non-modifier key comes down), so
    // Ctrl+Alt+F1 records as Key=F1 with both modifiers set, not as capturing "Ctrl" itself as the key.
    private void CaptureKeybind()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            recordingKeybind = null;
            return;
        }

        var ctrl = Plugin.KeyState[VirtualKey.CONTROL];
        var alt = Plugin.KeyState[VirtualKey.MENU];
        var shift = Plugin.KeyState[VirtualKey.SHIFT];

        foreach (var key in Plugin.KeyState.GetValidVirtualKeys())
        {
            if (key is VirtualKey.NO_KEY or VirtualKey.CONTROL or VirtualKey.LCONTROL or VirtualKey.RCONTROL
                or VirtualKey.MENU or VirtualKey.LMENU or VirtualKey.RMENU
                or VirtualKey.SHIFT or VirtualKey.LSHIFT or VirtualKey.RSHIFT)
                continue;

            if (!Plugin.KeyState[key])
                continue;

            recordingKeybind!.Key = key;
            recordingKeybind!.Ctrl = ctrl;
            recordingKeybind!.Alt = alt;
            recordingKeybind!.Shift = shift;
            recordingKeybind!.WasDown = true;
            plugin.Configuration.Save();
            recordingKeybind = null;
            break;
        }
    }

    private void DrawIntegrationsTab()
    {
        ImGui.TextDisabled("Status of the plugins Aetherfit integrates with.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Required");
        ImGui.Spacing();
        DrawIntegrationRow("Penumbra", plugin.Penumbra.CheckIntegration(), PenumbraService.MinApiVersion);
        ImGui.Spacing();
        DrawIntegrationRow("Glamourer", plugin.Glamourer.CheckIntegration(), GlamourerService.MinApiVersion);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Optional");
        ImGui.Spacing();

        var glamaholicInfo = plugin.Glamaholic.CheckIntegration();
        DrawIntegrationRow("Glamaholic", glamaholicInfo,
            rightAligned: () => plugin.Configuration.GlamaholicEnabled = DrawRightAlignedCheckbox(
                "Glamaholic", plugin.Configuration.GlamaholicEnabled, "Source designs from Glamaholic",
                glamaholicInfo.Status == PluginIntegrationStatus.Ok),
            extra: plugin.Configuration.GlamaholicEnabled ? () =>
                plugin.Configuration.GlamaholicResetTemporarySettingsBeforeApply = DrawResetTemporarySettingsToggle(
                    "Glamaholic", plugin.Configuration.GlamaholicResetTemporarySettingsBeforeApply) : null);
        ImGui.Spacing();

        DrawGlamourPlateIntegrationRow();
        ImGui.Spacing();

        var sgsInfo = plugin.SimpleGlamourSwitcher.CheckIntegration();
        DrawIntegrationRow("Simple Glamour Switcher", sgsInfo,
            rightAligned: () => plugin.Configuration.SimpleGlamourSwitcherEnabled = DrawRightAlignedCheckbox(
                "SimpleGlamourSwitcher", plugin.Configuration.SimpleGlamourSwitcherEnabled, "Source designs from Simple Glamour Switcher",
                sgsInfo.Status == PluginIntegrationStatus.Ok),
            extra: plugin.Configuration.SimpleGlamourSwitcherEnabled ? () =>
                plugin.Configuration.SimpleGlamourSwitcherResetTemporarySettingsBeforeApply = DrawResetTemporarySettingsToggle(
                    "SimpleGlamourSwitcher", plugin.Configuration.SimpleGlamourSwitcherResetTemporarySettingsBeforeApply) : null);
    }

    // Right-aligned on whatever row it's drawn from, matching DrawFilterHeaderOverlay's "N active"/
    // Clear positioning technique. Takes/returns by value (not ref) so it can be called from a lambda.
    // Unticking excludes that source's designs from browsing, filtering, and random/direct apply
    // (see Configuration.IsProviderEnabled / DesignApplyService.IsUsable) without touching their
    // cached local metadata - they reappear intact if re-enabled. On by default.
    private bool DrawRightAlignedCheckbox(string idSuffix, bool enabled, string tooltip, bool integrationOk = true)
    {
        var size = ImGui.GetFrameHeight();
        ImGui.SameLine(ImGui.GetContentRegionMax().X - size);

        // Always switchable off, but can't be switched on while the integration itself has a problem
        // (not installed/loaded, wrong version) - there'd be nothing to actually source designs from.
        var canToggle = integrationOk || enabled;
        using (ImRaii.Disabled(!canToggle))
        {
            if (ImGui.Checkbox($"##enable{idSuffix}", ref enabled))
                plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(canToggle ? tooltip : $"{idSuffix} isn't available right now - see the status below.");
        return enabled;
    }

    // Not a plugin, so DrawIntegrationRow's "is not installed"/"is installed but not enabled"
    // phrasing doesn't fit - native game data that's only populated once the Dresser/Mirage Prism
    // Plate window has been touched that zone (see GlamourPlateService).
    private void DrawGlamourPlateIntegrationRow()
    {
        var ok = plugin.GlamourPlate.CheckIntegration().Status == PluginIntegrationStatus.Ok;
        DesignDetailView.DrawFontAwesome(ok ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, ok ? UiTheme.StateOn : UiTheme.StateOff);
        ImGui.SameLine();
        ImGui.TextUnformatted("Glamour Plates");
        // Unlike Glamaholic/SGS, "not ok" here just means "haven't opened the Glamour Dresser yet this
        // zone" - a transient, self-resolving state, not a missing integration - so the checkbox stays
        // always-togglable rather than blocking the user from turning on a setting that's fine to leave
        // on persistently regardless of per-zone load state.
        plugin.Configuration.GlamourPlateEnabled = DrawRightAlignedCheckbox(
            "GlamourPlate", plugin.Configuration.GlamourPlateEnabled, "Source designs from the game's own Glamour Plates");
        DrawIndentedDisabledText(ok
            ? "Glamour Plates loaded."
            : "Not yet loaded this zone — open the Glamour Dresser once.");
        if (plugin.Configuration.GlamourPlateEnabled)
            plugin.Configuration.GlamourPlateResetTemporarySettingsBeforeApply = DrawResetTemporarySettingsToggle(
                "GlamourPlate", plugin.Configuration.GlamourPlateResetTemporarySettingsBeforeApply);
    }

    private static void DrawIntegrationRow(string label, PluginIntegrationInfo info, (int Major, int Minor)? required = null,
        Action? rightAligned = null, Action? extra = null)
    {
        var ok = info.Status == PluginIntegrationStatus.Ok;
        DesignDetailView.DrawFontAwesome(ok ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, ok ? UiTheme.StateOn : UiTheme.StateOff);
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        rightAligned?.Invoke();

        var status = info.Status switch
        {
            PluginIntegrationStatus.NotInstalled  => $"{label} is not installed.",
            PluginIntegrationStatus.NotLoaded     => $"{label} is installed but not enabled.",
            PluginIntegrationStatus.VersionTooLow => required is { } r
                ? $"{label} API v{info.ApiVersion?.Major}.{info.ApiVersion?.Minor} found — v{r.Major}.{r.Minor}+ required."
                : $"{label} API version too low.",
            _                                      => $"{label} v{info.PluginVersion} — OK.",
        };
        DrawIndentedDisabledText(status);
        extra?.Invoke();
    }

    // Off by default - see Configuration.ResetTemporarySettingsBeforeApply.
    private bool DrawResetTemporarySettingsToggle(string idSuffix, bool enabled)
    {
        ImGui.Indent();
        if (ImGui.Checkbox($"Reset Penumbra temporary mod settings before applying##resetTemp{idSuffix}", ref enabled))
            plugin.Configuration.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears leftover Penumbra temporary mod settings (including Glamourer's own mod "
                + "associations) right before applying a design from this source. Doesn't run before additional layers.");
        ImGui.Unindent();
        return enabled;
    }

    private static void DrawIndentedDisabledText(string text)
    {
        ImGui.Indent();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped(text);
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
