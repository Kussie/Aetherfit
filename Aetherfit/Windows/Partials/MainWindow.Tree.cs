using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;

namespace Aetherfit.Windows;

// The left-pane design tree: folder/leaf drawing, the tree's own guide-line decorations, and the
// per-leaf tooltip. "Create new design" leaf itself lives in MainWindow.CreateDesign.cs, but the plain
// "+"-dot glyph it (and the Personas tree's own "create new persona" leaf) draws stays here alongside
// the other leaf-row drawing helpers.
public partial class MainWindow
{
    // Faint grey for the tree indent guide lines, mirroring Glamourer's design list.
    private static readonly Vector4 TreeGuideColor = UiTheme.TreeGuide;
    // Leaf dot radius as a fraction of the text line height (the bullet glyphs were either too big or too small).
    private const float LeafDotRadius = 0.16f;

    private void DrawLeftPane()
    {
        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, "Your Designs");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.Separator();

        if (ImGui.Button("Gallery Mode >>", new Vector2(-1, 0)))
            coverMode = true;
        ImGui.Separator();

        if (DrawDesignsUnavailableBanner())
            return;

        DrawFilterUi(extraControls: DrawEditModeGroupByControls);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using var treeChild = ImRaii.Child("OutfitTreeScroll", Vector2.Zero, false);
        if (!treeChild.Success)
            return;

        var hasFilter = HasAnyFilter;
        // Whenthe filter clears, restore every tree node we forced open back to its pre-filter state
        if (!hasFilter && wasFilterActive && treeOpenSnapshot.Count > 0)
        {
            var storage = ImGui.GetStateStorage();
            foreach (var (id, wasOpen) in treeOpenSnapshot)
                storage.SetInt(id, wasOpen ? 1 : 0);
            treeOpenSnapshot.Clear();
        }
        wasFilterActive = hasFilter;

        // Only auto-expand on the frame the filter actually changes - that way the user can still collapse
        // folders while a filter just sits there unchanged.
        var snapshot = CaptureFilterSnapshot();
        var filterChanged = snapshot != filterSnapshot
            || !FiltersEqual(filterTags, filterTagsSnapshot)
            || !FiltersEqual(filterJobs, filterJobsSnapshot)
            || !filterEquipmentSlotsSnapshot.SetEquals(filterEquipmentSlots);
        if (hasFilter && filterChanged)
            expandTreesForFilter = true;
        if (filterChanged)
        {
            filterSnapshot = snapshot;
            filterTagsSnapshot = new(filterTags, StringComparer.OrdinalIgnoreCase);
            filterJobsSnapshot = new(filterJobs);
            filterEquipmentSlotsSnapshot = new(filterEquipmentSlots);
        }

        // Cleared each frame - see FolderHasMatch.
        folderMatchCache.Clear();

        // Widen the vertical gap between rows so the mouse rarely sits on the seam between two items and reports both as hovered in the same frame.
        var spacing = ImGui.GetStyle().ItemSpacing;
        hoveredDesignForTooltip = null;
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, spacing.Y + 3)))
        {
            if (plugin.Configuration.ShowVirtualFoldersInTree)
            {
                DrawFavouritesFolderSection();
                DrawRecentlyAddedFolderSection();
                DrawHealthIssuesFolderSection();
                DrawHiddenFolderSection();
            }

            if (plugin.Configuration.ShowDesignsSection)
            {
                var designsOpen = ImGui.TreeNodeEx($"Designs ({designsCount})##designsRoot",
                    ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
                if (designsOpen)
                {
                    if (groupByJob)
                        DrawJobTree(hasFilter);
                    else if (groupByTags)
                        DrawTagTree(hasFilter);
                    else if (groupBySource)
                        DrawSourceTree(hasFilter);
                    else
                        DrawTree(root, hasFilter);

                    ImGui.TreePop();
                }
            }

            if (plugin.Configuration.ShowPersonasSection)
                DrawPersonasSection();
        }

        // Tree's drawn, so we're done with the one-shot expand request - clear it for next frame.
        expandTreesForFilter = false;

        // Safety net: if the reveal target's leaf was never actually reached this frame (e.g. it's
        // hidden by an active filter, or the grouped view got switched back on before this ran), don't
        // leave the request dangling - it would otherwise keep forcing the same folders open forever.
        revealDesignInTree = null;
        revealDesignFolderPath = null;
        revealDesignVariantParent = null;

        if (hoveredDesignForTooltip is { } hovered)
            DrawDesignLeafTooltip(hovered);
    }

    // The groupings are mutually exclusive - ticking one unticks the others (all three can be off).
    private void DrawEditModeGroupByControls()
    {
        if (ImGui.Checkbox("Group by job association", ref groupByJob) && groupByJob)
        {
            groupByTags = false;
            groupBySource = false;
        }
        if (ImGui.Checkbox("Group by tags", ref groupByTags) && groupByTags)
        {
            groupByJob = false;
            groupBySource = false;
        }
        if (ImGui.Checkbox("Group by source", ref groupBySource) && groupBySource)
        {
            groupByJob = false;
            groupByTags = false;
        }

        ImGui.Spacing();
        var showDesigns = plugin.Configuration.ShowDesignsSection;
        if (ImGui.Checkbox("Show Designs section", ref showDesigns))
        {
            plugin.Configuration.ShowDesignsSection = showDesigns;
            plugin.Configuration.Save();
        }
        var showPersonas = plugin.Configuration.ShowPersonasSection;
        if (ImGui.Checkbox("Show Personas section", ref showPersonas))
        {
            plugin.Configuration.ShowPersonasSection = showPersonas;
            plugin.Configuration.Save();
        }
        var showVirtualFolders = plugin.Configuration.ShowVirtualFoldersInTree;
        if (ImGui.Checkbox("Show Virtual Folders", ref showVirtualFolders))
        {
            plugin.Configuration.ShowVirtualFoldersInTree = showVirtualFolders;
            plugin.Configuration.Save();
        }
    }

    private void DrawTree(FolderNode node, bool hasFilter, int depth = 0)
    {
        foreach (var (name, folder) in node.Folders)
        {
            if (!FolderHasMatch(folder)) continue;

            ForceOpenIfFiltering(name, hasFilter);
            if (revealDesignFolderPath is { } revealPath && depth < revealPath.Count && revealPath[depth] == name)
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);

            var rowX = ImGui.GetCursorScreenPos().X;
            var open = ImGui.TreeNodeEx(name, ImGuiTreeNodeFlags.SpanAvailWidth);
            // Connect this node to its parent's vertical guide with a short horizontal tick.
            DrawTreeItemTick(depth, rowX);

            if (open)
            {
                var drawList = ImGui.GetWindowDrawList();
                var guideX = rowX + TreeArrowCenterOffset();
                var guideTop = ImGui.GetCursorScreenPos().Y;

                DrawTree(folder, hasFilter, depth + 1);

                // Stop the line at the vertical centre of the last child row so it reads as connecting to it.
                var guideBottom = ImGui.GetCursorScreenPos().Y
                                  - ImGui.GetStyle().ItemSpacing.Y
                                  - (ImGui.GetTextLineHeight() * 0.5f);
                if (guideBottom > guideTop)
                    drawList.AddLine(new Vector2(guideX, guideTop), new Vector2(guideX, guideBottom),
                        ImGui.ColorConvertFloat4ToU32(TreeGuideColor), ImGuiHelpers.GlobalScale);

                ImGui.TreePop();
            }
        }

        foreach (var design in node.Designs)
        {
            plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var cached);
            if (!DesignOrHiddenVariantMatches(design, cached)) continue;
            if (plugin.Configuration.GetVariantInfo(design.Id) != null) continue; // drawn nested under its parent below

            DrawDesignLeafWithVariants(design, depth);
        }
    }

    // consumeReveal is false for the Favourites pseudo-folder's copy - RevealDesignInTree only resolves
    // a design's real folder path, so only the real-tree copy should scroll to itself.
    private void DrawDesignLeafWithVariants(DesignLeaf design, int depth, bool consumeReveal = true)
    {
        var variantIds = plugin.Configuration.GetVariantsOf(design.Id).Select(kv => kv.Key).ToList();
        var hasVariants = variantIds.Count > 0;

        if (consumeReveal && hasVariants && revealDesignVariantParent == design.Id)
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);

        var rowX = ImGui.GetCursorScreenPos().X;
        var open = DrawDesignLeaf(design, hasVariants);
        DrawTreeItemTick(depth, rowX);

        if (consumeReveal && revealDesignInTree == design.Id)
        {
            ImGui.SetScrollHereY(0.3f);
            revealDesignInTree = null;
            revealDesignFolderPath = null;
            revealDesignVariantParent = null;
        }

        if (!hasVariants) return;

        if (open)
        {
            var drawList = ImGui.GetWindowDrawList();
            var guideX = rowX + TreeArrowCenterOffset();
            var guideTop = ImGui.GetCursorScreenPos().Y;

            foreach (var variantId in variantIds)
            {
                if (!designLeafById.TryGetValue(variantId, out var variantLeaf)) continue;
                plugin.Configuration.CachedOutfits.TryGetValue(variantId, out var variantCached);
                if (!DesignMatchesFilters(variantLeaf, variantCached, ignoreHiddenOnly: true)) continue;

                var childRowX = ImGui.GetCursorScreenPos().X;
                DrawDesignLeaf(variantLeaf, false);
                DrawTreeItemTick(depth + 1, childRowX);

                if (consumeReveal && revealDesignInTree == variantId)
                {
                    ImGui.SetScrollHereY(0.3f);
                    revealDesignInTree = null;
                    revealDesignFolderPath = null;
                    revealDesignVariantParent = null;
                }
            }

            var guideBottom = ImGui.GetCursorScreenPos().Y
                              - ImGui.GetStyle().ItemSpacing.Y
                              - (ImGui.GetTextLineHeight() * 0.5f);
            if (guideBottom > guideTop)
                drawList.AddLine(new Vector2(guideX, guideTop), new Vector2(guideX, guideBottom),
                    ImGui.ColorConvertFloat4ToU32(TreeGuideColor), ImGuiHelpers.GlobalScale);

            ImGui.TreePop();
        }
    }

    // A true duplicate of each favourite's real tree row, not a partition like Gallery's own section.
    // A non-favourited parent shows too if one of its variants is favourited (mirrors the Hidden filter).
    private void DrawFavouritesFolderSection()
    {
        ImGui.Spacing();
        var sectionOpen = ImGui.TreeNodeEx($"Favourites ({plugin.Configuration.FavouriteDesigns.Count})##favouritesRoot",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
        if (!sectionOpen)
            return;

        var candidateIds = new HashSet<Guid>(plugin.Configuration.FavouriteDesigns);
        foreach (var favouriteId in plugin.Configuration.FavouriteDesigns)
            if (plugin.Configuration.GetVariantInfo(favouriteId) is { } info)
                candidateIds.Add(info.ParentId);

        var favourites = new List<DesignLeaf>();
        foreach (var id in candidateIds)
        {
            if (plugin.Configuration.GetVariantInfo(id) != null) continue; // never flat, same convention as the real tree
            if (!designLeafById.TryGetValue(id, out var leaf)) continue; // stale favourite (e.g. deleted design)
            plugin.Configuration.CachedOutfits.TryGetValue(id, out var cached);
            if (!BelongsInFavouritesFolder(leaf, cached)) continue;
            favourites.Add(leaf);
        }
        favourites.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));

        DrawVirtualFolderDesigns(favourites);
        ImGui.TreePop();
    }

    // Everything currently hidden, plus a non-hidden parent that needs showing to host a hidden variant.
    private void DrawHiddenFolderSection()
    {
        var sectionOpen = ImGui.TreeNodeEx($"Hidden ({plugin.Configuration.HiddenDesigns.Count})##hiddenFolderRoot",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
        if (!sectionOpen)
            return;

        var candidateIds = new HashSet<Guid>(plugin.Configuration.HiddenDesigns);
        foreach (var hiddenId in plugin.Configuration.HiddenDesigns)
            if (plugin.Configuration.GetVariantInfo(hiddenId) is { } info)
                candidateIds.Add(info.ParentId);

        var hidden = new List<DesignLeaf>();
        foreach (var id in candidateIds)
        {
            if (plugin.Configuration.GetVariantInfo(id) != null) continue;
            if (!designLeafById.TryGetValue(id, out var leaf)) continue;
            plugin.Configuration.CachedOutfits.TryGetValue(id, out var cached);
            if (!BelongsInHiddenFolder(leaf, cached)) continue;
            hidden.Add(leaf);
        }
        hidden.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));

        DrawVirtualFolderDesigns(hidden);
        ImGui.TreePop();
    }

    // Designs created within the last RecentlyAddedDays (GalleryMode.cs) - a preview, not a move, same
    // idea as Gallery Mode's own section. Variants are excluded, matching Gallery's own exclusion there.
    private void DrawRecentlyAddedFolderSection()
    {
        var cutoff = DateTimeOffset.Now - TimeSpan.FromDays(RecentlyAddedDays);
        var recent = new List<DesignLeaf>();
        foreach (var (id, leaf) in designLeafById)
        {
            if (plugin.Configuration.GetVariantInfo(id) != null) continue;
            if (GetCreatedAt(id) is not { } created || created < cutoff) continue;
            plugin.Configuration.CachedOutfits.TryGetValue(id, out var cached);
            if (!DesignMatchesFilters(leaf, cached)) continue;
            recent.Add(leaf);
        }
        recent.Sort((a, b) => Nullable.Compare(GetCreatedAt(b.Id), GetCreatedAt(a.Id))); // newest first

        var sectionOpen = ImGui.TreeNodeEx($"Recently Added ({recent.Count})##recentlyAddedFolderRoot",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
        if (!sectionOpen)
            return;

        DrawVirtualFolderDesigns(recent);
        ImGui.TreePop();
    }

    // Everything flagged by the Health Report, plus a non-flagged parent that needs showing to host a
    // flagged variant.
    private void DrawHealthIssuesFolderSection()
    {
        var flagged = FlaggedDesignIds();

        var sectionOpen = ImGui.TreeNodeEx($"Health Issues ({flagged.Count})##healthIssuesFolderRoot",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
        if (!sectionOpen)
            return;

        var candidateIds = new HashSet<Guid>(flagged);
        foreach (var flaggedId in flagged)
            if (plugin.Configuration.GetVariantInfo(flaggedId) is { } info)
                candidateIds.Add(info.ParentId);

        var issues = new List<DesignLeaf>();
        foreach (var id in candidateIds)
        {
            if (plugin.Configuration.GetVariantInfo(id) != null) continue;
            if (!designLeafById.TryGetValue(id, out var leaf)) continue;
            plugin.Configuration.CachedOutfits.TryGetValue(id, out var cached);
            if (!BelongsInHealthIssuesFolder(leaf, cached)) continue;
            issues.Add(leaf);
        }
        issues.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName));

        DrawVirtualFolderDesigns(issues);
        ImGui.TreePop();
    }

    // Shared by every virtual folder section above - already-resolved, sorted designs, each still
    // supporting nested variants like a normal top-level tree row.
    private void DrawVirtualFolderDesigns(List<DesignLeaf> designs)
    {
        foreach (var design in designs)
            DrawDesignLeafWithVariants(design, 0, consumeReveal: false);
    }

    private static float TreeArrowCenterOffset()
        => ImGui.GetStyle().FramePadding.X + (ImGui.GetFontSize() * 0.5f);

    private static void DrawTreeItemTick(int depth, float rowX)
    {
        if (depth <= 0)
            return;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var centerY = (min.Y + max.Y) * 0.5f;
        // The parent folder sits one indent level to the left; its arrow tip is where the guide runs.
        var guideX = rowX - ImGui.GetStyle().IndentSpacing + TreeArrowCenterOffset();
        var tickEndX = rowX - (2f * ImGuiHelpers.GlobalScale);
        if (tickEndX <= guideX)
            return;

        ImGui.GetWindowDrawList().AddLine(new Vector2(guideX, centerY), new Vector2(tickEndX, centerY),
            ImGui.ColorConvertFloat4ToU32(TreeGuideColor), ImGuiHelpers.GlobalScale);
    }

    // Keyed by design id; invalidated per-entry when favourite state or the display name changes,
    // so a frequently-redrawn tree of leaves isn't rebuilding this string every frame.
    private readonly Dictionary<Guid, (bool IsFavourite, bool HasVariants, string Name, string Label)> leafLabelCache = new();

    // The leading spaces on a non-favourite leaf just reserve room for the manually-drawn dot on a
    // plain Selectable row. A TreeNodeEx row (hasVariants) already gets that same breathing room for
    // free from its own arrow glyph - which is also where the dot itself ends up sitting - so adding
    // the same padding again on top of it just pushes the name further right than it needs to be.
    private string GetLeafLabel(DesignLeaf design, bool isFavourite, bool hasVariants)
    {
        if (leafLabelCache.TryGetValue(design.Id, out var cached)
            && cached.IsFavourite == isFavourite && cached.HasVariants == hasVariants && cached.Name == design.DisplayName)
            return cached.Label;

        var label = isFavourite
            ? $"★ {design.DisplayName}##{design.Id}"
            : hasVariants
                ? $"{design.DisplayName}##{design.Id}"
                : $"   {design.DisplayName}##{design.Id}";
        leafLabelCache[design.Id] = (isFavourite, hasVariants, design.DisplayName, label);
        return label;
    }

    // Returns whether the row is expanded (only meaningful when hasVariants - a plain leaf always
    // returns false, since it has nothing to expand into).
    private bool DrawDesignLeaf(DesignLeaf design, bool hasVariants)
    {
        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);
        var hasColor = design.Color != 0;
        var selected = selectedDesign == design.Id;
        var label = GetLeafLabel(design, isFavourite, hasVariants);
        var open = false;

        using (ImRaii.PushColor(ImGuiCol.Text, design.Color, hasColor))
        {
            if (hasVariants)
            {
                var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow
                    | (selected ? ImGuiTreeNodeFlags.Selected : 0);
                open = ImGui.TreeNodeEx(label, flags);
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    selectedDesign = design.Id;
            }
            else if (ImGui.Selectable(label, selected))
            {
                selectedDesign = design.Id;
            }

            if (!isFavourite)
                DrawLeafDot(hasColor ? design.Color : ImGui.GetColorU32(ImGuiCol.Text), hasVariants);

            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                selectedDesign = design.Id;
                ApplyDesignById(design.Id);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
            {
                selectedDesign = design.Id;
                if (plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var quickOpenOutfit)
                    && quickOpenOutfit.Source == DesignSource.Glamourer)
                    plugin.Glamourer.OpenInGlamourer(design.Id, design.DisplayName);
            }

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && !ImGui.GetIO().KeyShift)
                ImGui.OpenPopup($"##treeDesignContextMenu_{design.Id}");
        }

        using (var contextMenu = ImRaii.Popup($"##treeDesignContextMenu_{design.Id}"))
        {
            if (contextMenu.Success)
                DrawDesignContextMenu(design, fromGallery: false);
        }

        if (ImGui.IsItemHovered())
            hoveredDesignForTooltip = design;

        return open;
    }

    // A filled dot at the start of a leaf row, sized from the line height and tinted to the design's colour.
    // A TreeNodeEx row (hasVariants) draws its own arrow glyph inset by FramePadding.X from the row's
    // start, so the dot needs the same leftward nudge to land on it instead of drifting right of a plain
    // sibling row's dot.
    private static void DrawLeafDot(uint color, bool hasVariants = false)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineH = ImGui.GetTextLineHeight();
        var x = min.X + (lineH * 0.45f) - (hasVariants ? ImGui.GetStyle().FramePadding.X : 0f);
        var center = new Vector2(x, (min.Y + max.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddCircleFilled(center, lineH * LeafDotRadius, color, 16);
    }

    private void DrawDesignLeafTooltip(DesignLeaf design)
    {
        var imagePath = plugin.Configuration.ShowThumbnailOnHover ? plugin.ImageStorage.GetCoverPath(design.Id) : null;
        var hasPath = !string.IsNullOrEmpty(design.FullPath);

        ImGui.BeginTooltip();
        if (hasPath)
            ImGui.TextUnformatted(design.FullPath);
        if (imagePath != null)
            DrawImageScaled(imagePath, TooltipImageMax * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled("Double-click to apply");
        ImGui.TextDisabled("Shift + right-click to open in Glamourer");
        ImGui.EndTooltip();
    }
}
