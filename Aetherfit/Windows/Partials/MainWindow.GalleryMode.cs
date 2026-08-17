using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Export;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;
using Aetherfit.Services.Sharing;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private enum GallerySortField { Name, LastModified, Created, LastWorn }

    private const string BulkAddTagPopupId = "BulkAddDesignTagPopup";

    private bool coverMode;
    private bool bulkSelectMode;
    private readonly HashSet<Guid> bulkSelectedIds = new();
    private string bulkAddTagSearchText = string.Empty;
    private bool bulkAddTagReclaimFocus;
    private readonly Dictionary<Guid, int> galleryImageIndex = new();
    // Which variant is currently fronted in a parent's card stack - 0 = the parent itself, 1..n =
    // GetVisibleVariantsFor(parent)[n-1]. Same resolve-and-clamp shape as galleryImageIndex.
    private readonly Dictionary<Guid, int> galleryVariantIndex = new();
    // Ellipsized cell labels, cached because truncation re-measures per character.
    private readonly Dictionary<Guid, (string Source, float Width, string Fitted)> cellLabelCache = new();
    private GallerySortField gallerySortField = GallerySortField.Name;
    private bool gallerySortAscending = true;

    // Cached filtered+sorted design list — rebuilt only when filter/sort state or designs change.
    private List<DesignLeaf> cachedVisible = [];
    private int designListGeneration;
    private int cachedGeneration = -1;
    private FilterSnapshot cachedFilterSnapshot;
    private Dictionary<string, bool> cachedFilterTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, bool> cachedFilterJobs = new();
    private Dictionary<string, bool> cachedFilterMods = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<EquipmentSlot> cachedFilterEquipmentSlots = new();
    private GallerySortField cachedSortField;
    private bool cachedSortAscending = true;
    private int cachedLastAppliedVersion;
    private bool cachedPinFavourites = true;
    private bool cachedGlamaholicEnabled = true;
    private bool cachedGlamourPlateEnabled = true;
    private bool cachedSimpleGlamourSwitcherEnabled = true;
    private int favouriteVersion;
    private int cachedFavouriteVersion = -1;
    private int variantVersion;
    private int cachedVariantVersion = -1;
    private bool cachedUnstackVariants;
    private int hiddenVersion;
    private int cachedHiddenVersion = -1;
    private int jobAssociationVersion;
    private bool favouritesSectionOpen = true;
    private bool otherDesignsSectionOpen = true;

    private void DrawCoverModePane()
    {
        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, "Your Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("<< Edit Mode", new Vector2(-1, 0)))
            coverMode = false;
        ImGui.Separator();

        if (DrawDesignsUnavailableBanner())
            return;

        DrawFilterUi(defaultOpen: true, wide: true, extraControls: DrawCoverGroupByControls);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Rebuild now so the sort row's result count reflects this frame's filters.
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        DrawBulkSelectControls();
        DrawGallerySortControls();
        ImGui.Spacing();

        using var gridChild = ImRaii.Child("CoverGridScroll", Vector2.Zero, false);
        if (gridChild.Success)
            DrawCoverGrid();
    }

    private void DrawBulkSelectControls()
    {
        var label = bulkSelectMode ? $"Done Selecting ({bulkSelectedIds.Count})" : "Select Designs";
        if (IconTextButton(FontAwesomeIcon.Tags, label))
        {
            bulkSelectMode = !bulkSelectMode;
            if (!bulkSelectMode)
                bulkSelectedIds.Clear();
        }
        if (bulkSelectMode)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(bulkSelectedIds.Count == 0))
            {
                if (ImGui.Button("Add Tag to Selected..."))
                    ImGui.OpenPopup(BulkAddTagPopupId);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(bulkSelectedIds.Count == 0))
            {
                if (ImGui.Button("Clear Selection"))
                    bulkSelectedIds.Clear();
            }
        }
        DrawBulkAddTagPopup();
        ImGui.Spacing();
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

    // onlyIds null = export everything; otherwise just those designs (the currently filtered list).
    private void OpenExportLookBookDialog(IReadOnlySet<Guid>? onlyIds = null)
    {
        var label = Plugin.PlayerState.IsLoaded && !string.IsNullOrWhiteSpace(Plugin.PlayerState.CharacterName)
            ? Plugin.PlayerState.CharacterName
            : "Look Book";
        var filters = $"PDF Document{{{LookBookExportService.FileExtension}}}";
        var defaultName = SanitizeFileName(label) + " Look Book" + LookBookExportService.FileExtension;
        fileDialog.SaveFileDialog(
            "Export Look Book",
            filters,
            defaultName,
            LookBookExportService.FileExtension,
            (success, path) =>
            {
                if (success && !string.IsNullOrEmpty(path))
                    plugin.LookBookExport.ExportToFileAsync(label, path, onlyIds);
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
        var fieldOptions = new[] { "Name (alphabetical)", "Last modified", "Created", "Last worn" };
        if (ImGui.Combo("##gallerySortField", ref fieldIdx, fieldOptions, fieldOptions.Length))
            gallerySortField = (GallerySortField)fieldIdx;

        ImGui.SameLine(0, style.ItemInnerSpacing.X);
        GalleryDraw.DrawSortDirectionToggle(ref gallerySortAscending);

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
        var thumbSize = plugin.Configuration.GalleryThumbTargetWidth;
        GalleryDraw.DrawThumbSizeAndCount("##galleryThumbSize", ref thumbSize, countText, () => plugin.Configuration.Save());
        plugin.Configuration.GalleryThumbTargetWidth = thumbSize;
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
        cachedFilterSnapshot != CaptureFilterSnapshot() ||
        !FiltersEqual(cachedFilterTags, filterTags) ||
        !FiltersEqual(cachedFilterJobs, filterJobs) ||
        !FiltersEqual(cachedFilterMods, filterMods) ||
        !cachedFilterEquipmentSlots.SetEquals(filterEquipmentSlots) ||
        cachedLastAppliedVersion != plugin.Configuration.LastAppliedVersion ||
        cachedPinFavourites != plugin.Configuration.GalleryPinFavouritesFirst ||
        cachedGlamaholicEnabled != plugin.Configuration.GlamaholicEnabled ||
        cachedGlamourPlateEnabled != plugin.Configuration.GlamourPlateEnabled ||
        cachedSimpleGlamourSwitcherEnabled != plugin.Configuration.SimpleGlamourSwitcherEnabled ||
        cachedFavouriteVersion != favouriteVersion ||
        cachedHiddenVersion != hiddenVersion ||
        cachedVariantVersion != variantVersion ||
        cachedUnstackVariants != coverUnstackVariants;

    // Bumped every time RebuildGalleryCache actually runs, regardless of why (filter/sort/generation/...
    // all funnel through here) - lets other caches derived from cachedVisible (e.g. the tag-grouped
    // Cover Mode view) detect staleness without duplicating this method's own dirty-check fields.
    private int galleryCacheVersion;

    private void RebuildGalleryCache()
    {
        galleryCacheVersion++;
        cachedVisible.Clear();
        CollectVisibleDesigns(root, cachedVisible, excludeVariants: !coverUnstackVariants);
        SortGalleryDesigns(cachedVisible);

        cachedGeneration = designListGeneration;
        cachedSortField = gallerySortField;
        cachedSortAscending = gallerySortAscending;
        cachedFilterSnapshot = CaptureFilterSnapshot();
        cachedFilterTags = new Dictionary<string, bool>(filterTags, StringComparer.OrdinalIgnoreCase);
        cachedFilterJobs = new Dictionary<uint, bool>(filterJobs);
        cachedFilterMods = new Dictionary<string, bool>(filterMods, StringComparer.OrdinalIgnoreCase);
        cachedFilterEquipmentSlots = new HashSet<EquipmentSlot>(filterEquipmentSlots);
        cachedLastAppliedVersion = plugin.Configuration.LastAppliedVersion;
        cachedPinFavourites = plugin.Configuration.GalleryPinFavouritesFirst;
        cachedGlamaholicEnabled = plugin.Configuration.GlamaholicEnabled;
        cachedGlamourPlateEnabled = plugin.Configuration.GlamourPlateEnabled;
        cachedSimpleGlamourSwitcherEnabled = plugin.Configuration.SimpleGlamourSwitcherEnabled;
        cachedFavouriteVersion = favouriteVersion;
        cachedHiddenVersion = hiddenVersion;
        cachedVariantVersion = variantVersion;
        cachedUnstackVariants = coverUnstackVariants;
    }

    // The groupings are mutually exclusive - ticking one unticks the others (all three can be off).
    // Independent state from Edit Mode's own DrawEditModeGroupByControls.
    private void DrawCoverGroupByControls()
    {
        if (ImGui.Checkbox("Group by job association", ref coverGroupByJob) && coverGroupByJob)
        {
            coverGroupByTags = false;
            coverGroupBySource = false;
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Group by tags", ref coverGroupByTags) && coverGroupByTags)
        {
            coverGroupByJob = false;
            coverGroupBySource = false;
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Group by source", ref coverGroupBySource) && coverGroupBySource)
        {
            coverGroupByJob = false;
            coverGroupByTags = false;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Show variants separately", ref coverUnstackVariants);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: variants are stacked behind their parent's cell.\nOn: every variant gets its own cell, like a normal design.");
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

        if (coverGroupByJob)
        {
            DrawCoverGroupedByJob();
            return;
        }
        if (coverGroupByTags)
        {
            DrawCoverGroupedByTags();
            return;
        }
        if (coverGroupBySource)
        {
            DrawCoverGroupedBySource();
            return;
        }

        var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();

        // Favourites are only contiguous at the front of `visible` when pinning is on (see SortGalleryDesigns).
        if (cachedPinFavourites)
        {
            var favourites = plugin.Configuration.FavouriteDesigns;
            var splitIdx = visible.FindIndex(d => !favourites.Contains(d.Id));
            if (splitIdx == -1)
                splitIdx = visible.Count;

            if (splitIdx > 0)
            {
                ImGui.Separator();
                if (Pills.DrawCollapsibleSubheader($"Favourites ({splitIdx})", ref favouritesSectionOpen))
                {
                    ImGui.Spacing();
                    DrawCoverGridRange(visible, 0, splitIdx, columns, thumbWidth, thumbHeight);
                    ImGui.Spacing();
                }

                if (splitIdx < visible.Count)
                {
                    ImGui.Separator();
                    if (Pills.DrawCollapsibleSubheader($"All Designs ({visible.Count - splitIdx})", ref otherDesignsSectionOpen))
                    {
                        ImGui.Spacing();
                        DrawCoverGridRange(visible, splitIdx, visible.Count, columns, thumbWidth, thumbHeight);
                    }
                }
                return;
            }
        }

        DrawCoverGridRange(visible, 0, visible.Count, columns, thumbWidth, thumbHeight);
    }

    // Recomputed fresh wherever it's needed (rather than once per grid) - grouped/nested sections
    // shrink the available width via ImGui.Indent(), so column count has to be reevaluated per section.
    private (int Columns, float ThumbWidth, float ThumbHeight) ComputeGridLayout()
        => GalleryDraw.ComputeGridLayout(plugin.Configuration.GalleryThumbTargetWidth);

    // Row-clipped so a large gallery doesn't request a texture for every design at once - only rows
    // actually scrolled into view get drawn. Shared by the flat grid, the favourites split, and every
    // grouped view's per-section draw, so clipping here benefits all of them for free.
    private void DrawCoverGridRange(List<DesignLeaf> visible, int start, int end, int columns, float thumbWidth, float thumbHeight)
    {
        var count = end - start;
        if (count <= 0)
            return;

        var rowHeight = thumbHeight + ImGui.GetStyle().ItemSpacing.Y * 2 + ImGui.GetTextLineHeight();
        var rowCount = (count + columns - 1) / columns;

        var clipper = new ImGuiListClipper();
        clipper.Begin(rowCount, rowHeight);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var i = start + row * columns + col;
                    if (i >= end) break;
                    if (col != 0) ImGui.SameLine();
                    DrawCoverCell(visible[i], thumbWidth, thumbHeight);
                }
            }
        }
        clipper.End();
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
                case GallerySortField.LastWorn:
                    return CompareDates(GetLastAppliedAt(a.Id), GetLastAppliedAt(b.Id), asc);
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

    private DateTimeOffset? GetLastAppliedAt(Guid id) =>
        plugin.Configuration.CachedOutfits.TryGetValue(id, out var c) ? c.LastAppliedAt : null;

    // Missing dates always sink to the bottom, regardless of direction.
    private static int CompareDates(DateTimeOffset? a, DateTimeOffset? b, bool ascending)
    {
        if (a is null && b is null) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        var cmp = a.Value.CompareTo(b.Value);
        return ascending ? cmp : -cmp;
    }

    // A parent's variants that currently pass the same visibility/filter rules the flat gallery list
    // uses - only these are eligible to appear cycled into the stack.
    private List<DesignLeaf> GetVisibleVariantsFor(Guid parentId)
    {
        var result = new List<DesignLeaf>();
        foreach (var (variantId, _) in plugin.Configuration.GetVariantsOf(parentId))
        {
            if (plugin.Configuration.HiddenDesigns.Contains(variantId))
                continue;
            if (!designLeafById.TryGetValue(variantId, out var leaf))
                continue;
            plugin.Configuration.CachedOutfits.TryGetValue(variantId, out var cached);
            if (DesignMatchesFilters(leaf, cached))
                result.Add(leaf);
        }
        return result;
    }

    private void DrawCoverCell(DesignLeaf parentDesign, float thumbWidth, float thumbHeight)
    {
        using var id = ImRaii.PushId(parentDesign.Id.ToString());

        // Widget IDs stay anchored to the parent so cycling doesn't remount the cell - everything
        // below operates on whichever design (parent or a variant) is currently fronted. When variants
        // are shown separately, each already gets its own flat cell, so the parent's cell shouldn't
        // also try to stack/cycle them - that would show every variant twice.
        var variants = coverUnstackVariants ? [] : GetVisibleVariantsFor(parentDesign.Id);
        var stackIdx = variants.Count > 0
            ? GalleryDraw.ResolveImageIndex(galleryVariantIndex, parentDesign.Id, variants.Count + 1)
            : 0;
        var design = stackIdx > 0 ? variants[stackIdx - 1] : parentDesign;

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
        var rightClicked = false;
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
        var hasMissingMod = plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var outfitForModBadge)
            && plugin.Attribution.HasMissingModAssociation(outfitForModBadge);

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
                ImGui.ColorConvertFloat4ToU32(UiTheme.IconOverlayBg), 3f);
            dl.AddText(badgeMin + new Vector2(pad, pad), ImGui.ColorConvertFloat4ToU32(Vector4.One), badge);
        }

        // Variant browse badge, top-center - every corner is already spoken for (star/eye/warning/image
        // count). Click its left/right half to step back/forward through the stack, same idea as the
        // image-paging arrows but positioned clear of them (those sit on the thumbnail's vertical edges).
        if (variants.Count > 0)
        {
            var dl = ImGui.GetWindowDrawList();
            var total = variants.Count + 1;
            var badgeText = $"< {stackIdx + 1}/{total} >";
            var pad = 3f * ImGuiHelpers.GlobalScale;
            var margin = 4f * ImGuiHelpers.GlobalScale;
            var badgeTextSize = ImGui.CalcTextSize(badgeText);
            var badgeMin = new Vector2(thumbStart.X + (thumbWidth - badgeTextSize.X) * 0.5f - pad, thumbStart.Y + margin);
            var badgeMax = badgeMin + badgeTextSize + new Vector2(pad * 2f, pad * 2f);

            var overVariantBadge = mouse.X >= badgeMin.X && mouse.X <= badgeMax.X
                && mouse.Y >= badgeMin.Y && mouse.Y <= badgeMax.Y;

            dl.AddRectFilled(badgeMin, badgeMax,
                ImGui.ColorConvertFloat4ToU32(overVariantBadge ? UiTheme.IconOverlayBgHovered : UiTheme.IconOverlayBg), 3f);
            dl.AddText(badgeMin + new Vector2(pad, pad), ImGui.ColorConvertFloat4ToU32(Vector4.One), badgeText);

            if (overVariantBadge)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"Variant {stackIdx + 1}/{total}: {design.DisplayName}\nClick left/right half to browse");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    var badgeCenterX = (badgeMin.X + badgeMax.X) * 0.5f;
                    var step = mouse.X < badgeCenterX ? total - 1 : 1;
                    galleryVariantIndex[parentDesign.Id] = (stackIdx + step) % total;
                }
            }
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

        // Missing-mod-association warning, anchored to the bottom-left corner (the only one of the
        // four not already used by the star/eye/page-count badges).
        var warnMin = new Vector2(thumbStart.X + starMargin, thumbStart.Y + thumbHeight - starSize - starMargin);
        var warnMax = new Vector2(warnMin.X + starSize, warnMin.Y + starSize);
        var overEye = imageHovered
            && mouse.X >= eyeMin.X && mouse.X <= eyeMax.X
            && mouse.Y >= eyeMin.Y && mouse.Y <= eyeMax.Y;

        if (imageHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            if (bulkSelectMode)
            {
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !overLeft && !overRight)
                {
                    // A stacked card represents the parent + all its variants - select/deselect the whole
                    // family together, not just whichever variant happens to be fronted right now.
                    var stackIds = new List<Guid> { parentDesign.Id };
                    stackIds.AddRange(variants.Select(v => v.Id));
                    var anySelected = stackIds.Any(bulkSelectedIds.Contains);
                    foreach (var stackId in stackIds)
                    {
                        if (anySelected)
                            bulkSelectedIds.Remove(stackId);
                        else
                            bulkSelectedIds.Add(stackId);
                    }
                }
                if (!overLeft && !overRight)
                {
                    ImGui.SetTooltip(bulkSelectedIds.Contains(design.Id)
                        ? (variants.Count > 0 ? "Click to deselect (whole stack)" : "Click to deselect")
                        : (variants.Count > 0 ? "Click to select (whole stack)" : "Click to select"));
                }
            }
            else
            {
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
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !overLeft && !overRight && !overStar && !overEye)
                    rightClicked = true;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !overLeft && !overRight && !overStar && !overEye)
                    doubleClicked = true;
                if (overStar)
                    ImGui.SetTooltip(isFavourite ? "Click to remove from favourites" : "Click to add to favourites");
                else if (overEye)
                    ImGui.SetTooltip("Click to hide from the gallery and exports");
                else if (!overLeft && !overRight)
                    DrawCoverCellTooltip(design);
            }
        }

        if (bulkSelectMode && bulkSelectedIds.Contains(design.Id))
        {
            ImGui.GetWindowDrawList().AddRect(thumbStart, cellMax,
                ImGui.ColorConvertFloat4ToU32(UiTheme.StateOn), 4f, ImDrawFlags.None, 2.5f);
        }
        else if (selectedDesign == design.Id)
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
            dl.AddRectFilled(starMin, starMax,
                ImGui.ColorConvertFloat4ToU32(overStar ? UiTheme.IconOverlayBgHovered : UiTheme.IconOverlayBg), 3f);
            var starChar = isFavourite ? "★" : "☆";
            var starColor = isFavourite
                ? ImGui.ColorConvertFloat4ToU32(UiTheme.FavouriteStar)
                : ImGui.ColorConvertFloat4ToU32(UiTheme.FavouriteStarOff);
            var starTextSize = ImGui.CalcTextSize(starChar);
            var starCenter = (starMin + starMax) * 0.5f;
            dl.AddText(new Vector2(starCenter.X - starTextSize.X * 0.5f, starCenter.Y - starTextSize.Y * 0.5f),
                starColor, starChar);
        }

        if (hasMissingMod)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(warnMin, warnMax, ImGui.ColorConvertFloat4ToU32(UiTheme.IconOverlayBg), 3f);
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                var warnChar = FontAwesomeIcon.ExclamationTriangle.ToIconString();
                var warnTextSize = ImGui.CalcTextSize(warnChar);
                var warnCenter = (warnMin + warnMax) * 0.5f;
                dl.AddText(new Vector2(warnCenter.X - warnTextSize.X * 0.5f, warnCenter.Y - warnTextSize.Y * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(UiTheme.ErrorText), warnChar);
            }
        }

        // The hide-eye only appears on hover: a visible cell is never hidden, so there is no persistent state to show.
        if (imageHovered)
        {
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(eyeMin, eyeMax,
                ImGui.ColorConvertFloat4ToU32(overEye ? UiTheme.IconOverlayBgHovered : UiTheme.IconOverlayBg), 3f);
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
        GalleryDraw.IndentForCenteredLabel(thumbWidth, label);

        var hasColor = design.Color != 0;
        using (ImRaii.PushColor(ImGuiCol.Text, design.Color, hasColor))
            ImGui.TextUnformatted(label);
        if (label != fullName && ImGui.IsItemHovered())
            ImGui.SetTooltip(fullName);

        if (clicked)
            selectedDesign = design.Id;
        if (shiftClicked)
        {
            selectedDesign = design.Id;
            RevealDesignInTree(design.Id);
        }
        if (doubleClicked)
        {
            selectedDesign = design.Id;
            ApplyDesignById(design.Id);
        }

        if (rightClicked)
            ImGui.OpenPopup("##cellContextMenu");
        DrawCellContextMenu(design);
    }

    private void DrawCellContextMenu(DesignLeaf design)
    {
        using var popup = ImRaii.Popup("##cellContextMenu");
        if (!popup.Success)
            return;

        if (ImGui.MenuItem("Apply"))
        {
            selectedDesign = design.Id;
            ApplyDesignById(design.Id);
        }

        DrawApplySingleSlotSubmenu(design);

        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);
        if (ImGui.MenuItem(isFavourite ? "Remove from Favourites" : "Add to Favourites"))
        {
            if (isFavourite)
                plugin.Configuration.FavouriteDesigns.Remove(design.Id);
            else
                plugin.Configuration.FavouriteDesigns.Add(design.Id);
            plugin.Configuration.Save();
            favouriteVersion++;
        }

        // Visible cells are never hidden (see the eye-icon overlay above), so this is always an add.
        if (ImGui.MenuItem("Hide"))
        {
            plugin.Configuration.HiddenDesigns.Add(design.Id);
            plugin.Configuration.Save();
            hiddenVersion++;
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Open in Edit Mode"))
        {
            selectedDesign = design.Id;
            RevealDesignInTree(design.Id);
        }

        if (plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var outfit)
            && outfit.Source == DesignSource.Glamourer
            && ImGui.MenuItem("Open in Glamourer"))
            plugin.Glamourer.OpenInGlamourer(design.Id, design.DisplayName);
    }

    // Same "Apply Just This" mechanism the design detail pane's equipment rows already use, just
    // reachable straight from the gallery grid without opening the design first.
    private void DrawApplySingleSlotSubmenu(DesignLeaf design)
    {
        if (!plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var outfit))
            return;

        var slotMap = outfit.Equipment.ToDictionary(e => e.Slot);
        var bonusMap = outfit.BonusItems.ToDictionary(b => b.Slot);

        // ItemId == 0 alone misses Glamourer's other "empty slot" sentinels (its "nothing"/
        // "smallclothes" ids, up near uint.MaxValue) - checking the resolved name against
        // NothingItemName catches every case ResolveItemName/ResolveBonusItemName already know about.
        var equipmentSlots = DesignDetailView.SlotDisplay
            .Where(s => slotMap.ContainsKey(s.Slot))
            .Select(s => (s.Slot, s.Label, ItemName: plugin.GameData.ResolveItemName(slotMap[s.Slot].ItemId)))
            .Where(s => s.ItemName != GameDataService.NothingItemName)
            .ToList();
        var bonusSlots = DesignDetailView.BonusSlotDisplay
            .Where(b => bonusMap.ContainsKey(b.SlotKey))
            .Select(b => (b.SlotKey, b.Label, ItemName: plugin.GameData.ResolveBonusItemName(b.SlotKey, bonusMap[b.SlotKey].ItemId)))
            .Where(b => b.ItemName != GameDataService.NothingItemName)
            .ToList();

        using var subMenu = ImRaii.Menu("Apply Single Slot", equipmentSlots.Count > 0 || bonusSlots.Count > 0);
        if (!subMenu.Success)
            return;

        foreach (var (slot, label, itemName) in equipmentSlots)
            if (ImGui.MenuItem($"{label}: {itemName}"))
                plugin.DesignApply.ApplySingleEquipmentSlot(design.Id, slot, label);

        foreach (var (slotKey, label, itemName) in bonusSlots)
            if (ImGui.MenuItem($"{label}: {itemName}"))
                plugin.DesignApply.ApplySingleBonusItem(design.Id, slotKey, label);
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

    private void DrawBulkAddTagPopup()
    {
        using var popup = ImRaii.Popup(BulkAddTagPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing() || bulkAddTagReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            bulkAddTagReclaimFocus = false;
        }

        ImGui.TextDisabled($"Adding to {bulkSelectedIds.Count} selected design(s).");
        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##bulkAddTagSearch", "Type or search a tag...",
            ref bulkAddTagSearchText, 64, ImGuiInputTextFlags.EnterReturnsTrue);

        var trimmed = bulkAddTagSearchText.Trim();
        var existingTags = plugin.Configuration.DistinctSortedTags()
            .Where(t => trimmed.Length == 0 || t.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var isNewTag = trimmed.Length > 0 && !existingTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase);

        void ApplyTag(string tag)
        {
            foreach (var selectedId in bulkSelectedIds)
                if (plugin.Configuration.CachedOutfits.TryGetValue(selectedId, out var outfit))
                    plugin.Configuration.AddTag(selectedId, outfit, tag);
            bulkAddTagSearchText = string.Empty;
            bulkAddTagReclaimFocus = true;
        }

        if (submitted)
        {
            if (trimmed.Length > 0)
                ApplyTag(trimmed);
            else
                ImGui.CloseCurrentPopup();
        }

        ImGui.Separator();

        if (isNewTag && ImGui.Selectable($"Add new tag \"{trimmed}\""))
            ApplyTag(trimmed);

        if (existingTags.Count == 0)
        {
            if (!isNewTag)
                ImGui.TextDisabled(trimmed.Length > 0 ? "No matching tags." : "No tags yet.");
            return;
        }

        if (isNewTag)
            ImGui.Separator();

        var listHeight = Math.Min(existingTags.Count, 8) * ImGui.GetTextLineHeightWithSpacing();
        using var scroll = ImRaii.Child("BulkAddTagList", new Vector2(220 * ImGuiHelpers.GlobalScale, listHeight), false);
        if (!scroll.Success)
            return;

        foreach (var tag in existingTags)
            if (ImGui.Selectable(tag))
                ApplyTag(tag);
    }

    private void DrawCoverCellTooltip(DesignLeaf design)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(design.DisplayName);
        if (!string.IsNullOrEmpty(design.FullPath))
            ImGui.TextDisabled(design.FullPath);
        ImGui.TextDisabled("Double-click to apply");
        ImGui.TextDisabled("Shift+click to show in the tree");
        ImGui.TextDisabled("Right-click for more options");
        ImGui.EndTooltip();
    }
}
