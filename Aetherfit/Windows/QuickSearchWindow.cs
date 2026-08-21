using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// A command-palette style popup: type a design's name (or a tag) and Enter/click to apply. Opened via
// its own keybind - unlike every other window here, it isn't meant to linger, so it closes itself on
// Escape or as soon as it loses focus rather than waiting for the user to close it.
public sealed class QuickSearchWindow : Window, IDisposable
{
    private const int MaxDesignResults = 12;
    private const int MaxTagResults = 5;
    private const int MaxAllTagResults = 40;
    private const int MaxVisibleRows = 12;
    private const string TagBrowsePrefix = "tag:";
    private const float WidthPt = 480f;
    private const float ThumbnailMaxPt = 160f;

    // Exactly one of DesignId/Tag/Command is set - a specific design to apply, a tag to apply a random
    // match for, or a system command to run outright.
    private readonly record struct SearchResult(string Label, Guid? DesignId, string? Tag, Action? Command = null, string? Description = null)
    {
        public static SearchResult ForDesign(Guid id, string name) => new(name, id, null);
        public static SearchResult ForTag(string tag) => new($"Tag: {tag} (random pick)", null, tag);
        public static SearchResult ForCommand(string label, string description, Action command) => new(label, null, null, command, description);
    }

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
        var submitted = ImGui.InputTextWithHint("##quickSearchInput", "Type a design name or tag...", ref query, 128,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        var selectionMoved = false;
        if (query != lastQuery)
        {
            selectedIndex = 0;
            lastQuery = query;
            selectionMoved = true;
        }

        var matches = FindMatches();
        if (matches.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
            {
                selectedIndex = Math.Min(selectedIndex + 1, matches.Count - 1);
                selectionMoved = true;
            }
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
            {
                selectedIndex = Math.Max(selectedIndex - 1, 0);
                selectionMoved = true;
            }
        }
        selectedIndex = matches.Count == 0 ? 0 : Math.Clamp(selectedIndex, 0, matches.Count - 1);

        if (submitted && matches.Count > 0)
        {
            ApplyAndClose(matches[selectedIndex]);
            return;
        }

        ImGui.Spacing();

        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No matching designs, tags, or commands.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ImGui.TextDisabled("Type to search your designs or tags. Type \"tag:\" to browse every tag.");
                ImGui.Spacing();
            }

            // Capped so a long result list (e.g. "tag:" browsing everything) scrolls in place instead of
            // growing the window past the bottom of the screen with no way to reach the rest.
            var listHeight = Math.Min(matches.Count, MaxVisibleRows) * ImGui.GetTextLineHeightWithSpacing();
            using var scroll = ImRaii.Child("##qsResults", new Vector2(-1, listHeight), false);
            for (var i = 0; i < matches.Count; i++)
            {
                var result = matches[i];
                if (ImGui.Selectable($"{result.Label}##qs{i}", i == selectedIndex))
                    ApplyAndClose(result);
                if (ImGui.IsItemHovered())
                    DrawResultTooltip(result);
                if (i == selectedIndex && selectionMoved)
                    ImGui.SetScrollHereY();
            }
        }

        if (skipFocusCheckFrames > 0)
            skipFocusCheckFrames--;
        else if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            IsOpen = false;
    }

    private void DrawResultTooltip(SearchResult result)
    {
        if (result.DesignId is { } id)
        {
            var imagePath = plugin.Configuration.ShowThumbnailOnHover ? plugin.ImageStorage.GetCoverPath(id) : null;
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(result.Label);
            if (imagePath != null)
                DrawThumbnail(imagePath);
            ImGui.EndTooltip();
        }
        else if (result.Tag is { } tag)
        {
            var count = plugin.Configuration.CachedOutfits.Values.Count(o => TagMatching.AnyMatch(o.Tags, tag));
            ImGui.SetTooltip($"Applies a random design tagged \"{tag}\" ({count} matching).");
        }
        else if (result.Description is { } description)
        {
            ImGui.SetTooltip(description);
        }
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

    private void ApplyAndClose(SearchResult result)
    {
        if (result.Command is { } command)
        {
            command();
        }
        else if (result.DesignId is { } id)
        {
            plugin.DesignApply.ApplyDesignById(id);
        }
        else if (result.Tag is { } tag)
        {
            var err = plugin.MainWindow.ApplyRandomByTags(new[] { tag });
            if (err != null)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{err}");
        }

        IsOpen = false;
    }

    // Always available, filtered by label like everything else - lets a quick "revert" or "favourite"
    // resolve without having to know a design or tag name.
    private List<SearchResult> SystemCommands()
    {
        void ReportError(string? error)
        {
            if (error != null)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{error}");
        }

        return new List<SearchResult>
        {
            SearchResult.ForCommand("Apply Favourite", "Apply a random favourite design.",
                () => ReportError(plugin.MainWindow.ApplyRandomFavourite(matchCurrentJob: false))),
            SearchResult.ForCommand("Apply Last Known", "Reapply the last design you had worn.",
                () => ReportError(plugin.MainWindow.ReapplyLastWorn())),
            SearchResult.ForCommand("Revert", "Revert your character's appearance back to the game's state.",
                plugin.MainWindow.RevertAppearance),
        };
    }

    private List<SearchResult> FindMatches()
    {
        var trimmed = query.Trim();

        if (trimmed.StartsWith(TagBrowsePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var filter = trimmed[TagBrowsePrefix.Length..].Trim();
            return plugin.Configuration.DistinctSortedTags()
                .Where(t => filter.Length == 0 || t.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(SearchResult.ForTag)
                .ToList();
        }

        var results = SystemCommands()
            .Where(c => trimmed.Length == 0 || c.Label.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (trimmed.Length == 0)
            return results;

        results.AddRange(plugin.Configuration.DistinctSortedTags()
            .Where(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(MaxTagResults)
            .Select(SearchResult.ForTag));

        results.AddRange(plugin.Configuration.CachedOutfits
            .Where(kv => !plugin.Configuration.HiddenDesigns.Contains(kv.Key)
                        && plugin.Configuration.IsProviderEnabled(kv.Value.Source)
                        && kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDesignResults)
            .Select(kv => SearchResult.ForDesign(kv.Key, kv.Value.Name)));

        return results;
    }
}
