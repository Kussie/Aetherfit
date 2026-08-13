using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// A command-palette style popup: type a design's name, arrow through matches, Enter/click to apply and
// dismiss. Opened via its own keybind - unlike every other window here, it isn't meant to linger, so it
// closes itself on Escape or as soon as it loses focus rather than waiting for the user to close it.
public sealed class QuickSearchWindow : Window, IDisposable
{
    private const int MaxResults = 12;
    private const float WidthPt = 480f;
    private const float ThumbnailMaxPt = 160f;

    private readonly Plugin plugin;
    private string query = string.Empty;
    private string lastQuery = string.Empty;
    private int selectedIndex;
    private bool reclaimFocus;
    // Grace period after opening before the focus-loss check can close it - BringToFront() doesn't
    // guarantee ImGui focus lands on frame one, so closing immediately on the same frame it opens would
    // make the window unusable.
    private int skipFocusCheckFrames;

    public QuickSearchWindow(Plugin plugin)
        : base("Quick Search##AetherfitQuickSearch",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
               | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove)
    {
        this.plugin = plugin;
        Size = new Vector2(WidthPt, 0);
        SizeCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public void Show()
    {
        query = string.Empty;
        lastQuery = string.Empty;
        selectedIndex = 0;
        reclaimFocus = true;
        skipFocusCheckFrames = 2;

        var display = ImGui.GetIO().DisplaySize;
        var width = WidthPt * ImGuiHelpers.GlobalScale;
        Position = new Vector2((display.X - width) * 0.5f, display.Y * 0.16f);
        PositionCondition = ImGuiCond.Always;

        IsOpen = true;
        BringToFront();
    }

    public override void Draw()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            IsOpen = false;
            return;
        }

        if (reclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            reclaimFocus = false;
        }

        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##quickSearchInput", "Type a design name...", ref query, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        if (query != lastQuery)
        {
            selectedIndex = 0;
            lastQuery = query;
        }

        var matches = FindMatches();
        if (matches.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
                selectedIndex = Math.Min(selectedIndex + 1, matches.Count - 1);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                selectedIndex = Math.Max(selectedIndex - 1, 0);
        }
        selectedIndex = matches.Count == 0 ? 0 : Math.Clamp(selectedIndex, 0, matches.Count - 1);

        if (submitted && matches.Count > 0)
        {
            ApplyAndClose(matches[selectedIndex].Id);
            return;
        }

        ImGui.Spacing();

        if (string.IsNullOrWhiteSpace(query))
            ImGui.TextDisabled("Type to search your designs.");
        else if (matches.Count == 0)
            ImGui.TextDisabled("No matching designs.");
        else
        {
            for (var i = 0; i < matches.Count; i++)
            {
                var (id, name) = matches[i];
                if (ImGui.Selectable($"{name}##qs{id}", i == selectedIndex))
                    ApplyAndClose(id);
                if (ImGui.IsItemHovered())
                    DrawResultTooltip(id, name);
                if (i == selectedIndex)
                    ImGui.SetScrollHereY();
            }
        }

        if (skipFocusCheckFrames > 0)
            skipFocusCheckFrames--;
        else if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            IsOpen = false;
    }

    private void DrawResultTooltip(Guid id, string name)
    {
        var imagePath = plugin.Configuration.ShowThumbnailOnHover ? plugin.ImageStorage.GetCoverPath(id) : null;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(name);
        if (imagePath != null)
            DrawThumbnail(imagePath);
        ImGui.EndTooltip();
    }

    private static void DrawThumbnail(string absolutePath)
    {
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading image...");
            return;
        }

        var (size, _) = GalleryDraw.ComputeFitSize(new Vector2(ThumbnailMaxPt, ThumbnailMaxPt) * ImGuiHelpers.GlobalScale, tex.Width, tex.Height);
        ImGui.Image(tex.Handle, size);
    }

    private void ApplyAndClose(Guid id)
    {
        plugin.DesignApply.ApplyDesignById(id);
        IsOpen = false;
    }

    private List<(Guid Id, string Name)> FindMatches()
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<(Guid, string)>();

        return plugin.Configuration.CachedOutfits
            .Where(kv => !plugin.Configuration.HiddenDesigns.Contains(kv.Key)
                        && plugin.Configuration.IsProviderEnabled(kv.Value.Source)
                        && kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(kv => (kv.Key, kv.Value.Name))
            .ToList();
    }
}
