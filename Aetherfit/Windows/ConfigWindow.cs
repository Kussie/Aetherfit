using System;
using System.Collections.Generic;
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
    private readonly HashSet<string> loginTags;

    public ConfigWindow(Plugin plugin)
        : base("Aetherfit Settings###AetherfitConfig")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 340),
            MaximumSize = new Vector2(640, 800),
        };

        this.plugin = plugin;
        loginTags = new HashSet<string>(plugin.Configuration.LoginTags, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        // Re-sync from config in case it changed externally.
        loginTags.Clear();
        foreach (var t in plugin.Configuration.LoginTags)
            loginTags.Add(t);
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        var showThumb = cfg.ShowThumbnailOnHover;
        if (ImGui.Checkbox("Show outfit thumbnail on mouse-over", ref showThumb))
        {
            cfg.ShowThumbnailOnHover = showThumb;
            cfg.Save();
        }

        var defaultCover = cfg.DefaultToCoverMode;
        if (ImGui.Checkbox("Open the main window in Cover Mode by default", ref defaultCover))
        {
            cfg.DefaultToCoverMode = defaultCover;
            cfg.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "On login");
        ImGui.TextDisabled("Only one login action can be active.");
        ImGui.Spacing();

        if (ImGui.RadioButton("Do nothing", cfg.LoginAction == LoginAction.None))
            SetLoginAction(LoginAction.None);

        if (ImGui.RadioButton("Apply a random outfit", cfg.LoginAction == LoginAction.ApplyRandom))
            SetLoginAction(LoginAction.ApplyRandom);

        if (ImGui.RadioButton("Apply a random outfit by tag", cfg.LoginAction == LoginAction.ApplyRandomByTag))
            SetLoginAction(LoginAction.ApplyRandomByTag);

        if (cfg.LoginAction == LoginAction.ApplyRandomByTag)
        {
            ImGui.Indent();
            DrawLoginTagPicker();
            ImGui.Unindent();
        }
    }

    private void SetLoginAction(LoginAction action)
    {
        if (plugin.Configuration.LoginAction == action) return;
        plugin.Configuration.LoginAction = action;
        plugin.Configuration.Save();
    }

    private void DrawLoginTagPicker()
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
                    var selected = loginTags.Contains(tag);
                    if (ImGui.Checkbox(tag, ref selected))
                    {
                        if (selected) loginTags.Add(tag);
                        else loginTags.Remove(tag);
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            plugin.Configuration.LoginTags = loginTags.ToList();
            plugin.Configuration.Save();
        }
    }
}
