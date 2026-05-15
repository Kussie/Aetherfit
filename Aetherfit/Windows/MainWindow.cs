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

    private readonly FileDialogManager fileDialog = new();
    private const string ImageFilters = "Image{.png,.jpg,.jpeg,.webp}";

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
        RefreshDesigns();
    }

    public override void Draw()
    {
        var style = ImGui.GetStyle();
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
            var leftWidth = 260 * ImGuiHelpers.GlobalScale;

            using (var left = ImRaii.Child("OutfitTree", new Vector2(leftWidth, bodyHeight), true))
            {
                if (left.Success)
                    DrawLeftPane();
            }

            ImGui.SameLine();

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
        var result = plugin.Glamourer.FetchDesigns();
        if (result.Error != null)
        {
            root = new FolderNode();
            designsCount = 0;
            designsError = result.Error;
            return;
        }

        var newRoot = new FolderNode();
        foreach (var design in result.Designs)
        {
            var folderSegments = SplitFolderPath(design.FullPath);
            var node = newRoot;
            foreach (var segment in folderSegments)
            {
                if (!node.Folders.TryGetValue(segment, out var child))
                {
                    child = new FolderNode();
                    node.Folders[segment] = child;
                }
                node = child;
            }
            node.Designs.Add(new DesignLeaf(design.Id, design.DisplayName, design.FullPath, design.Color));
        }

        SortNodeDesigns(newRoot);
        root = newRoot;
        designsCount = result.Designs.Count;
        designsError = null;

        plugin.Configuration.CachedOutfits = result.Metadata;

        var validIds = new HashSet<Guid>(result.Designs.Select(d => d.Id));
        plugin.ImageStorage.CleanupRemovedDesigns(validIds);

        if (selectedDesign is { } sid && !validIds.Contains(sid))
            selectedDesign = null;

        plugin.Configuration.Save();
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

    private void DrawBottomButtons()
    {
        var style = ImGui.GetStyle();
        const string settingsLabel = "Settings";
        const string revertLabel = "Revert Appearance";
        const string applyLabel = "Apply Selected";
        const string randomLabel = "Apply Random";
        const string byTagLabel = "Apply Random By Tag(s)";

        var pad = style.FramePadding.X * 2 + 8 * ImGuiHelpers.GlobalScale;
        var settingsW = ImGui.CalcTextSize(settingsLabel).X + pad;
        var revertW = ImGui.CalcTextSize(revertLabel).X + pad;
        var applyW = ImGui.CalcTextSize(applyLabel).X + pad;
        var randomW = ImGui.CalcTextSize(randomLabel).X + pad;
        var byTagW = ImGui.CalcTextSize(byTagLabel).X + pad;
        var rightTotal = applyW + randomW + byTagW + 2 * style.ItemSpacing.X;

        if (ImGui.Button(settingsLabel, new Vector2(settingsW, 0)))
            plugin.ToggleConfigUi();
        ImGui.SameLine();

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
            if (err != null) Plugin.ChatGui.PrintError($"[Aetherfit] {err}");
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
        availableTagsForPopup = plugin.Configuration.CachedOutfits.Values
            .SelectMany(o => o.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            if (ImGui.Button("Apply Random Match"))
            {
                var err = ApplyRandomByTags(selectedTagsForApply);
                if (err != null) Plugin.ChatGui.PrintError($"[Aetherfit] {err}");
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

    private sealed class FolderNode
    {
        public SortedDictionary<string, FolderNode> Folders { get; } = new(NaturalStringComparer.OrdinalIgnoreCase);
        public List<DesignLeaf> Designs { get; } = new();
    }

    private sealed record DesignLeaf(Guid Id, string DisplayName, string FullPath, uint Color);

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer OrdinalIgnoreCase = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                var cx = x[i];
                var cy = y[j];

                if (char.IsDigit(cx) && char.IsDigit(cy))
                {
                    var xStart = i;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    var yStart = j;
                    while (j < y.Length && char.IsDigit(y[j])) j++;

                    var xDigit = xStart;
                    while (xDigit < i - 1 && x[xDigit] == '0') xDigit++;
                    var yDigit = yStart;
                    while (yDigit < j - 1 && y[yDigit] == '0') yDigit++;

                    var xLen = i - xDigit;
                    var yLen = j - yDigit;

                    if (xLen != yLen) return xLen - yLen;
                    for (var k = 0; k < xLen; k++)
                    {
                        var d = x[xDigit + k] - y[yDigit + k];
                        if (d != 0) return d;
                    }

                    var leadX = xDigit - xStart;
                    var leadY = yDigit - yStart;
                    if (leadX != leadY) return leadX - leadY;
                }
                else
                {
                    var ux = char.ToUpperInvariant(cx);
                    var uy = char.ToUpperInvariant(cy);
                    if (ux != uy) return ux - uy;
                    i++;
                    j++;
                }
            }

            return (x.Length - i) - (y.Length - j);
        }
    }
}
