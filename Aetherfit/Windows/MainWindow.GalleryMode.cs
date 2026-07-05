using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private int hiddenVersion;
    private int cachedHiddenVersion = -1;

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

        DrawFilterUi(defaultOpen: true, wide: true);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawGallerySortControls();
        ImGui.Spacing();

        using var gridChild = ImRaii.Child("CoverGridScroll", Vector2.Zero, false);
        if (gridChild.Success)
            DrawCoverGrid();
    }

    // onlyIds null = export everything; otherwise just those designs (the currently filtered list).
    private void OpenExportGalleryDialog(IReadOnlySet<Guid>? onlyIds = null)
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
                    plugin.GallerySharing.ExportToFile(label, path, onlyIds);
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
        cachedFavouriteVersion != favouriteVersion ||
        cachedHiddenVersion != hiddenVersion;

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
        cachedHiddenVersion = hiddenVersion;
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
        var shiftRightClicked = false;
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

        var mouse = ImGui.GetIO().MousePos;
        var arrows = GalleryDraw.DrawArrows(thumbStart, thumbWidth, thumbHeight,
            hasArrows, canPrev, canNext, mouse, imageHovered);
        var overLeft = arrows.OverLeft;
        var overRight = arrows.OverRight;

        var starSize = 24f * ImGuiHelpers.GlobalScale;
        var starMargin = 4f * ImGuiHelpers.GlobalScale;
        var starMin = new Vector2(thumbStart.X + thumbWidth - starSize - starMargin, thumbStart.Y + starMargin);
        var starMax = new Vector2(starMin.X + starSize, starMin.Y + starSize);
        var overStar = imageHovered
            && mouse.X >= starMin.X && mouse.X <= starMax.X
            && mouse.Y >= starMin.Y && mouse.Y <= starMax.Y;

        // Hidden-eye toggle mirrors the star, anchored to the top-left corner instead of the top-right.
        var eyeMin = new Vector2(thumbStart.X + starMargin, thumbStart.Y + starMargin);
        var eyeMax = new Vector2(eyeMin.X + starSize, eyeMin.Y + starSize);
        var overEye = imageHovered
            && mouse.X >= eyeMin.X && mouse.X <= eyeMax.X
            && mouse.Y >= eyeMin.Y && mouse.Y <= eyeMax.Y;

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
                else if (overEye)
                {
                    // Visible cells are never hidden, so a click here always hides the design (it then drops
                    // out of the gallery). Unhiding happens from the detail header.
                    plugin.Configuration.HiddenDesigns.Add(design.Id);
                    plugin.Configuration.Save();
                    hiddenVersion++;
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
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift && !overLeft && !overRight && !overStar && !overEye)
                shiftRightClicked = true;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !overLeft && !overRight && !overStar && !overEye)
                doubleClicked = true;
            if (overStar)
                ImGui.SetTooltip(isFavourite ? "Click to remove from favourites" : "Click to add to favourites");
            else if (overEye)
                ImGui.SetTooltip("Click to hide from the gallery and exports");
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
                ? ImGui.ColorConvertFloat4ToU32(UiTheme.FavouriteStar)
                : ImGui.ColorConvertFloat4ToU32(UiTheme.FavouriteStarOff);
            var starTextSize = ImGui.CalcTextSize(starChar);
            var starCenter = (starMin + starMax) * 0.5f;
            dl.AddText(new Vector2(starCenter.X - starTextSize.X * 0.5f, starCenter.Y - starTextSize.Y * 0.5f),
                starColor, starChar);
        }

        // The hide-eye only appears on hover: a visible cell is never hidden, so there is no persistent state to show.
        if (imageHovered)
        {
            var dl = ImGui.GetWindowDrawList();
            var bgAlpha = overEye ? 0.85f : 0.55f;
            dl.AddRectFilled(eyeMin, eyeMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, bgAlpha)), 3f);
            var eyeColor = ImGui.ColorConvertFloat4ToU32(UiTheme.HiddenEyeOff);
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                var eyeChar = FontAwesomeIcon.Eye.ToIconString();
                var eyeTextSize = ImGui.CalcTextSize(eyeChar);
                var eyeCenter = (eyeMin + eyeMax) * 0.5f;
                dl.AddText(new Vector2(eyeCenter.X - eyeTextSize.X * 0.5f, eyeCenter.Y - eyeTextSize.Y * 0.5f),
                    eyeColor, eyeChar);
            }
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
        if (shiftRightClicked)
        {
            selectedDesign = design.Id;
            plugin.Glamourer.OpenInGlamourer(design.Id, design.DisplayName);
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
        ImGui.TextDisabled("Shift+right-click to open in Glamourer");
        ImGui.EndTooltip();
    }
}
