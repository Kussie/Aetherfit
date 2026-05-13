using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly GetDesignListExtended getDesignListExtended;
    private readonly GetDesignJObject getDesignJObject;
    private readonly ApplyDesign applyDesign;

    private FolderNode root = new();
    private int designsCount;
    private string? designsError;

    private Guid? selectedDesign;

    private const string ApplyByTagPopupId = "ApplyRandomByTag";
    private List<string> availableTagsForPopup = new();
    private readonly HashSet<string> selectedTagsForApply = new(StringComparer.OrdinalIgnoreCase);

    private readonly FileDialogManager fileDialog = new();
    private const string ImageFilters = "Image{.png,.jpg,.jpeg,.webp}";
    private const float RightPaneImageMax = 220f;
    private const float TooltipImageMax = 160f;
    private const float AdditionalThumbSize = 72f;
    private const int MaxAdditionalImages = 5;
    private const string AdditionalImagesSubdir = "additional";
    private const string ImageHelpText = "Click an image to view it full size. Hold Shift and right-click to remove. \"+\" picks a file; \"Snap\" captures from the game.";

    private enum ImageFilterMode { All, HasImage, NoImage }
    private string filterName = string.Empty;
    private readonly HashSet<string> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private ImageFilterMode filterImage = ImageFilterMode.All;
    private const string FilterTagsPopupId = "FilterTagsPopup";
    private List<string> availableTagsForFilter = new();

    public MainWindow(Plugin plugin)
        : base("Aetherfit##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        getDesignListExtended = new GetDesignListExtended(Plugin.PluginInterface);
        getDesignJObject = new GetDesignJObject(Plugin.PluginInterface);
        applyDesign = new ApplyDesign(Plugin.PluginInterface);
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        RefreshDesigns();
    }

    private void RefreshDesigns()
    {
        try
        {
            var data = getDesignListExtended.Invoke();
            var newRoot = new FolderNode();
            var newCache = new Dictionary<Guid, CachedOutfit>();

            foreach (var (guid, tuple) in data)
            {
                var folderSegments = SplitFolderPath(tuple.FullPath);
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
                node.Designs.Add(new DesignLeaf(guid, tuple.DisplayName, tuple.FullPath, tuple.DisplayColor));

                try
                {
                    var jobject = getDesignJObject.Invoke(guid);
                    if (jobject != null)
                        newCache[guid] = ParseOutfit(jobject);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to cache metadata for design {Id}", guid);
                }
            }

            SortNodeDesigns(newRoot);
            root = newRoot;
            designsCount = data.Count;
            designsError = null;

            plugin.Configuration.CachedOutfits = newCache;
            plugin.Configuration.Save();
        }
        catch (Exception ex)
        {
            root = new FolderNode();
            designsCount = 0;
            designsError = ex.Message;
            Plugin.Log.Warning(ex, "Failed to fetch Glamourer designs");
        }
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

    public override void Draw()
    {
        var leftWidth = 260 * ImGuiHelpers.GlobalScale;

        using (var left = ImRaii.Child("OutfitTree", new Vector2(leftWidth, 0), true))
        {
            if (left.Success)
                DrawLeftPane();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("Right", Vector2.Zero, true))
        {
            if (right.Success)
                DrawRightPane();
        }

        fileDialog.Draw();
    }

    private void DrawLeftPane()
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), "Glamourer Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("Refresh"))
            RefreshDesigns();

        ImGui.SameLine();
        ImGui.TextDisabled($"{designsCount} design(s)");

        ImGui.Separator();

        if (designsError != null)
        {
            ImGui.TextWrapped("Glamourer is not available. Make sure it is installed and enabled.");
            ImGui.TextDisabled(designsError);
            return;
        }

        if (designsCount == 0)
        {
            ImGui.Text("No Glamourer designs found.");
            return;
        }

        DrawFilterUi();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using var treeChild = ImRaii.Child("OutfitTreeScroll", Vector2.Zero, false);
        if (treeChild.Success)
            DrawTree(root);
    }

    private void DrawFilterUi()
    {
        if (!ImGui.CollapsingHeader("Filters"))
            return;

        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##nameFilter", "Filter by name...", ref filterName, 64);
        ImGui.PopItemWidth();

        var tagsLabel = filterTags.Count == 0
            ? "Filter by tags..."
            : $"Tags: {filterTags.Count} selected";
        if (ImGui.Button(tagsLabel, new Vector2(-1, 0)))
        {
            RebuildAvailableFilterTags();
            ImGui.OpenPopup(FilterTagsPopupId);
        }

        ImGui.TextDisabled("Cover Image:");
        ImGui.SameLine();
        ImGui.PushItemWidth(-1);
        var imageIdx = (int)filterImage;
        var imageOptions = new[] { "All", "With image", "Without image" };
        if (ImGui.Combo("##imgFilter", ref imageIdx, imageOptions, imageOptions.Length))
            filterImage = (ImageFilterMode)imageIdx;
        ImGui.PopItemWidth();

        var hasAnyFilter = filterName.Length > 0
                        || filterTags.Count > 0
                        || filterImage != ImageFilterMode.All;
        using (ImRaii.Disabled(!hasAnyFilter))
        {
            if (ImGui.SmallButton("Clear filters"))
            {
                filterName = string.Empty;
                filterTags.Clear();
                filterImage = ImageFilterMode.All;
            }
        }

        DrawFilterTagsPopup();
    }

    private void RebuildAvailableFilterTags()
    {
        availableTagsForFilter = plugin.Configuration.CachedOutfits.Values
            .SelectMany(o => o.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void DrawFilterTagsPopup()
    {
        using var popup = ImRaii.Popup(FilterTagsPopupId);
        if (!popup.Success)
            return;

        if (availableTagsForFilter.Count == 0)
        {
            ImGui.Text("No tags available.");
            if (ImGui.Button("Close"))
                ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.Text("Show designs matching all of:");
        ImGui.Separator();

        var size = new Vector2(220 * ImGuiHelpers.GlobalScale, 200 * ImGuiHelpers.GlobalScale);
        using (var scroll = ImRaii.Child("FilterTagsScroll", size, true))
        {
            if (scroll.Success)
            {
                foreach (var tag in availableTagsForFilter)
                {
                    var sel = filterTags.Contains(tag);
                    if (ImGui.Checkbox(tag, ref sel))
                    {
                        if (sel) filterTags.Add(tag);
                        else filterTags.Remove(tag);
                    }
                }
            }
        }

        if (ImGui.Button("Clear"))
            filterTags.Clear();
        ImGui.SameLine();
        if (ImGui.Button("Done"))
            ImGui.CloseCurrentPopup();
    }

    private void DrawTree(FolderNode node)
    {
        foreach (var (name, folder) in node.Folders)
        {
            if (!FolderHasMatch(folder)) continue;
            if (ImGui.TreeNodeEx(name, ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                DrawTree(folder);
                ImGui.TreePop();
            }
        }

        foreach (var design in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var cached);
            if (!DesignMatchesFilters(design, cached)) continue;
            DrawDesignLeaf(design);
        }
    }

    private bool FolderHasMatch(FolderNode node)
    {
        foreach (var d in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(d.Id, out var c);
            if (DesignMatchesFilters(d, c)) return true;
        }
        foreach (var f in node.Folders.Values)
            if (FolderHasMatch(f)) return true;
        return false;
    }

    private bool DesignMatchesFilters(DesignLeaf design, CachedOutfit? cached)
    {
        if (filterName.Length > 0
            && design.DisplayName.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        if (filterTags.Count > 0)
        {
            if (cached == null || cached.Tags.Count == 0) return false;
            if (!filterTags.All(t => cached.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))) return false;
        }

        if (filterImage != ImageFilterMode.All)
        {
            var hasImage = plugin.Configuration.OutfitImages.ContainsKey(design.Id);
            if (filterImage == ImageFilterMode.HasImage && !hasImage) return false;
            if (filterImage == ImageFilterMode.NoImage && hasImage) return false;
        }

        return true;
    }

    private void DrawDesignLeaf(DesignLeaf design)
    {
        var hasColor = design.Color != 0;
        if (hasColor)
            ImGui.PushStyleColor(ImGuiCol.Text, design.Color);

        var selected = selectedDesign == design.Id;
        if (ImGui.Selectable($"{design.DisplayName}##{design.Id}", selected))
            selectedDesign = design.Id;

        if (hasColor)
            ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            DrawDesignLeafTooltip(design);
    }

    private void DrawDesignLeafTooltip(DesignLeaf design)
    {
        var imagePath = plugin.Configuration.ShowThumbnailOnHover ? GetOutfitImagePath(design.Id) : null;
        var hasPath = !string.IsNullOrEmpty(design.FullPath);
        if (!hasPath && imagePath == null)
            return;

        ImGui.BeginTooltip();
        if (hasPath)
            ImGui.TextUnformatted(design.FullPath);
        if (imagePath != null)
            DrawImageScaled(imagePath, TooltipImageMax * ImGuiHelpers.GlobalScale);
        ImGui.EndTooltip();
    }

    private void DrawRightPane()
    {
        var style = ImGui.GetStyle();
        var bottomRowHeight = ImGui.GetFrameHeight() + style.ItemSpacing.Y;

        var topHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - bottomRowHeight - style.ItemSpacing.Y);

        using (var top = ImRaii.Child("DesignTop", new Vector2(0, topHeight), false))
        {
            if (top.Success)
                DrawSelectedOutfitDetails();
        }

        ImGui.Separator();
        DrawBottomButtons();

        DrawApplyByTagPopup();
    }

    private void DrawSelectedOutfitDetails()
    {
        if (selectedDesign is not { } id)
        {
            ImGui.TextDisabled("Select a design on the left to see its details.");
            return;
        }

        if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var details))
        {
            ImGui.TextDisabled("No cached metadata for this design. Click Refresh.");
            return;
        }

        var datesLineCount = (details.CreatedAt.HasValue ? 1 : 0) + (details.LastEdit.HasValue ? 1 : 0);
        var datesBlockHeight = datesLineCount > 0
            ? datesLineCount * ImGui.GetTextLineHeightWithSpacing()
            : 0;

        var bodyHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - datesBlockHeight);

        using (var body = ImRaii.Child("DesignBody", new Vector2(0, bodyHeight), false))
        {
            if (body.Success)
            {
                ImGui.SetWindowFontScale(1.5f);
                ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), details.Name);
                ImGui.SetWindowFontScale(1.0f);
                ImGui.Spacing();

                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "Tags");
                ImGui.Indent();
                if (details.Tags.Count > 0)
                {
                    for (var i = 0; i < details.Tags.Count; i++)
                    {
                        if (i > 0) ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.55f, 0.78f, 1.0f, 1.0f), details.Tags[i]);
                    }
                }
                else
                {
                    ImGui.TextDisabled("No tags set in Glamourer");
                }
                ImGui.Unindent();
                ImGui.Spacing();

                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "Description");
                ImGui.Indent();
                if (!string.IsNullOrWhiteSpace(details.Description))
                    ImGui.TextWrapped(details.Description);
                else
                    ImGui.TextDisabled("No description set in Glamourer");
                ImGui.Unindent();
                ImGui.Spacing();

                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "Cover Image");
                DrawHelpMarker(ImageHelpText);
                ImGui.Indent();
                DrawOutfitImageBlock(id);
                ImGui.Unindent();
                ImGui.Spacing();

                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1.0f), "Additional Images");
                DrawHelpMarker(ImageHelpText);
                ImGui.Indent();
                DrawAdditionalImagesBlock(id);
                ImGui.Unindent();
            }
        }

        if (details.CreatedAt is { } created)
            DrawDateLine("Created", created);
        if (details.LastEdit is { } edited)
            DrawDateLine("Last edited", edited);
    }

    private static void DrawDateLine(string label, DateTimeOffset dt)
    {
        ImGui.TextDisabled($"{label}: {FormatFriendlyRelative(dt)}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FormatFullDate(dt));
    }

    private static string FormatFriendlyRelative(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.Now - dt;
        if (diff.TotalSeconds < 0) return FormatFullDate(dt);
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 2) return "a minute ago";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} minutes ago";
        if (diff.TotalHours < 2) return "an hour ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hours ago";
        if (diff.TotalDays < 2) return "yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 14) return "last week";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 60) return "last month";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        if (diff.TotalDays < 730) return "last year";
        return $"{(int)(diff.TotalDays / 365)} years ago";
    }

    private static string FormatFullDate(DateTimeOffset dt) =>
        dt.LocalDateTime.ToString("dddd, MMMM d, yyyy 'at' h:mm tt");

    private void DrawBottomButtons()
    {
        var style = ImGui.GetStyle();
        const string settingsLabel = "Settings";
        const string applyLabel = "Apply Selected";
        const string randomLabel = "Apply Random";
        const string byTagLabel = "Apply Random By Tag(s)";

        var pad = style.FramePadding.X * 2 + 8 * ImGuiHelpers.GlobalScale;
        var settingsW = ImGui.CalcTextSize(settingsLabel).X + pad;
        var applyW = ImGui.CalcTextSize(applyLabel).X + pad;
        var randomW = ImGui.CalcTextSize(randomLabel).X + pad;
        var byTagW = ImGui.CalcTextSize(byTagLabel).X + pad;
        var rightTotal = applyW + randomW + byTagW + 2 * style.ItemSpacing.X;

        if (ImGui.Button(settingsLabel, new Vector2(settingsW, 0)))
            plugin.ToggleConfigUi();
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

    private void ApplyDesignById(Guid id)
    {
        try
        {
            var result = applyDesign.Invoke(id, 0, 0, ApplyFlag.Equipment | ApplyFlag.Customization);
            var name = plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.Name : id.ToString();
            Plugin.ChatGui.Print($"[Aetherfit] Applied \"{name}\": {result}");
            Plugin.Log.Info("Applied design {Name} ({Id}): {Result}", name, id, result);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Apply failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to apply Glamourer design {Id}", id);
        }
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

    private void DrawOutfitImageBlock(Guid id)
    {
        var imagePath = GetOutfitImagePath(id);
        var deleteRequested = false;
        if (imagePath != null)
        {
            if (DrawImageScaled(imagePath, RightPaneImageMax * ImGuiHelpers.GlobalScale, clickable: true))
                plugin.ImageViewer.Show(imagePath);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                deleteRequested = true;
        }
        else
        {
            ImGui.TextDisabled("No image set");
        }

        if (imagePath == null)
        {
            ImGui.Spacing();

            var thumb = AdditionalThumbSize * ImGuiHelpers.GlobalScale;
            if (ImGui.Button("+##cover", new Vector2(thumb, thumb)))
                OpenImagePicker(id);

            ImGui.SameLine();
            if (ImGui.Button("Snap##cover", new Vector2(thumb, thumb)))
                plugin.ScreenshotSetup.Begin(croppedPath => SetOutfitImage(id, croppedPath));
        }

        if (deleteRequested)
            RemoveOutfitImage(id);
    }

    private void DrawAdditionalImagesBlock(Guid id)
    {
        var paths = GetAdditionalImagePaths(id);
        var thumb = AdditionalThumbSize * ImGuiHelpers.GlobalScale;
        var toRemoveIndex = -1;

        for (var i = 0; i < paths.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            using (ImRaii.PushId(i))
            {
                var clicked = DrawSquareThumbnail(paths[i], thumb, out var deleteRequested);
                if (clicked)
                    plugin.ImageViewer.Show(paths[i]);
                if (deleteRequested)
                    toRemoveIndex = i;
            }
        }

        if (paths.Count < MaxAdditionalImages)
        {
            if (paths.Count > 0)
                ImGui.SameLine();
            if (ImGui.Button("+", new Vector2(thumb, thumb)))
                OpenAdditionalImagePicker(id);

            ImGui.SameLine();
            if (ImGui.Button("Snap", new Vector2(thumb, thumb)))
                plugin.ScreenshotSetup.Begin(croppedPath => AddAdditionalImage(id, croppedPath));
        }

        if (toRemoveIndex >= 0)
            RemoveAdditionalImage(id, toRemoveIndex);
    }

    private static void DrawHelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered())
            return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static bool DrawImageScaled(string absolutePath, float maxSide, bool clickable = false)
    {
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.TextDisabled("Loading image...");
            return false;
        }

        var scale = Math.Min(maxSide / tex.Width, maxSide / tex.Height);
        ImGui.Image(tex.Handle, new Vector2(tex.Width * scale, tex.Height * scale));

        if (!clickable)
            return false;

        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    private static bool DrawSquareThumbnail(string absolutePath, float size, out bool deleteRequested)
    {
        deleteRequested = false;
        var tex = Plugin.TextureProvider.GetFromFile(absolutePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return false;
        }

        float uMin = 0f, uMax = 1f, vMin = 0f, vMax = 1f;
        if (tex.Width > tex.Height)
        {
            var keep = tex.Height / (float)tex.Width;
            uMin = (1f - keep) * 0.5f;
            uMax = 1f - uMin;
        }
        else if (tex.Height > tex.Width)
        {
            var keep = tex.Width / (float)tex.Height;
            vMin = (1f - keep) * 0.5f;
            vMax = 1f - vMin;
        }

        ImGui.Image(tex.Handle, new Vector2(size, size), new Vector2(uMin, vMin), new Vector2(uMax, vMax));

        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var leftClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
            deleteRequested = true;
        return leftClicked;
    }

    private void OpenImagePicker(Guid id)
    {
        fileDialog.OpenFileDialog(
            "Pick an image for this design",
            ImageFilters,
            (success, paths) =>
            {
                if (!success || paths.Count == 0)
                    return;
                SetOutfitImage(id, paths[0]);
            },
            1);
    }

    private void SetOutfitImage(Guid id, string sourcePath)
    {
        try
        {
            var imagesDir = EnsureImagesDirectory();
            DeleteImageFilesFor(id, imagesDir);

            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = ".png";
            var targetName = id.ToString("N") + ext;
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            plugin.Configuration.OutfitImages[id] = targetName;
            plugin.Configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Failed to set image: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to set image for {Id} from {Path}", id, sourcePath);
        }
    }

    private void RemoveOutfitImage(Guid id)
    {
        try
        {
            var imagesDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images");
            DeleteImageFilesFor(id, imagesDir);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete image file for {Id}", id);
        }

        if (plugin.Configuration.OutfitImages.Remove(id))
            plugin.Configuration.Save();
    }

    private string? GetOutfitImagePath(Guid id)
    {
        if (!plugin.Configuration.OutfitImages.TryGetValue(id, out var filename) || string.IsNullOrEmpty(filename))
            return null;
        var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images", filename);
        return File.Exists(path) ? path : null;
    }

    private static string EnsureImagesDirectory()
    {
        var dir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteImageFilesFor(Guid id, string imagesDir)
    {
        if (!Directory.Exists(imagesDir))
            return;
        var prefix = id.ToString("N");
        foreach (var file in Directory.EnumerateFiles(imagesDir, prefix + ".*"))
        {
            try { File.Delete(file); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to delete {File}", file); }
        }
    }

    private static string EnsureAdditionalImagesDirectory()
    {
        var dir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images", AdditionalImagesSubdir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private List<string> GetAdditionalImagePaths(Guid id)
    {
        var result = new List<string>();
        if (!plugin.Configuration.OutfitAdditionalImages.TryGetValue(id, out var filenames) || filenames.Count == 0)
            return result;

        var dir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images", AdditionalImagesSubdir);
        foreach (var name in filenames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                result.Add(path);
        }
        return result;
    }

    private void OpenAdditionalImagePicker(Guid id)
    {
        fileDialog.OpenFileDialog(
            "Pick an additional image",
            ImageFilters,
            (success, paths) =>
            {
                if (!success || paths.Count == 0)
                    return;
                AddAdditionalImage(id, paths[0]);
            },
            1);
    }

    private void AddAdditionalImage(Guid id, string sourcePath)
    {
        try
        {
            if (!plugin.Configuration.OutfitAdditionalImages.TryGetValue(id, out var list))
            {
                list = new List<string>();
                plugin.Configuration.OutfitAdditionalImages[id] = list;
            }

            if (list.Count >= MaxAdditionalImages)
                return;

            var imagesDir = EnsureAdditionalImagesDirectory();
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = ".png";

            var targetName = $"{id:N}_{Guid.NewGuid():N}{ext}";
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            list.Add(targetName);
            plugin.Configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Failed to add image: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to add additional image for {Id} from {Path}", id, sourcePath);
        }
    }

    private void RemoveAdditionalImage(Guid id, int index)
    {
        if (!plugin.Configuration.OutfitAdditionalImages.TryGetValue(id, out var list))
            return;
        if (index < 0 || index >= list.Count)
            return;

        var filename = list[index];
        list.RemoveAt(index);
        if (list.Count == 0)
            plugin.Configuration.OutfitAdditionalImages.Remove(id);

        try
        {
            var path = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images", AdditionalImagesSubdir, filename);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete additional image {File}", filename);
        }

        plugin.Configuration.Save();
    }

    private static CachedOutfit ParseOutfit(JObject j)
    {
        var name = ReadString(j["Name"]) ?? "(unnamed)";
        var description = ReadString(j["Description"]);
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var tags = j["Tags"] is JArray tagArray
            ? tagArray.Select(t => ReadString(t) ?? string.Empty)
                      .Where(t => !string.IsNullOrWhiteSpace(t))
                      .ToList()
            : new List<string>();

        return new CachedOutfit
        {
            Name = name,
            Description = description,
            Tags = tags,
            CreatedAt = ReadDateTimeOffset(j["CreationDate"]),
            LastEdit = ReadDateTimeOffset(j["LastEdit"]),
        };
    }

    // Avoid JToken.Value<string>(): it goes through Convert.ChangeType, which throws
    // "Object must implement IConvertible" on token values like DateTimeOffset.
    private static string? ReadString(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
            return null;
        if (token is JValue v)
            return v.Value?.ToString();
        return token.ToString();
    }

    private static DateTimeOffset? ReadDateTimeOffset(JToken? token)
    {
        if (token is not JValue v || v.Value is null)
            return null;
        return v.Value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
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
