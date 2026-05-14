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
    private readonly RevertState revertState;

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

    private bool coverMode;
    private const int CoverColumns = 4;
    private const float CoverMinThumbSize = 96f;
    private const float CoverAspectRatio = 4f / 3f; // height / width — portrait to suit character screenshots
    private readonly Dictionary<Guid, int> galleryImageIndex = new();

    private enum GallerySortField { Name, LastModified, Created }
    private GallerySortField gallerySortField = GallerySortField.Name;
    private bool gallerySortAscending = true;

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
        revertState = new RevertState(Plugin.PluginInterface);
    }

    public void Dispose() { }

    public override void OnOpen()
    {
        coverMode = plugin.Configuration.DefaultToCoverMode;
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

            var validIds = new HashSet<Guid>(data.Keys);
            CleanupRemovedDesigns(validIds);

            if (selectedDesign is { } sid && !validIds.Contains(sid))
                selectedDesign = null;

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

    private void DrawLeftPane()
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), "Glamourer Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("Gallery Mode >>", new Vector2(-1, 0)))
            coverMode = true;

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

    private void DrawCoverModePane()
    {
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.4f, 1.0f), "Glamourer Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("<< Edit Mode", new Vector2(-1, 0)))
            coverMode = false;

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

        DrawFilterUi(defaultOpen: true);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGallerySortControls();
        ImGui.Spacing();

        using var gridChild = ImRaii.Child("CoverGridScroll", Vector2.Zero, false);
        if (gridChild.Success)
            DrawCoverGrid();
    }

    private void DrawGallerySortControls()
    {
        ImGui.TextDisabled("Sort by:");
        ImGui.SameLine();

        var dirLabel = gallerySortAscending ? "Asc" : "Desc";
        var dirWidth = ImGui.CalcTextSize(dirLabel).X + ImGui.GetStyle().FramePadding.X * 2 + 8 * ImGuiHelpers.GlobalScale;
        var comboWidth = Math.Max(120f * ImGuiHelpers.GlobalScale,
            ImGui.GetContentRegionAvail().X - dirWidth - ImGui.GetStyle().ItemSpacing.X);

        ImGui.PushItemWidth(comboWidth);
        var fieldIdx = (int)gallerySortField;
        var fieldOptions = new[] { "Name (alphabetical)", "Last modified", "Created" };
        if (ImGui.Combo("##gallerySortField", ref fieldIdx, fieldOptions, fieldOptions.Length))
            gallerySortField = (GallerySortField)fieldIdx;
        ImGui.PopItemWidth();

        ImGui.SameLine();
        if (ImGui.Button($"{dirLabel}##gallerySortDir", new Vector2(dirWidth, 0)))
            gallerySortAscending = !gallerySortAscending;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(gallerySortAscending ? "Ascending — click to switch to descending" : "Descending — click to switch to ascending");
    }

    private void DrawCoverGrid()
    {
        var visible = new List<DesignLeaf>();
        CollectVisibleDesigns(root, visible);
        SortGalleryDesigns(visible);

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No designs match the current filters.");
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var thumbWidth = Math.Max(CoverMinThumbSize, (avail - (CoverColumns - 1) * spacing) / CoverColumns);
        var thumbHeight = thumbWidth * CoverAspectRatio;

        for (var i = 0; i < visible.Count; i++)
        {
            if (i % CoverColumns != 0)
                ImGui.SameLine();
            DrawCoverCell(visible[i], thumbWidth, thumbHeight);
        }
    }

    private void SortGalleryDesigns(List<DesignLeaf> designs)
    {
        var asc = gallerySortAscending;
        switch (gallerySortField)
        {
            case GallerySortField.Name:
                designs.Sort((a, b) =>
                {
                    var cmp = NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
                    return asc ? cmp : -cmp;
                });
                break;
            case GallerySortField.LastModified:
                designs.Sort((a, b) => CompareDates(GetLastEdit(a.Id), GetLastEdit(b.Id), asc));
                break;
            case GallerySortField.Created:
                designs.Sort((a, b) => CompareDates(GetCreatedAt(a.Id), GetCreatedAt(b.Id), asc));
                break;
        }
    }

    private DateTimeOffset? GetLastEdit(Guid id) =>
        plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.LastEdit : null;

    private DateTimeOffset? GetCreatedAt(Guid id) =>
        plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.CreatedAt : null;

    // Missing dates always sink to the bottom, regardless of direction.
    private static int CompareDates(DateTimeOffset? a, DateTimeOffset? b, bool ascending)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        var cmp = a.Value.CompareTo(b.Value);
        return ascending ? cmp : -cmp;
    }

    private void CollectVisibleDesigns(FolderNode node, List<DesignLeaf> result)
    {
        foreach (var design in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var cached);
            if (DesignMatchesFilters(design, cached))
                result.Add(design);
        }
        foreach (var folder in node.Folders.Values)
            CollectVisibleDesigns(folder, result);
    }

    private void DrawCoverCell(DesignLeaf design, float thumbWidth, float thumbHeight)
    {
        using var id = ImRaii.PushId(design.Id.ToString());
        using var group = ImRaii.Group();

        var thumbStart = ImGui.GetCursorScreenPos();
        var thumbVec = new Vector2(thumbWidth, thumbHeight);
        var containerAspect = thumbWidth / thumbHeight;

        var coverPath = GetOutfitImagePath(design.Id);
        var additionalPaths = GetAdditionalImagePaths(design.Id);
        var images = new List<string>();
        if (coverPath != null) images.Add(coverPath);
        images.AddRange(additionalPaths);

        if (!galleryImageIndex.TryGetValue(design.Id, out var imgIdx) || imgIdx < 0 || imgIdx >= images.Count)
        {
            imgIdx = 0;
            galleryImageIndex[design.Id] = 0;
        }
        var currentImage = images.Count > 0 ? images[imgIdx] : null;

        var clicked = false;
        var shiftClicked = false;
        var doubleClicked = false;

        if (currentImage != null)
        {
            var tex = Plugin.TextureProvider.GetFromFile(currentImage).GetWrapOrEmpty();
            if (tex.Width > 0 && tex.Height > 0)
            {
                float uMin = 0f, uMax = 1f, vMin = 0f, vMax = 1f;
                var texAspect = tex.Width / (float)tex.Height;
                if (texAspect > containerAspect)
                {
                    var keep = containerAspect / texAspect;
                    uMin = (1f - keep) * 0.5f;
                    uMax = 1f - uMin;
                }
                else if (texAspect < containerAspect)
                {
                    var keep = texAspect / containerAspect;
                    vMin = (1f - keep) * 0.5f;
                    vMax = 1f - vMin;
                }
                ImGui.Image(tex.Handle, thumbVec, new Vector2(uMin, vMin), new Vector2(uMax, vMax));
            }
            else
            {
                ImGui.Dummy(thumbVec);
            }
        }
        else
        {
            ImGui.InvisibleButton("##placeholder", thumbVec);
            var dl = ImGui.GetWindowDrawList();
            var fill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.22f, 0.22f, 0.25f, 1f));
            dl.AddRectFilled(thumbStart, thumbStart + thumbVec, fill, 4f);
            const string text = "No Image";
            var textSize = ImGui.CalcTextSize(text);
            var textPos = thumbStart + (thumbVec - textSize) * 0.5f;
            dl.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.65f, 0.65f, 0.68f, 1f)), text);
        }

        var imageHovered = ImGui.IsItemHovered();

        var hasArrows = additionalPaths.Count > 0;
        var canPrev = imgIdx > 0;
        var canNext = imgIdx < images.Count - 1;

        var arrowZone = Math.Min(28f * ImGuiHelpers.GlobalScale, Math.Min(thumbWidth, thumbHeight) * 0.32f);
        var arrowMargin = 4f * ImGuiHelpers.GlobalScale;
        var leftMin = new Vector2(thumbStart.X + arrowMargin, thumbStart.Y + (thumbHeight - arrowZone) * 0.5f);
        var leftMax = new Vector2(leftMin.X + arrowZone, leftMin.Y + arrowZone);
        var rightMax = new Vector2(thumbStart.X + thumbWidth - arrowMargin, thumbStart.Y + (thumbHeight + arrowZone) * 0.5f);
        var rightMin = new Vector2(rightMax.X - arrowZone, rightMax.Y - arrowZone);

        var mouse = ImGui.GetIO().MousePos;
        var overLeft = imageHovered && hasArrows && canPrev
                       && mouse.X >= leftMin.X && mouse.X <= leftMax.X
                       && mouse.Y >= leftMin.Y && mouse.Y <= leftMax.Y;
        var overRight = imageHovered && hasArrows && canNext
                        && mouse.X >= rightMin.X && mouse.X <= rightMax.X
                        && mouse.Y >= rightMin.Y && mouse.Y <= rightMax.Y;

        if (imageHovered && hasArrows)
        {
            var dl = ImGui.GetWindowDrawList();
            if (canPrev) DrawGalleryChevron(dl, leftMin, leftMax, isLeft: true, hovered: overLeft);
            if (canNext) DrawGalleryChevron(dl, rightMin, rightMax, isLeft: false, hovered: overRight);
        }

        if (imageHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (overLeft)
                    galleryImageIndex[design.Id] = imgIdx - 1;
                else if (overRight)
                    galleryImageIndex[design.Id] = imgIdx + 1;
                else if (ImGui.GetIO().KeyShift)
                    shiftClicked = true;
                else
                    clicked = true;
            }
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !overLeft && !overRight)
                doubleClicked = true;
            if (!overLeft && !overRight)
                DrawCoverCellTooltip(design);
        }

        if (selectedDesign == design.Id)
        {
            var dl = ImGui.GetWindowDrawList();
            var hl = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 0.85f, 0.4f, 1f));
            dl.AddRect(thumbStart, thumbStart + thumbVec, hl, 4f, ImDrawFlags.None, 2.5f);
        }

        var label = design.DisplayName;
        var labelWidth = ImGui.CalcTextSize(label).X;
        var indent = Math.Max(0f, (thumbWidth - labelWidth) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        var hasColor = design.Color != 0;
        if (hasColor)
            ImGui.PushStyleColor(ImGuiCol.Text, design.Color);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + thumbWidth);
        ImGui.TextUnformatted(label);
        ImGui.PopTextWrapPos();
        if (hasColor)
            ImGui.PopStyleColor();

        if (clicked)
            selectedDesign = design.Id;
        if (shiftClicked)
        {
            selectedDesign = design.Id;
            coverMode = false;
        }
        if (doubleClicked)
        {
            selectedDesign = design.Id;
            ApplyDesignById(design.Id);
        }
    }

    private static void DrawGalleryChevron(ImDrawListPtr dl, Vector2 min, Vector2 max, bool isLeft, bool hovered)
    {
        var bgAlpha = hovered ? 0.85f : 0.55f;
        var bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, bgAlpha));
        dl.AddRectFilled(min, max, bg, 4f);

        var center = (min + max) * 0.5f;
        var size = Math.Min(max.X - min.X, max.Y - min.Y);
        var halfH = size * 0.25f;
        var halfW = size * 0.18f;
        var color = ImGui.ColorConvertFloat4ToU32(Vector4.One);
        if (isLeft)
        {
            dl.AddTriangleFilled(
                new Vector2(center.X - halfW, center.Y),
                new Vector2(center.X + halfW, center.Y + halfH),
                new Vector2(center.X + halfW, center.Y - halfH),
                color);
        }
        else
        {
            dl.AddTriangleFilled(
                new Vector2(center.X + halfW, center.Y),
                new Vector2(center.X - halfW, center.Y - halfH),
                new Vector2(center.X - halfW, center.Y + halfH),
                color);
        }
    }

    private void DrawCoverCellTooltip(DesignLeaf design)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(design.DisplayName);
        if (!string.IsNullOrEmpty(design.FullPath))
            ImGui.TextDisabled(design.FullPath);
        ImGui.TextDisabled("Double-click to apply");
        ImGui.TextDisabled("Shift+click to open in edit view");
        ImGui.EndTooltip();
    }

    private void DrawFilterUi(bool defaultOpen = false)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader("Filters", flags))
            return;

        DrawFilterControls();
        DrawFilterTagsPopup();
    }

    private void DrawFilterControls()
    {
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

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            selectedDesign = design.Id;
            ApplyDesignById(design.Id);
        }

        if (hasColor)
            ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
            DrawDesignLeafTooltip(design);
    }

    private void DrawDesignLeafTooltip(DesignLeaf design)
    {
        var imagePath = plugin.Configuration.ShowThumbnailOnHover ? GetOutfitImagePath(design.Id) : null;
        var hasPath = !string.IsNullOrEmpty(design.FullPath);

        ImGui.BeginTooltip();
        if (hasPath)
            ImGui.TextUnformatted(design.FullPath);
        if (imagePath != null)
            DrawImageScaled(imagePath, TooltipImageMax * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled("Double-click to apply");
        ImGui.EndTooltip();
    }

    private void DrawWelcomePlaceholder()
    {
        const string message = "Select a design on the left to see its details.";
        var avail = ImGui.GetContentRegionAvail();

        var iconDir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        var iconPath = iconDir != null ? Path.Combine(iconDir, "icon.png") : null;
        var tex = iconPath != null && File.Exists(iconPath)
            ? Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrEmpty()
            : null;

        Vector2 imageSize = Vector2.Zero;
        if (tex is { Width: > 0, Height: > 0 })
        {
            var maxSide = Math.Min(256f * ImGuiHelpers.GlobalScale, Math.Min(avail.X, avail.Y * 0.6f));
            var scale = Math.Min(maxSide / tex.Width, maxSide / tex.Height);
            if (scale > 0f)
                imageSize = new Vector2(tex.Width * scale, tex.Height * scale);
        }

        var textSize = ImGui.CalcTextSize(message);
        var spacing = imageSize.Y > 0 ? ImGui.GetStyle().ItemSpacing.Y : 0f;
        var totalHeight = imageSize.Y + spacing + textSize.Y;
        var startY = ImGui.GetCursorPosY() + Math.Max(0f, (avail.Y - totalHeight) * 0.5f);

        if (imageSize.Y > 0 && tex != null)
        {
            ImGui.SetCursorPosY(startY);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail.X - imageSize.X) * 0.5f));
            ImGui.Image(tex.Handle, imageSize);
            startY = ImGui.GetCursorPosY() + spacing;
        }

        ImGui.SetCursorPosY(startY);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail.X - textSize.X) * 0.5f));
        ImGui.TextDisabled(message);
    }

    private void DrawSelectedOutfitDetails()
    {
        if (selectedDesign is not { } id)
        {
            DrawWelcomePlaceholder();
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

    public void RevertAppearance()
    {
        try
        {
            var result = revertState.Invoke(0);
            Plugin.ChatGui.Print($"[Aetherfit] Reverted appearance to game state: {result}");
            Plugin.Log.Info("Reverted appearance to game state: {Result}", result);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Revert failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to revert appearance");
        }
    }

    private void ApplyDesignById(Guid id)
    {
        try
        {
            var result = applyDesign.Invoke(id, 0, 0);
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

    private void CleanupRemovedDesigns(IReadOnlySet<Guid> validIds)
    {
        var coverDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images");
        var additionalDir = Path.Combine(coverDir, AdditionalImagesSubdir);

        var staleCoverIds = plugin.Configuration.OutfitImages.Keys
            .Where(id => !validIds.Contains(id))
            .ToList();
        foreach (var id in staleCoverIds)
        {
            DeleteImageFilesFor(id, coverDir);
            plugin.Configuration.OutfitImages.Remove(id);
        }

        var staleAdditionalIds = plugin.Configuration.OutfitAdditionalImages.Keys
            .Where(id => !validIds.Contains(id))
            .ToList();
        foreach (var id in staleAdditionalIds)
        {
            if (plugin.Configuration.OutfitAdditionalImages.TryGetValue(id, out var filenames))
            {
                foreach (var name in filenames)
                {
                    try
                    {
                        var path = Path.Combine(additionalDir, name);
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning(ex, "Failed to delete additional image {File}", name);
                    }
                }
            }
            plugin.Configuration.OutfitAdditionalImages.Remove(id);
        }

        SweepOrphanFiles(coverDir, validIds);
        SweepOrphanFiles(additionalDir, validIds);
    }

    // Filenames in our image dirs encode the design Guid as the prefix before any underscore
    // (cover: "{guid:N}.ext"; additional: "{guid:N}_{anotherGuid:N}.ext"). Anything whose prefix
    // parses as a Guid but isn't in validIds is a leftover and gets removed.
    private static void SweepOrphanFiles(string directory, IReadOnlySet<Guid> validIds)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var underscore = name.IndexOf('_');
            var prefix = underscore >= 0 ? name[..underscore] : name;
            if (!Guid.TryParseExact(prefix, "N", out var fileId))
                continue;
            if (validIds.Contains(fileId))
                continue;
            try { File.Delete(path); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to delete orphan file {File}", path); }
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
