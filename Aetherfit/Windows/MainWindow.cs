using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    // Session-only: when true the Edit Mode tree groups designs by job association instead of folder path.
    private bool groupByJob;

    // When a filter is active we force every matching tree node open and keep note of the previous state so it can be restored whe nthe filters are cleared
    private readonly Dictionary<uint, bool> treeOpenSnapshot = new();
    private bool wasFilterActive;
    // Set for the single frame after the filter changes. Pops matching nodes open to show results, but
    // otherwise stays out of the way so folders can still be collapsed.
    private bool expandTreesForFilter;
    private string filterSignature = string.Empty;

    private readonly FileDialogManager fileDialog = new();
    private const string ImageFilters = "Image{.png,.jpg,.jpeg,.webp}";

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
        RefreshDesigns();
    }

    public override void Draw()
    {
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
            var splitterW = 5f * scale;
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

    // The toolbar across the top, the same in both modes: gallery actions, the design count, and settings.
    private void DrawTopToolbar()
    {
        if (ImGui.Button("Refresh"))
            RefreshDesigns();
        ImGui.SameLine();
        ImGui.TextDisabled($"{designsCount} design(s)");
        ImGui.SameLine();

        if (ImGui.Button("Share your Designs"))
            OpenExportGalleryDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Export this gallery to a shareable .afgallery file (images + basic info).");
        ImGui.SameLine();

        if (ImGui.Button("View Shared Designs"))
            OpenImportGalleryDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open another user's exported .afgallery file in a read-only viewer.");
        ImGui.SameLine();

        if (ImGui.Button("Settings"))
            plugin.ToggleConfigUi();
    }

    private void DrawBottomButtons()
    {
        var style = ImGui.GetStyle();
        const string revertLabel = "Revert Appearance";
        const string applyLabel = "Apply Selected";
        const string randomLabel = "Apply Random";
        const string byTagLabel = "Apply Random By Tag(s)";

        var pad = style.FramePadding.X * 2 + 8 * ImGuiHelpers.GlobalScale;
        var revertW = ImGui.CalcTextSize(revertLabel).X + pad;
        var applyW = ImGui.CalcTextSize(applyLabel).X + pad;
        var randomW = ImGui.CalcTextSize(randomLabel).X + pad;
        var byTagW = ImGui.CalcTextSize(byTagLabel).X + pad;
        var rightTotal = applyW + randomW + byTagW + 2 * style.ItemSpacing.X;

        if (ImGui.Button(revertLabel, new Vector2(revertW, 0)))
            RevertAppearance();
        ImGui.SameLine();

        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, avail - rightTotal));

        var hasSelection = selectedDesign is { } sid
                        && plugin.Configuration.CachedOutfits.ContainsKey(sid);

        using (ImRaii.Disabled(!hasSelection))
        {
            if (ImGui.Button(applyLabel, new Vector2(applyW, 0)) && selectedDesign is { } id)
                ApplyDesignById(id);
        }
        ImGui.SameLine();

        if (ImGui.Button(randomLabel, new Vector2(randomW, 0)))
        {
            var err = ApplyRandomDesign();
            if (err != null) Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{err}");
        }
        ImGui.SameLine();

        var anyHasTags = plugin.Configuration.CachedOutfits.Values.Any(o => o.Tags.Count > 0);
        using (ImRaii.Disabled(!anyHasTags))
        {
            if (ImGui.Button(byTagLabel, new Vector2(byTagW, 0)))
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

    public void RevertAppearance() => plugin.Glamourer.Revert();

    private void ApplyDesignById(Guid id)
    {
        var name = plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.Name : id.ToString();
        plugin.Glamourer.Apply(id, name);
    }

    public string? ApplyRandomDesign()
    {
        var ids = plugin.Configuration.CachedOutfits.Keys.ToList();
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

    public string? ApplyRandomByTags(IReadOnlyCollection<string> tags)
    {
        if (tags.Count == 0)
        {
            var msg = "No tags provided.";
            Plugin.Log.Info(msg);
            return msg;
        }

        var matching = plugin.Configuration.CachedOutfits
            .Where(kv => tags.All(t => kv.Value.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .Select(kv => kv.Key)
            .ToList();

        if (matching.Count == 0)
        {
            var msg = $"No designs match tags: {string.Join(", ", tags)}";
            Plugin.Log.Info(msg);
            return msg;
        }

        var pick = matching[Random.Shared.Next(matching.Count)];
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
            .Where(kv => kv.Value.Contains(jobId) && plugin.Configuration.CachedOutfits.ContainsKey(kv.Key))
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
