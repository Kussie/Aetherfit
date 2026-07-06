using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public partial class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private FolderNode root = new();
    private int designsCount;
    private string? designsError;
    private Guid? selectedDesign;
    private DesignLeaf? hoveredDesignForTooltip;

    // Session-only: the Edit Mode tree groups designs by job association or by tag instead of folder
    // path. At most one grouping is active at a time.
    private bool groupByJob;
    private bool groupByTags;

    // When a filter is active we force every matching tree node open and keep note of the previous state so it can be restored whe nthe filters are cleared
    private readonly Dictionary<uint, bool> treeOpenSnapshot = new();
    private bool wasFilterActive;
    // Set for the single frame after the filter changes. Pops matching nodes open to show results, but
    // otherwise stays out of the way so folders can still be collapsed.
    private bool expandTreesForFilter;
    private string filterSignature = string.Empty;

    private readonly FileDialogManager fileDialog = new();
    private const string ImageFilters = "Image{.png,.jpg,.jpeg,.webp}";

    // Applied at the top of the next Draw rather than immediately: OnOpen resets the view mode when
    // the window was closed, and this has to win over that.
    private Guid? pendingRevealDesign;

    private float leftPaneWidth = 260f;
    private const float MinLeftPaneWidth = 260f;

    private const string ApplyByTagPopupId = "ApplyRandomByTag";
    private List<string> availableTagsForPopup = new();
    private readonly HashSet<string> selectedTagsForApply = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(Plugin plugin)
        : base("Aetherfit##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        coverMode = plugin.Configuration.DefaultToCoverMode;
        groupByJob = false;
        groupByTags = false;
        RefreshDesigns();
    }

    public void OpenDesign(Guid id)
    {
        pendingRevealDesign = id;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (pendingRevealDesign is { } reveal)
        {
            selectedDesign = reveal;
            coverMode = false;
            pendingRevealDesign = null;
        }

        var style = ImGui.GetStyle();

        DrawTopToolbar();
        ImGui.Separator();

        var bottomRowHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y;
        var bodyHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - bottomRowHeight - style.ItemSpacing.Y);

        if (coverMode)
        {
            using (var full = ImRaii.Child("CoverModePane", new Vector2(0, bodyHeight), true))
            {
                if (full.Success)
                    DrawCoverModePane();
            }
        }
        else
        {
            var scale = ImGuiHelpers.GlobalScale;
            // Wide enough to hover without pixel-hunting; the visible line stays centred in it.
            var splitterW = 10f * scale;
            var maxLeft = Math.Max(MinLeftPaneWidth, Math.Min(460f, (ImGui.GetWindowSize().X / scale) - 200f));
            var actualLeftW = Math.Clamp(leftPaneWidth, MinLeftPaneWidth, maxLeft) * scale;

            using (var left = ImRaii.Child("OutfitTree", new Vector2(actualLeftW, bodyHeight), true))
            {
                if (left.Success)
                    DrawLeftPane();
            }

            ImGui.SameLine(0, 0);

            var splitterScreenPos = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##vSplit", new Vector2(splitterW, bodyHeight));
            var splitHovered = ImGui.IsItemHovered();
            var splitActive  = ImGui.IsItemActive();

            if (splitActive)
            {
                leftPaneWidth = Math.Clamp(
                    leftPaneWidth + (ImGui.GetIO().MouseDelta.X / scale),
                    MinLeftPaneWidth, maxLeft);
            }
            if (splitHovered || splitActive)
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);

            var lineX     = splitterScreenPos.X + (splitterW * 0.5f);
            var lineTop   = splitterScreenPos.Y;
            var lineBot   = splitterScreenPos.Y + bodyHeight;
            var lineColor = (splitHovered || splitActive)
                ? ImGui.ColorConvertFloat4ToU32(Ui.UiTheme.GoldAccent)
                : ImGui.ColorConvertFloat4ToU32(Ui.UiTheme.SplitterIdle);
            var lineThick = splitActive ? 2.5f : (splitHovered ? 2.0f : 1.5f);
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(lineX, lineTop), new Vector2(lineX, lineBot), lineColor, lineThick);

            ImGui.SameLine(0, 0);

            using (var right = ImRaii.Child("Right", new Vector2(0, bodyHeight), true))
            {
                if (right.Success)
                    DrawSelectedOutfitDetails();
            }
        }

        // Shade the footer strip (separator down to the window edge) so the action bar stands apart.
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(winPos.X, ImGui.GetCursorScreenPos().Y),
            winPos + winSize,
            ImGui.ColorConvertFloat4ToU32(Ui.UiTheme.FooterShade),
            ImGui.GetStyle().WindowRounding, ImDrawFlags.RoundCornersBottom);

        ImGui.Separator();
        DrawBottomButtons();
        DrawApplyByTagPopup();

        fileDialog.Draw();
    }

    private void RefreshDesigns()
    {
        designListGeneration++;
        var result = plugin.Glamourer.FetchDesigns();
        if (result.Error != null)
        {
            root = new FolderNode();
            designsCount = 0;
            designsError = result.Error;
            return;
        }

        root = BuildFolderTree(result.Designs
            .Select(d => new DesignLeaf(d.Id, d.DisplayName, d.FullPath, d.Color)));
        designsCount = result.Designs.Count;
        designsError = null;

        plugin.Configuration.CachedOutfits = result.Metadata;

        // Mods might have changed since last time, so clear the affected-item caches and let the
        // "(Appearance affected by ...)" notes rebuild from fresh Penumbra data.
        plugin.Penumbra.ClearChangedItemsCache();
        affectedByCache.Clear();

        var validIds = new HashSet<Guid>(result.Designs.Select(d => d.Id));
        plugin.ImageStorage.CleanupRemovedDesigns(validIds);

        var staleJobAssociations = plugin.Configuration.DesignJobAssociations.Keys
            .Where(k => !validIds.Contains(k))
            .ToList();
        foreach (var stale in staleJobAssociations)
            plugin.Configuration.DesignJobAssociations.Remove(stale);

        plugin.Configuration.FavouriteDesigns.RemoveWhere(id => !validIds.Contains(id));
        plugin.Configuration.HiddenDesigns.RemoveWhere(id => !validIds.Contains(id));
        CleanupStaleLayers(validIds);

        if (selectedDesign is { } sid && !validIds.Contains(sid))
            selectedDesign = null;

        plugin.Configuration.Save();
    }

    private static FolderNode BuildFolderTree(IEnumerable<DesignLeaf> leaves)
    {
        var newRoot = new FolderNode();
        foreach (var leaf in leaves)
        {
            var node = newRoot;
            foreach (var segment in SplitFolderPath(leaf.FullPath))
            {
                if (!node.Folders.TryGetValue(segment, out var child))
                {
                    child = new FolderNode();
                    node.Folders[segment] = child;
                }
                node = child;
            }
            node.Designs.Add(leaf);
        }

        SortNodeDesigns(newRoot);
        return newRoot;
    }

    private static IEnumerable<string> SplitFolderPath(string fullPath)
    {
        var parts = fullPath.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!string.IsNullOrEmpty(parts[i]))
                yield return parts[i];
        }
    }

    private static void SortNodeDesigns(FolderNode node)
    {
        node.Designs.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));
        foreach (var child in node.Folders.Values)
            SortNodeDesigns(child);
    }

    // The toolbar across the top, the same in both modes: data actions left, status and settings right.
    private void DrawTopToolbar()
    {
        var style = ImGui.GetStyle();

        if (IconTextButton(FontAwesomeIcon.Sync, "Refresh"))
            RefreshDesigns();
        ImGui.SameLine();

        if (IconTextButton(FontAwesomeIcon.Upload, "Export Gallery", dropdown: true))
            ImGui.OpenPopup("##sharePopup");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Export designs to a shareable .afgallery file (images + basic info).");
        DrawSharePopup();
        ImGui.SameLine();

        if (IconTextButton(FontAwesomeIcon.FolderOpen, "Open Shared Gallery..."))
            OpenImportGalleryDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open another user's exported .afgallery file in a read-only viewer.");

        var countText = designsCount == 1 ? "1 design" : $"{designsCount} designs";
        float gearW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            gearW = ImGui.CalcTextSize(FontAwesomeIcon.Cog.ToIconString()).X + (style.FramePadding.X * 2);
        var countW = ImGui.CalcTextSize(countText).X;

        ImGui.SameLine(ImGui.GetContentRegionMax().X - gearW - style.ItemSpacing.X - countW);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(countText);
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
            plugin.ToggleConfigUi();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");
    }

    private static float IconTextButtonWidth(FontAwesomeIcon icon, string label, bool dropdown = false)
    {
        var style = ImGui.GetStyle();
        float iconW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconW = ImGui.CalcTextSize(icon.ToIconString()).X;
        var caretW = dropdown ? style.ItemInnerSpacing.X + (ImGui.GetFontSize() * 0.5f) : 0f;
        return (style.FramePadding.X * 2) + iconW + style.ItemInnerSpacing.X + ImGui.CalcTextSize(label).X + caretW;
    }

    // Icon + label button, with a caret when it opens a menu. Drawn by hand because a single
    // ImGui button can't mix the icon font with the text font.
    private static bool IconTextButton(FontAwesomeIcon icon, string label, bool dropdown = false)
    {
        var style = ImGui.GetStyle();
        var iconStr = icon.ToIconString();
        float iconW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconW = ImGui.CalcTextSize(iconStr).X;
        var textW = ImGui.CalcTextSize(label).X;
        var caretHalf = ImGui.GetFontSize() * 0.25f;

        var clicked = ImGui.Button($"##iconText{label}", new Vector2(IconTextButtonWidth(icon, label, dropdown), 0));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var x = min.X + style.FramePadding.X;
        var textY = min.Y + style.FramePadding.Y;

        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            dl.AddText(new Vector2(x, textY), color, iconStr);
        x += iconW + style.ItemInnerSpacing.X;
        dl.AddText(new Vector2(x, textY), color, label);

        if (dropdown)
        {
            x += textW + style.ItemInnerSpacing.X;
            var centerY = (min.Y + max.Y) * 0.5f;
            dl.AddTriangleFilled(
                new Vector2(x, centerY - (caretHalf * 0.5f)),
                new Vector2(x + (caretHalf * 2f), centerY - (caretHalf * 0.5f)),
                new Vector2(x + caretHalf, centerY + (caretHalf * 0.75f)),
                color);
        }

        return clicked;
    }

    // The "Share your Designs" dropdown: export everything, or just the designs left after the active filters.
    private void DrawSharePopup()
    {
        using var popup = ImRaii.Popup("##sharePopup");
        if (!popup.Success)
            return;

        if (ImGui.Selectable("All Designs"))
            OpenExportGalleryDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Export every cached design.");

        // Filtered export is only meaningful while a filter is narrowing the list.
        var hasFilter = HasAnyFilter;
        using (ImRaii.Disabled(!hasFilter))
        {
            if (ImGui.Selectable("Filtered Designs"))
                OpenExportGalleryDialog(CollectVisibleDesignIds());
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(hasFilter
                ? "Export only the designs currently shown by the active filters."
                : "Set a filter first to export only the designs that remain visible.");
    }

    private void DrawBottomButtons()
    {
        var style = ImGui.GetStyle();
        const string applyLabel = "Apply Selected";
        const string randomLabel = "Apply Random";
        const string byTagLabel = "Apply Random By Tag(s)";

        var applyW = IconTextButtonWidth(FontAwesomeIcon.Check, applyLabel);
        var randomW = IconTextButtonWidth(FontAwesomeIcon.Random, randomLabel);
        var byTagW = IconTextButtonWidth(FontAwesomeIcon.Tags, byTagLabel);
        var rightTotal = applyW + randomW + byTagW + 2 * style.ItemSpacing.X;

        if (IconTextButton(FontAwesomeIcon.Undo, "Revert Appearance"))
            RevertAppearance();
        ImGui.SameLine();

        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, avail - rightTotal));

        var hasSelection = selectedDesign is { } sid
                        && plugin.Configuration.CachedOutfits.ContainsKey(sid);

        using (ImRaii.Disabled(!hasSelection))
        {
            if (IconTextButton(FontAwesomeIcon.Check, applyLabel) && selectedDesign is { } id)
                ApplyDesignById(id);
        }
        ImGui.SameLine();

        if (IconTextButton(FontAwesomeIcon.Random, randomLabel))
        {
            var err = ApplyRandomDesign();
            if (err != null) Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{err}");
        }
        ImGui.SameLine();

        var anyHasTags = plugin.Configuration.CachedOutfits.Values.Any(o => o.Tags.Count > 0);
        using (ImRaii.Disabled(!anyHasTags))
        {
            if (IconTextButton(FontAwesomeIcon.Tags, byTagLabel))
            {
                RebuildAvailableTags();
                ImGui.OpenPopup(ApplyByTagPopupId);
            }
        }
    }

    private void RebuildAvailableTags()
    {
        availableTagsForPopup = plugin.Configuration.DistinctSortedTags();

        selectedTagsForApply.RemoveWhere(t => !availableTagsForPopup.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    private void DrawApplyByTagPopup()
    {
        using var popup = ImRaii.Popup(ApplyByTagPopupId);
        if (!popup.Success)
            return;

        if (availableTagsForPopup.Count == 0)
        {
            ImGui.Text("No designs have any tags assigned in Glamourer.");
            ImGui.Spacing();
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.Text("Pick tags to match (all of):");
        ImGui.Separator();

        var scrollSize = new Vector2(
            220 * ImGuiHelpers.GlobalScale,
            200 * ImGuiHelpers.GlobalScale);

        using (var scroll = ImRaii.Child("TagsScroll", scrollSize, true))
        {
            if (scroll.Success)
            {
                foreach (var tag in availableTagsForPopup)
                {
                    var isSelected = selectedTagsForApply.Contains(tag);
                    if (ImGui.Checkbox(tag, ref isSelected))
                    {
                        if (isSelected) selectedTagsForApply.Add(tag);
                        else selectedTagsForApply.Remove(tag);
                    }
                }
            }
        }

        ImGui.Spacing();

        using (ImRaii.Disabled(selectedTagsForApply.Count == 0))
        {
            if (ImGui.Button("Apply Random Matching Design"))
            {
                var err = ApplyRandomByTags(selectedTagsForApply);
                if (err != null) Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{err}");
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    public void RevertAppearance()
    {
        plugin.Glamourer.Revert();

        // A deliberate revert means "I want my real gear" — forget the last-worn record so
        // LoginAction.ReapplyLast doesn't re-dress the character on the next login.
        if (Plugin.PlayerState.IsLoaded
            && plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            && settings.LastWornDesign != null)
        {
            settings.LastWornDesign = null;
            settings.LastWornLayers.Clear();
            plugin.Configuration.Save();
        }
    }

    private bool applyingLayer;

    private void ApplyDesignById(Guid id)
        => ApplyDesignCore(id, applyingLayer ? new List<Guid>() : PickLayers(id));

    private void ApplyDesignCore(Guid id, List<Guid> layerIds, bool quiet = false)
    {
        var name = plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.Name : id.ToString();
        if (!plugin.Glamourer.Apply(id, name, layerIds.Select(ResolveLinkedDesignName).ToList(), quiet))
            return;

        if (layerIds.Count > 0)
        {
            applyingLayer = true;
            try
            {
                foreach (var layerId in layerIds)
                    plugin.Glamourer.ApplyLayer(layerId);
            }
            finally { applyingLayer = false; }
        }

        RecordLastWorn(id, layerIds);
    }

    // Always records, regardless of the login-action setting, so enabling ReapplyLast later
    // works immediately. Recording at apply time also keeps the persisted record current at
    // logout without needing a logout hook.
    private void RecordLastWorn(Guid baseId, List<Guid> layerIds)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        settings.LastWornDesign = baseId;
        settings.LastWornLayers = new List<Guid>(layerIds);
        plugin.Configuration.Save();
    }

    public string? ReapplyLastWorn(bool quiet = false)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return "Log in to a character first.";

        if (!plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            || settings.LastWornDesign is not { } baseId)
            return "No previously worn design recorded for this character yet.";

        if (!plugin.Configuration.CachedOutfits.ContainsKey(baseId))
            return "Your previously worn design no longer exists in Glamourer — nothing reapplied.";

        var layers = plugin.Configuration.EnableRandomLayers
            ? settings.LastWornLayers.Where(l => plugin.Configuration.CachedOutfits.ContainsKey(l)).ToList()
            : new List<Guid>();
        if (layers.Count < settings.LastWornLayers.Count && plugin.Configuration.EnableRandomLayers)
            Plugin.Log.Info($"Skipped {settings.LastWornLayers.Count - layers.Count} previously worn layer(s) that no longer exist in Glamourer.");

        selectedDesign = baseId;
        ApplyDesignCore(baseId, layers, quiet);
        return null;
    }

    // Walks the base design's layer slots top-down, picking one job-matching design per slot (at random when
    // the slot holds several). Returns the layers to apply, in application order.
    private List<Guid> PickLayers(Guid baseId)
    {
        var picks = new List<Guid>();
        if (!plugin.Configuration.EnableRandomLayers || !Plugin.PlayerState.IsLoaded)
            return picks;

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        foreach (var slot in plugin.Configuration.GetLayerSlots(baseId))
        {
            var candidates = slot.Designs
                .Where(l => (l.AllJobs || l.Jobs.Contains(jobId))
                            && plugin.Configuration.CachedOutfits.ContainsKey(l.DesignId))
                .ToList();

            if (candidates.Count > 0)
                picks.Add(candidates[Random.Shared.Next(candidates.Count)].DesignId);
        }

        return picks;
    }

    public string? ApplyRandomDesign()
    {
        var ids = plugin.Configuration.CachedOutfits.Keys
            .Where(id => !plugin.Configuration.HiddenDesigns.Contains(id))
            .ToList();
        if (ids.Count == 0)
        {
            var msg = "No cached designs — open Aetherfit and click Refresh first.";
            Plugin.Log.Info(msg);
            return msg;
        }

        var pick = ids[Random.Shared.Next(ids.Count)];
        selectedDesign = pick;
        ApplyDesignById(pick);
        return null;
    }

    public string? ApplyRandomByTags(IReadOnlyCollection<string> tags, bool favouritesOnly = false)
    {
        if (tags.Count == 0)
        {
            var msg = "No tags provided.";
            Plugin.Log.Info(msg);
            return msg;
        }

        var matching = plugin.Configuration.CachedOutfits
            .Where(kv => !plugin.Configuration.HiddenDesigns.Contains(kv.Key)
                         && (!favouritesOnly || plugin.Configuration.FavouriteDesigns.Contains(kv.Key))
                         && tags.All(t => kv.Value.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
        {
            var msg = $"No {(favouritesOnly ? "favourite designs" : "designs")} match tags: {string.Join(", ", tags)}";
            Plugin.Log.Info(msg);
            return msg;
        }

        var pick = matching[Random.Shared.Next(matching.Count)];
        selectedDesign = pick;
        ApplyDesignById(pick);
        return null;
    }

    public string? ApplyRandomFavourite(bool matchCurrentJob)
    {
        var favourites = plugin.Configuration.FavouriteDesigns
            .Where(id => plugin.Configuration.CachedOutfits.ContainsKey(id)
                         && !plugin.Configuration.HiddenDesigns.Contains(id))
            .ToList();

        if (favourites.Count == 0)
        {
            var msg = "No favourite designs yet — click the ☆ star on a design first.";
            Plugin.Log.Info(msg);
            return msg;
        }

        if (matchCurrentJob)
        {
            if (!Plugin.PlayerState.IsLoaded)
            {
                var msg = "Log in to a character first.";
                Plugin.Log.Info(msg);
                return msg;
            }

            var jobId = Plugin.PlayerState.ClassJob.RowId;
            favourites = favourites
                .Where(id => plugin.Configuration.DesignJobAssociations.TryGetValue(id, out var jobs) && jobs.Contains(jobId))
                .ToList();

            if (favourites.Count == 0)
            {
                var jobName = plugin.GameData.ResolveJobName(jobId);
                var msg = $"No favourite designs associated with your current job ({jobName}).";
                Plugin.Log.Info(msg);
                return msg;
            }
        }

        var pick = favourites[Random.Shared.Next(favourites.Count)];
        selectedDesign = pick;
        ApplyDesignById(pick);
        return null;
    }

    public string? ApplyRandomByCurrentJob()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            var msg = "Log in to a character first.";
            Plugin.Log.Info(msg);
            return msg;
        }

        var jobId = Plugin.PlayerState.ClassJob.RowId;
        var matching = plugin.Configuration.DesignJobAssociations
            .Where(kv => kv.Value.Contains(jobId)
                         && plugin.Configuration.CachedOutfits.ContainsKey(kv.Key)
                         && !plugin.Configuration.HiddenDesigns.Contains(kv.Key))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
        {
            var jobName = plugin.GameData.ResolveJobName(jobId);
            var msg = $"No designs associated with your current job ({jobName}).";
            Plugin.Log.Info(msg);
            return msg;
        }

        var pick = matching[Random.Shared.Next(matching.Count)];
        selectedDesign = pick;
        ApplyDesignById(pick);
        return null;
    }

    private sealed class FolderNode
    {
        public SortedDictionary<string, FolderNode> Folders { get; } = new(NaturalStringComparer.OrdinalIgnoreCase);
        public List<DesignLeaf> Designs { get; } = new();
    }

    private sealed record DesignLeaf(Guid Id, string DisplayName, string FullPath, uint Color);
}
