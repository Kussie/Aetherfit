using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private enum GallerySortField { Name, LastModified, Created }

    private const float CoverMinThumbSize = 96f;
    // Portrait aspect to suit character screenshots.
    private const float CoverAspectRatio = 3f / 2f;
    // Thumbnail-size slider bounds, unscaled.
    private const float CoverThumbSliderMin = 140f;
    private const float CoverThumbSliderMax = 360f;

    private bool coverMode;
    private readonly Dictionary<Guid, int> galleryImageIndex = new();
    // Ellipsized cell labels, cached because truncation re-measures per character.
    private readonly Dictionary<Guid, (string Source, float Width, string Fitted)> cellLabelCache = new();
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
    private Dictionary<string, bool> cachedFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, bool> cachedFilterJobs = new();
    private Dictionary<string, bool> cachedFilterMods = new(StringComparer.OrdinalIgnoreCase);
    private GallerySortField cachedSortField;
    private bool cachedSortAscending = true;
    private bool cachedFilterFavourites;
    private bool cachedFilterVanillaOnly;
    private bool cachedFilterModdedOnly;
    private bool cachedPinFavourites = true;
    private int favouriteVersion;
    private int cachedFavouriteVersion = -1;
    private int hiddenVersion;
    private int cachedHiddenVersion = -1;
    private int jobAssociationVersion;

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

        // Rebuild now so the sort row's result count reflects this frame's filters.
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

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
                    plugin.GallerySharing.ExportToFileAsync(label, path, onlyIds);
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
                plugin.GallerySharing.ImportFromFileAsync(paths[0], plugin.ForeignGallery.Show);
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
        var style = ImGui.GetStyle();
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Sort by:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(190f * scale);
        var fieldIdx = (int)gallerySortField;
        var fieldOptions = new[] { "Name (alphabetical)", "Last modified", "Created" };
        if (ImGui.Combo("##gallerySortField", ref fieldIdx, fieldOptions, fieldOptions.Length))
            gallerySortField = (GallerySortField)fieldIdx;

        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        if (ImGuiComponents.IconButton(gallerySortAscending ? FontAwesomeIcon.SortAmountUp : FontAwesomeIcon.SortAmountDown))
            gallerySortAscending = !gallerySortAscending;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(gallerySortAscending ? "Ascending — click to switch to descending" : "Descending — click to switch to ascending");

        ImGui.SameLine();
        var pinFavs = plugin.Configuration.GalleryPinFavouritesFirst;
        if (Pills.DrawToggle("★ First", "pinFavs", pinFavs))
        {
            plugin.Configuration.GalleryPinFavouritesFirst = !pinFavs;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pin favourites to the top regardless of sort order");

        var countText = HasAnyFilter
            ? $"{cachedVisible.Count} of {designsCount} designs"
            : cachedVisible.Count == 1 ? "1 design" : $"{cachedVisible.Count} designs";
        const string sizeLabel = "Size:";
        var sliderW = 120f * scale;
        var rightW = ImGui.CalcTextSize(sizeLabel).X + style.ItemInnerSpacing.X + sliderW
                   + style.ItemSpacing.X + ImGui.CalcTextSize(countText).X;

        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + style.ItemSpacing.X,
            ImGui.GetContentRegionMax().X - rightW));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(sizeLabel);
        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        ImGui.SetNextItemWidth(sliderW);
        var thumbSize = plugin.Configuration.GalleryThumbTargetWidth;
        if (ImGui.SliderFloat("##galleryThumbSize", ref thumbSize, CoverThumbSliderMin, CoverThumbSliderMax, ""))
            plugin.Configuration.GalleryThumbTargetWidth = thumbSize;
        if (ImGui.IsItemDeactivatedAfterEdit())
            plugin.Configuration.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Thumbnail size");

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(countText);
    }

    private static bool FiltersEqual<TKey>(Dictionary<TKey, bool> a, Dictionary<TKey, bool> b) where TKey : notnull
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, include) in a)
            if (!b.TryGetValue(key, out var otherInclude) || include != otherInclude)
                return false;
        return true;
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
        !FiltersEqual(cachedFilterTags, filterTags) ||
        !FiltersEqual(cachedFilterJobs, filterJobs) ||
        !FiltersEqual(cachedFilterMods, filterMods) ||
        cachedFilterFavourites != filterFavourites ||
        cachedFilterVanillaOnly != filterVanillaOnly ||
        cachedFilterModdedOnly != filterModdedOnly ||
        cachedPinFavourites != plugin.Configuration.GalleryPinFavouritesFirst ||
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
        cachedFilterTags = new Dictionary<string, bool>(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedFilterJobs = new Dictionary<uint, bool>(filterJobs);
        cachedFilterMods = new Dictionary<string, bool>(filterMods, StringComparer.OrdinalIgnoreCase);
        cachedFilterFavourites = filterFavourites;
        cachedFilterVanillaOnly = filterVanillaOnly;
        cachedFilterModdedOnly = filterModdedOnly;
        cachedPinFavourites = plugin.Configuration.GalleryPinFavouritesFirst;
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
        // As many columns as the target thumbnail width allows.
        var target = Math.Max(CoverMinThumbSize, plugin.Configuration.GalleryThumbTargetWidth) * ImGuiHelpers.GlobalScale;
        var columns = Math.Max(1, (int)((avail + spacing) / (target + spacing)));
        var thumbWidth = Math.Max(CoverMinThumbSize, (avail - (columns - 1) * spacing) / columns);
        var thumbHeight = thumbWidth * CoverAspectRatio;

        for (var i = 0; i < visible.Count; i++)
        {
            if (i % columns != 0)
                ImGui.SameLine();
            DrawCoverCell(visible[i], thumbWidth, thumbHeight);
        }
    }

    private void SortGalleryDesigns(List<DesignLeaf> designs)
    {
        var favourites = plugin.Configuration.FavouriteDesigns;
        var pinFavs = plugin.Configuration.GalleryPinFavouritesFirst;
        var asc = gallerySortAscending;
        designs.Sort((a, b) =>
        {
            if (pinFavs)
            {
                var fa = favourites.Contains(a.Id);
                var fb = favourites.Contains(b.Id);
                if (fa != fb) return fa ? -1 : 1;
            }

            switch (gallerySortField)
            {
                case GallerySortField.LastModified:
                    return CompareDates(GetLastEdit(a.Id), GetLastEdit(b.Id), asc);
                case GallerySortField.Created:
                    return CompareDates(GetCreatedAt(a.Id), GetCreatedAt(b.Id), asc);
                default:
                    var cmp = NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
                    return asc ? cmp : -cmp;
            }
        });
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

        // Faint plate behind the whole card, thumb plus the one-line label strip.
        var cellMax = thumbStart + new Vector2(thumbWidth,
            thumbHeight + ImGui.GetStyle().ItemSpacing.Y + ImGui.GetTextLineHeight());
        ImGui.GetWindowDrawList().AddRectFilled(thumbStart, cellMax,
            ImGui.ColorConvertFloat4ToU32(UiTheme.CardBg), 4f);

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

        // Page badge so multi-image cells are recognisable without hovering.
        if (images.Count > 1)
        {
            var dl = ImGui.GetWindowDrawList();
            var badge = $"{imgIdx + 1}/{images.Count}";
            var pad = 3f * ImGuiHelpers.GlobalScale;
            var margin = 4f * ImGuiHelpers.GlobalScale;
            var badgeTextSize = ImGui.CalcTextSize(badge);
            var badgeMax = new Vector2(thumbStart.X + thumbWidth - margin, thumbStart.Y + thumbHeight - margin);
            var badgeMin = badgeMax - badgeTextSize - new Vector2(pad * 2f, pad * 2f);
            dl.AddRectFilled(badgeMin, badgeMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f)), 3f);
            dl.AddText(badgeMin + new Vector2(pad, pad), ImGui.ColorConvertFloat4ToU32(Vector4.One), badge);
        }

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
            dl.AddRect(thumbStart, cellMax, hl, 4f, ImDrawFlags.None, 2.5f);
        }
        else if (imageHovered)
        {
            ImGui.GetWindowDrawList().AddRect(thumbStart, cellMax,
                ImGui.ColorConvertFloat4ToU32(UiTheme.CardHoverBorder), 4f, ImDrawFlags.None, 1.5f);
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

        // One label line per cell so rows stay even; long names get an ellipsis and a tooltip.
        var fullName = design.DisplayName;
        var label = FitCellLabel(design.Id, fullName, thumbWidth);
        var labelWidth = ImGui.CalcTextSize(label).X;
        var indent = Math.Max(0f, (thumbWidth - labelWidth) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        var hasColor = design.Color != 0;
        if (hasColor)
            ImGui.PushStyleColor(ImGuiCol.Text, design.Color);
        ImGui.TextUnformatted(label);
        if (hasColor)
            ImGui.PopStyleColor();
        if (label != fullName && ImGui.IsItemHovered())
            ImGui.SetTooltip(fullName);

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

    private string FitCellLabel(Guid id, string label, float width)
    {
        if (cellLabelCache.TryGetValue(id, out var cached)
            && cached.Source == label && Math.Abs(cached.Width - width) < 0.5f)
            return cached.Fitted;

        var fitted = TextFit.Ellipsize(label, width);
        cellLabelCache[id] = (label, width, fitted);
        return fitted;
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
