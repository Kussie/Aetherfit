using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawLoginSection();
    }

    private void DrawLoginSection()
    {
        var ps = Plugin.PlayerState;
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "On login");

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
    }

    private static void DrawCharacterLine()
    {
        var ps = Plugin.PlayerState;
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "Character:");
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
        var availableTags = plugin.Configuration.CachedOutfits.Values
            .SelectMany(o => o.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (availableTags.Count == 0)
        {
            ImGui.TextDisabled("No tags available — refresh outfits in the main window first.");
            return;
        }

        ImGui.TextDisabled("Pick tags (outfit must have all of these):");

        var size = new Vector2(0, 180 * ImGuiHelpers.GlobalScale);
        var changed = false;
        using (var scroll = ImRaii.Child("LoginTagsScroll", size, true))
        {
            if (scroll.Success)
            {
                foreach (var tag in availableTags)
                {
                    var selected = settings.LoginTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
                    if (ImGui.Checkbox(tag, ref selected))
                    {
                        if (selected)
                        {
                            if (!settings.LoginTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                                settings.LoginTags.Add(tag);
                        }
                        else
                        {
                            settings.LoginTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                        }
                        changed = true;
                    }
                }
            }
        }

        if (changed)
            plugin.Configuration.Save();
    }
}
