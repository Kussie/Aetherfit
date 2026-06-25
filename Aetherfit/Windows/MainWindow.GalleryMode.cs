using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private enum GallerySortField { Name, LastModified, Created }

    private const int CoverColumns = 4;
    private const float CoverMinThumbSize = 96f;
    // Portrait aspect to suit character screenshots.
    private const float CoverAspectRatio = 3f / 2f;

    private bool coverMode;
    private readonly Dictionary<Guid, int> galleryImageIndex = new();
    private GallerySortField gallerySortField = GallerySortField.Name;
    private bool gallerySortAscending = true;

    // Cached filtered+sorted design list — rebuilt only when filter/sort state or designs change.
    private List<DesignLeaf> cachedVisible = [];
    private int designListGeneration;
    private int cachedGeneration = -1;
    private string cachedFilterName = string.Empty;
    private bool cachedSearchDesignName;
    private bool cachedSearchModName;
    private bool cachedSearchEquipmentName;
    private ImageFilterMode cachedFilterImage;
    private HashSet<string> cachedFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private GallerySortField cachedSortField;
    private bool cachedSortAscending = true;
    private bool cachedFilterFavourites;
    private bool cachedFilterVanillaOnly;
    private bool cachedFilterModdedOnly;
    private int favouriteVersion;
    private int cachedFavouriteVersion = -1;

    private void DrawCoverModePane()
    {
        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, "Glamourer Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("<< Edit Mode", new Vector2(-1, 0)))
            coverMode = false;
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

        DrawFilterUi(defaultOpen: true, inlineModFilters: true);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGallerySortControls();
        ImGui.Spacing();

        using var gridChild = ImRaii.Child("CoverGridScroll", Vector2.Zero, false);
        if (gridChild.Success)
            DrawCoverGrid();
    }

    private void OpenExportGalleryDialog()
    {
        var label = Plugin.PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(Plugin.PlayerState.CharacterName)
            ? Plugin.PlayerState.CharacterName
            : "Shared Gallery";
        var filters = $"Aetherfit Gallery{{{GallerySharingService.FileExtension}}}";
        var defaultName = SanitizeFileName(label) + GallerySharingService.FileExtension;
        fileDialog.SaveFileDialog(
            "Export Gallery",
            filters,
            defaultName,
            GallerySharingService.FileExtension,
            (success, path) =>
            {
                if (success && !string.IsNullOrEmpty(path))
                    plugin.GallerySharing.ExportToFile(label, path);
            });
    }

    private void OpenImportGalleryDialog()
    {
        var filters = $"Aetherfit Gallery{{{GallerySharingService.FileExtension}}}";
        fileDialog.OpenFileDialog(
            "Import Gallery",
            filters,
            (success, paths) =>
            {
                if (!success || paths.Count == 0)
                    return;
                var foreign = plugin.GallerySharing.ImportFromFile(paths[0]);
                if (foreign != null)
                    plugin.ForeignGallery.Show(foreign);
            },
            1);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "gallery" : name;
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

    private bool IsGalleryCacheStale() =>
        cachedGeneration != designListGeneration ||
        cachedSortField != gallerySortField ||
        cachedSortAscending != gallerySortAscending ||
        cachedFilterImage != filterImage ||
        cachedFilterName != filterName ||
        cachedSearchDesignName != searchDesignName ||
        cachedSearchModName != searchModName ||
        cachedSearchEquipmentName != searchEquipmentName ||
        !cachedFilterTags.SetEquals(filterTags) ||
        cachedFilterFavourites != filterFavourites ||
        cachedFilterVanillaOnly != filterVanillaOnly ||
        cachedFilterModdedOnly != filterModdedOnly ||
        cachedFavouriteVersion != favouriteVersion;

    private void RebuildGalleryCache()
    {
        cachedVisible.Clear();
        CollectVisibleDesigns(root, cachedVisible);
        SortGalleryDesigns(cachedVisible);

        cachedGeneration = designListGeneration;
        cachedSortField = gallerySortField;
        cachedSortAscending = gallerySortAscending;
        cachedFilterImage = filterImage;
        cachedFilterName = filterName;
        cachedSearchDesignName = searchDesignName;
        cachedSearchModName = searchModName;
        cachedSearchEquipmentName = searchEquipmentName;
        cachedFilterTags = new HashSet<string>(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedFilterFavourites = filterFavourites;
        cachedFilterVanillaOnly = filterVanillaOnly;
        cachedFilterModdedOnly = filterModdedOnly;
        cachedFavouriteVersion = favouriteVersion;
    }

    private void DrawCoverGrid()
    {
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        var visible = cachedVisible;

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
        var favourites = plugin.Configuration.FavouriteDesigns;
        var asc = gallerySortAscending;
        switch (gallerySortField)
        {
            case GallerySortField.Name:
                designs.Sort((a, b) =>
                {
                    var fa = favourites.Contains(a.Id);
                    var fb = favourites.Contains(b.Id);
                    if (fa != fb) return fa ? -1 : 1;
                    var cmp = NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
                    return asc ? cmp : -cmp;
                });
                break;
            case GallerySortField.LastModified:
                designs.Sort((a, b) =>
                {
                    var fa = favourites.Contains(a.Id);
                    var fb = favourites.Contains(b.Id);
                    if (fa != fb) return fa ? -1 : 1;
                    return CompareDates(GetLastEdit(a.Id), GetLastEdit(b.Id), asc);
                });
                break;
            case GallerySortField.Created:
                designs.Sort((a, b) =>
                {
                    var fa = favourites.Contains(a.Id);
                    var fb = favourites.Contains(b.Id);
                    if (fa != fb) return fa ? -1 : 1;
                    return CompareDates(GetCreatedAt(a.Id), GetCreatedAt(b.Id), asc);
                });
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

    private void DrawCoverCell(DesignLeaf design, float thumbWidth, float thumbHeight)
    {
        using var id = ImRaii.PushId(design.Id.ToString());
        using var group = ImRaii.Group();

        var thumbStart = ImGui.GetCursorScreenPos();
        var thumbVec = new Vector2(thumbWidth, thumbHeight);
        var containerAspect = thumbWidth / thumbHeight;

        var coverPath = plugin.ImageStorage.GetCoverPath(design.Id);
        var additionalPaths = plugin.ImageStorage.GetAdditionalPaths(design.Id);
        var images = GalleryDraw.BuildImageList(coverPath, additionalPaths);

        var imgIdx = GalleryDraw.ResolveImageIndex(galleryImageIndex, design.Id, images.Count);
        var currentImage = images.Count > 0 ? images[imgIdx] : null;

        var clicked = false;
        var shiftClicked = false;
        var doubleClicked = false;

        if (currentImage != null)
        {
            var tex = Plugin.TextureProvider.GetFromFile(currentImage).GetWrapOrEmpty();
            if (tex.Width > 0 && tex.Height > 0)
                GalleryDraw.DrawFittedImage(tex, thumbStart, thumbVec, thumbWidth, thumbHeight, containerAspect,
                    plugin.Configuration.GalleryFitMode);
            else
                ImGui.Dummy(thumbVec);
        }
        else
        {
            GalleryDraw.DrawNoImagePlaceholder(thumbStart, thumbVec);
        }

        var imageHovered = ImGui.IsItemHovered();
        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);

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

        var starSize = 24f * ImGuiHelpers.GlobalScale;
        var starMargin = 4f * ImGuiHelpers.GlobalScale;
        var starMin = new Vector2(thumbStart.X + thumbWidth - starSize - starMargin, thumbStart.Y + starMargin);
        var starMax = new Vector2(starMin.X + starSize, starMin.Y + starSize);
        var overStar = imageHovered
            && mouse.X >= starMin.X && mouse.X <= starMax.X
            && mouse.Y >= starMin.Y && mouse.Y <= starMax.Y;

        if (imageHovered && hasArrows)
        {
            var dl = ImGui.GetWindowDrawList();
            if (canPrev) GalleryDraw.DrawChevron(dl, leftMin, leftMax, isLeft: true, hovered: overLeft);
            if (canNext) GalleryDraw.DrawChevron(dl, rightMin, rightMax, isLeft: false, hovered: overRight);
        }

        if (imageHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (overStar)
                {
                    if (isFavourite)
                        plugin.Configuration.FavouriteDesigns.Remove(design.Id);
                    else
                        plugin.Configuration.FavouriteDesigns.Add(design.Id);
                    plugin.Configuration.Save();
                    favouriteVersion++;
                }
                else if (overLeft)
                    galleryImageIndex[design.Id] = imgIdx - 1;
                else if (overRight)
                    galleryImageIndex[design.Id] = imgIdx + 1;
                else if (ImGui.GetIO().KeyShift)
                    shiftClicked = true;
                else
                    clicked = true;
            }
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !overLeft && !overRight && !overStar)
                doubleClicked = true;
            if (overStar)
                ImGui.SetTooltip(isFavourite ? "Click to remove from favourites" : "Click to add to favourites");
            else if (!overLeft && !overRight)
                DrawCoverCellTooltip(design);
        }

        if (selectedDesign == design.Id)
        {
            var dl = ImGui.GetWindowDrawList();
            var hl = ImGui.ColorConvertFloat4ToU32(UiTheme.GoldAccent);
            dl.AddRect(thumbStart, thumbStart + thumbVec, hl, 4f, ImDrawFlags.None, 2.5f);
        }

        if (isFavourite || imageHovered)
        {
            var dl = ImGui.GetWindowDrawList();
            var bgAlpha = overStar ? 0.85f : 0.55f;
            dl.AddRectFilled(starMin, starMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, bgAlpha)), 3f);
            var starChar = isFavourite ? "★" : "☆";
            var starColor = isFavourite
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.1f, 1f))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(0.7f, 0.7f, 0.72f, 0.9f));
            var starTextSize = ImGui.CalcTextSize(starChar);
            var starCenter = (starMin + starMax) * 0.5f;
            dl.AddText(new Vector2(starCenter.X - starTextSize.X * 0.5f, starCenter.Y - starTextSize.Y * 0.5f),
                starColor, starChar);
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
}
