using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;
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
    private Guid? viewerFollowedDesign;
    private DesignLeaf? hoveredDesignForTooltip;

    // Session-only: the Edit Mode tree groups designs by job association, by tag, or by source
    // instead of folder path. At most one grouping is active at a time.
    private bool groupByJob;
    private bool groupByTags;
    private bool groupBySource;

    // Cover Mode's own grouping toggles - independent of Edit Mode's groupBy* above.
    private bool coverGroupByJob;
    private bool coverGroupByTags;
    private bool coverGroupBySource;

    // When a filter is active we force every matching tree node open and keep note of the previous state so it can be restored whe nthe filters are cleared
    private readonly Dictionary<uint, bool> treeOpenSnapshot = new();
    private bool wasFilterActive;
    // Set for the single frame after the filter changes. Pops matching nodes open to show results, but
    // otherwise stays out of the way so folders can still be collapsed.
    private bool expandTreesForFilter;

    private Guid? revealDesignInTree;
    private List<string>? revealDesignFolderPath;
    private FilterSnapshot filterSnapshot;
    private Dictionary<string, bool> filterTagsSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, bool> filterJobsSnapshot = new();

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
        : base($"Aetherfit - {Plugin.Version}###AetherfitMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    public override void OnOpen()
    {
        coverMode = plugin.Configuration.DefaultToCoverMode;
        groupByJob = false;
        groupByTags = false;
        groupBySource = false;
        coverGroupByJob = false;
        coverGroupByTags = false;
        coverGroupBySource = false;
        RefreshDesigns();
    }

    public void OpenDesign(Guid id)
    {
        // A design from a currently-disabled source can still be lingering in CachedOutfits for a
        // moment (e.g. a Health Report row clicked right after toggling the source off, before the
        // next refresh purges it) - refuse to open it rather than briefly showing something the user
        // just asked to hide.
        if (plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)
            && !plugin.Configuration.IsProviderEnabled(outfit.Source))
            return;

        pendingRevealDesign = id;
        IsOpen = true;
    }

    // Switches to the plain folder tree (out of Cover Mode and out of any grouped-by view, since the
    // reveal path below only knows how to walk the plain FolderNode tree) and queues a one-shot
    // request that DrawTree picks up to force every ancestor folder open and scroll to the leaf.
    private void RevealDesignInTree(Guid id)
    {
        coverMode = false;
        groupByJob = false;
        groupByTags = false;
        groupBySource = false;

        var path = FindDesignFolderPath(root, id, new List<string>());
        if (path == null)
            return; // not present in the tree right now (e.g. filtered out)

        revealDesignInTree = id;
        revealDesignFolderPath = path;
    }

    private static List<string>? FindDesignFolderPath(FolderNode node, Guid id, List<string> pathSoFar)
    {
        if (node.Designs.Any(d => d.Id == id))
            return new List<string>(pathSoFar);

        foreach (var (name, folder) in node.Folders)
        {
            pathSoFar.Add(name);
            var found = FindDesignFolderPath(folder, id, pathSoFar);
            if (found != null)
                return found;
            pathSoFar.RemoveAt(pathSoFar.Count - 1);
        }
        return null;
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

        // Checked once per frame so it catches selection changes from anywhere (tree, gallery, random applies).
        if (selectedDesign != viewerFollowedDesign)
        {
            viewerFollowedDesign = selectedDesign;
            if (plugin.Configuration.ImageViewerFollowsSelection && selectedDesign is { } followId)
                plugin.ImageViewer.SyncTo(plugin.ImageStorage.GetCoverPath(followId));
        }
    }

    // A refresh in flight: the design list is fetched up front (one cheap call per provider), while
    // the per-design metadata calls are spread over framework ticks so a large collection doesn't
    // stall a frame. The previous CachedOutfits stay live until the new set is complete.
    private sealed class RefreshJob
    {
        public required IReadOnlyList<(IDesignProvider Provider, Guid NativeId, Guid AetherfitId)> Designs { get; init; }
        public required HashSet<DesignSource> FailedProviders { get; init; }
        public Dictionary<Guid, CachedOutfit> Metadata { get; } = new();
        public int Index;
    }

    private RefreshJob? activeRefresh;

    public bool IsRefreshing => activeRefresh != null;

    private void RefreshDesigns()
    {
        var entries = new List<(IDesignProvider Provider, Guid NativeId, Guid AetherfitId)>();
        var leaves = new List<DesignLeaf>();
        var errors = new List<string>();
        var failedProviders = new HashSet<DesignSource>();
        var multipleProviders = plugin.DesignProviders.Count > 1;

        foreach (var provider in plugin.DesignProviders)
        {
            // A disabled source is never fetched at all - not just filtered from the tree afterward -
            // so it can't surface an error notice (e.g. Glamour Plate's "not loaded this zone") while
            // switched off. Unlike a genuine fetch failure (below), a disabled source is deliberately
            // left out of failedProviders too - see FinishRefresh, which only preserves a failed
            // provider's prior cache, not a disabled one's.
            if (!plugin.Configuration.IsProviderEnabled(provider.Source))
                continue;

            var result = provider.FetchDesignList();
            if (result.Error != null)
            {
                errors.Add(multipleProviders ? $"{provider.DisplayName}: {result.Error}" : result.Error);
                failedProviders.Add(provider.Source);
            }

            foreach (var info in result.Designs)
            {
                var aetherfitId = DesignIdentity.Resolve(plugin.Configuration, provider.Source, info.NativeId);
                entries.Add((provider, info.NativeId, aetherfitId));
                // Sources share the same tree by default (no per-provider top-level folder) - "Group
                // by source" (MainWindow.SourceTree.cs) is the opt-in view that separates them.
                leaves.Add(new DesignLeaf(aetherfitId, info.DisplayName, info.FullPath, info.Color, info.SourceSubGroup));
            }
        }

        // Total failure (nothing fetched at all) aborts entirely - CachedOutfits and everything keyed
        // off it stays exactly as-is rather than a redundant rebuild that would just reproduce the same
        // state. A *partial* failure (some providers succeeded) falls through instead: FinishRefresh
        // preserves whatever a failed provider's own designs already were, so one source's transient
        // error can't wipe another source's - or its own previous - cached data/favourites/tags.
        if (entries.Count == 0 && errors.Count > 0)
        {
            root = new FolderNode();
            designsCount = 0;
            designsError = errors[0];
            activeRefresh = null;
            designListGeneration++;
            return;
        }

        // The tree only needs the list, so it shows immediately; metadata streams in behind it.
        root = BuildFolderTree(leaves);
        designsCount = leaves.Count;
        designsError = errors.Count > 0 ? string.Join("\n", errors) : null;
        designListGeneration++;

        activeRefresh = new RefreshJob { Designs = entries, FailedProviders = failedProviders };
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (activeRefresh is not { } job)
            return;

        // A small per-tick budget keeps the game responsive while the metadata streams in.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (job.Index < job.Designs.Count && sw.ElapsedMilliseconds < 4)
        {
            var (provider, nativeId, aetherfitId) = job.Designs[job.Index++];
            if (provider.FetchDesignMetadata(nativeId) is { } outfit)
            {
                outfit.Source = provider.Source;
                outfit.ProviderDesignId = nativeId;

                var meta = plugin.Configuration.GetOrSeedDesignMeta(aetherfitId, outfit.GlamourerDescription, outfit.GlamourerTags);
                outfit.Description = meta.Description;
                outfit.Tags = new List<string>(meta.Tags);
                outfit.LastAppliedAt = meta.LastApplied;
                job.Metadata[aetherfitId] = outfit;

                if (!plugin.ImageStorage.HasCover(aetherfitId) && provider.GetNativeImagePath(nativeId) is { } nativeImagePath)
                    plugin.ImageStorage.SetCover(aetherfitId, nativeImagePath);
            }
        }

        if (job.Index >= job.Designs.Count)
            FinishRefresh(job);
    }

    private void FinishRefresh(RefreshJob job)
    {
        activeRefresh = null;

        // A provider that failed to fetch this round keeps whatever it last had cached, rather than
        // a transient error wiping its designs (and their favourites/hidden/tags/job associations)
        // out of Configuration entirely. A *disabled* provider gets no such preservation - disabling
        // a source is a deliberate choice to remove it from the library, not a transient error to ride
        // out, so its designs (and everything keyed off them) fall out of validIds below and get
        // pruned by the same cleanup that already handles a design no longer existing at all.
        var merged = new Dictionary<Guid, CachedOutfit>(job.Metadata);
        if (job.FailedProviders.Count > 0)
            foreach (var (existingId, outfit) in plugin.Configuration.CachedOutfits)
                if (job.FailedProviders.Contains(outfit.Source))
                    merged.TryAdd(existingId, outfit);

        plugin.Configuration.CachedOutfits = merged;
        plugin.OutfitCache.Save();

        // Mods might have changed since last time, so clear the affected-item caches and let the
        // "(Appearance affected by ...)" notes rebuild from fresh Penumbra data.
        plugin.Penumbra.ClearChangedItemsCache();
        affectedByCache.Clear();

        var validIds = new HashSet<Guid>(merged.Keys);
        plugin.ImageStorage.CleanupRemovedDesigns(validIds);

        var staleJobAssociations = plugin.Configuration.DesignJobAssociations.Keys
            .Where(k => !validIds.Contains(k))
            .ToList();
        foreach (var stale in staleJobAssociations)
            plugin.Configuration.DesignJobAssociations.Remove(stale);

        var staleMeta = plugin.Configuration.DesignMeta.Keys
            .Where(k => !validIds.Contains(k))
            .ToList();
        foreach (var stale in staleMeta)
            plugin.Configuration.DesignMeta.Remove(stale);

        var staleHealthIgnores = plugin.Configuration.IgnoredHealthChecks.Keys
            .Where(k => !validIds.Contains(k))
            .ToList();
        foreach (var stale in staleHealthIgnores)
            plugin.Configuration.IgnoredHealthChecks.Remove(stale);

        plugin.Configuration.FavouriteDesigns.RemoveWhere(id => !validIds.Contains(id));
        plugin.Configuration.HiddenDesigns.RemoveWhere(id => !validIds.Contains(id));
        CleanupStaleLayers(validIds);

        if (selectedDesign is { } sid && !validIds.Contains(sid))
            selectedDesign = null;

        plugin.Configuration.Save();

        // Metadata feeds the filters and detail panes, so the cached visible list must rebuild.
        designListGeneration++;
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

        using (ImRaii.Disabled(IsRefreshing))
        {
            if (IconTextButton(FontAwesomeIcon.Sync, "Refresh"))
                RefreshDesigns();
        }
        ImGui.SameLine();

        // Each dropdown's individual entries disable themselves against their own relevant busy
        // flag (see DrawSharePopup/DrawOpenGalleryPopup) rather than gating the whole button, so
        // e.g. waiting on a live-share peer doesn't lock out an unrelated plain export/import.
        if (IconTextButton(FontAwesomeIcon.Upload, "Export Gallery", dropdown: true))
            ImGui.OpenPopup("##sharePopup");
        DrawSharePopup();
        ImGui.SameLine();

        if (IconTextButton(FontAwesomeIcon.FolderOpen, "Open Shared Gallery", dropdown: true))
            ImGui.OpenPopup("##openGalleryPopup");
        DrawOpenGalleryPopup();
        ImGui.SameLine();

        if (IconTextButton(FontAwesomeIcon.Stethoscope, "Health Report", warning: HasHealthIssues(),
                warningTooltip: "Some designs have health issues"))
            plugin.ToggleHealthReportUi();

        var countText = IsRefreshing && activeRefresh is { } job
            ? $"Loading {job.Index}/{designsCount}..."
            : designsCount == 1 ? "1 design" : $"{designsCount} designs";
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

    private static float IconTextButtonWidth(FontAwesomeIcon icon, string label, bool dropdown = false, bool warning = false)
    {
        var style = ImGui.GetStyle();
        float iconW, warningW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            iconW = ImGui.CalcTextSize(icon.ToIconString()).X;
            warningW = warning ? style.ItemInnerSpacing.X + ImGui.CalcTextSize(FontAwesomeIcon.ExclamationTriangle.ToIconString()).X : 0f;
        }
        var caretW = dropdown ? style.ItemInnerSpacing.X + (ImGui.GetFontSize() * 0.5f) : 0f;
        return (style.FramePadding.X * 2) + iconW + style.ItemInnerSpacing.X + ImGui.CalcTextSize(label).X + warningW + caretW;
    }

    // Icon + label button, with a caret when it opens a menu and/or a trailing warning icon+tooltip.
    // Drawn by hand because a single ImGui button can't mix the icon font with the text font.
    internal static bool IconTextButton(FontAwesomeIcon icon, string label, bool dropdown = false,
        bool warning = false, string? warningTooltip = null)
    {
        var style = ImGui.GetStyle();
        var iconStr = icon.ToIconString();
        float iconW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            iconW = ImGui.CalcTextSize(iconStr).X;
        var textW = ImGui.CalcTextSize(label).X;
        var caretHalf = ImGui.GetFontSize() * 0.25f;

        var clicked = ImGui.Button($"##iconText{label}", new Vector2(IconTextButtonWidth(icon, label, dropdown, warning), 0));
        var hovered = ImGui.IsItemHovered();
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
        x += textW;

        if (warning)
        {
            x += style.ItemInnerSpacing.X;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                dl.AddText(new Vector2(x, textY), ImGui.GetColorU32(UiTheme.ErrorText), FontAwesomeIcon.ExclamationTriangle.ToIconString());
        }

        if (dropdown)
        {
            x += style.ItemInnerSpacing.X;
            var centerY = (min.Y + max.Y) * 0.5f;
            dl.AddTriangleFilled(
                new Vector2(x, centerY - (caretHalf * 0.5f)),
                new Vector2(x + (caretHalf * 2f), centerY - (caretHalf * 0.5f)),
                new Vector2(x + caretHalf, centerY + (caretHalf * 0.75f)),
                color);
        }

        if (warning && hovered && warningTooltip != null)
            ImGui.SetTooltip(warningTooltip);

        return clicked;
    }

    // The "Share your Designs" dropdown: export everything, or just the designs left after the active filters.
    private void DrawSharePopup()
    {
        using var popup = ImRaii.Popup("##sharePopup");
        if (!popup.Success)
            return;

        if (ImGui.BeginMenu("Create Bundle File"))
        {
            if (ImGui.MenuItem("All Designs"))
                OpenExportGalleryDialog();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Export every cached design.");

            // Filtered export is only meaningful while a filter is narrowing the list.
            var hasFilter = HasAnyFilter;
            using (ImRaii.Disabled(!hasFilter))
            {
                if (ImGui.MenuItem("Filtered Designs"))
                    OpenExportGalleryDialog(CollectVisibleDesignIds());
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(hasFilter
                    ? "Export only the designs currently shown by the active filters."
                    : "Set a filter first to export only the designs that remain visible.");

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Export Look Book"))
        {
            if (ImGui.MenuItem("All Designs"))
                OpenExportLookBookDialog();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A printable PDF of design covers - for showing off, not re-importing.");

            var hasLookBookFilter = HasAnyFilter;
            using (ImRaii.Disabled(!hasLookBookFilter))
            {
                if (ImGui.MenuItem("Filtered Designs"))
                    OpenExportLookBookDialog(CollectVisibleDesignIds());
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(hasLookBookFilter
                    ? "Export only the designs currently shown by the active filters."
                    : "Set a filter first to export only the designs that remain visible.");

            ImGui.EndMenu();
        }

        ImGui.Separator();

        var liveSharingEnabled = plugin.FeatureFlags.EnableLiveSharing;

        // Not gated on IsBusy: if a share is already running, clicking this just reopens the modal
        // on its current state (pairing code, progress, etc.) instead of starting a new one - the
        // only way back in after the popup's been dismissed by clicking away.
        using (ImRaii.Disabled(!liveSharingEnabled))
        {
            if (ImGui.Selectable("Share Live..."))
                OpenShareLiveDialog();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!liveSharingEnabled
                ? "Live sharing is temporarily disabled."
                : plugin.LiveShare.IsBusy
                    ? "Reopen the share in progress."
                    : "Share your gallery directly with another online player.");
    }

    private void DrawBottomButtons()
    {
        var style = ImGui.GetStyle();
        const string applyLabel = "Apply Selected";
        const string randomLabel = "Apply Random";
        const string byTagLabel = "Apply Random By Tag(s)";

        var hasIncompatibleSelection = selectedDesign is { } warnId
            && plugin.Configuration.CachedOutfits.TryGetValue(warnId, out var warnOutfit)
            && plugin.GameData.DesignHasAnyIncompatibleItems(warnOutfit);
        const string incompatibleTooltip = "This design can only be partially applied on your current character.";

        var applyW = IconTextButtonWidth(FontAwesomeIcon.Check, applyLabel, warning: hasIncompatibleSelection);
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
            if (IconTextButton(FontAwesomeIcon.Check, applyLabel, warning: hasIncompatibleSelection, warningTooltip: incompatibleTooltip)
                && selectedDesign is { } id)
                ApplyDesignById(id);
        }
        ImGui.SameLine();

        if (IconTextButton(FontAwesomeIcon.Random, randomLabel))
        {
            var err = ApplyRandomDesign();
            if (err != null) Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{err}");
        }
        ImGui.SameLine();

        using (ImRaii.Disabled(!AnyDesignHasTags()))
        {
            if (IconTextButton(FontAwesomeIcon.Tags, byTagLabel))
            {
                RebuildAvailableTags();
                ImGui.OpenPopup(ApplyByTagPopupId);
            }
        }
    }

    private bool cachedHasHealthIssues;
    private int cachedHealthIssuesGeneration = -1;
    private int cachedHealthIssuesIgnoreVersion = -1;

    private bool HasHealthIssues()
    {
        if (cachedHealthIssuesGeneration != designListGeneration || cachedHealthIssuesIgnoreVersion != plugin.Configuration.HealthCheckIgnoreVersion)
        {
            cachedHasHealthIssues = plugin.HealthReport.FindMissingModAssociations().Count > 0
                || plugin.HealthReport.FindBrokenItems().Count > 0
                || plugin.HealthReport.FindIncompatibleDesigns().Count > 0
                || plugin.HealthReport.FindDuplicates().Count > 0;
            cachedHealthIssuesGeneration = designListGeneration;
            cachedHealthIssuesIgnoreVersion = plugin.Configuration.HealthCheckIgnoreVersion;
        }
        return cachedHasHealthIssues;
    }

    private bool cachedAnyHasTags;
    private int cachedAnyHasTagsGeneration = -1;

    private bool AnyDesignHasTags()
    {
        if (cachedAnyHasTagsGeneration != designListGeneration)
        {
            cachedAnyHasTags = plugin.Configuration.CachedOutfits.Values.Any(o => o.Tags.Count > 0);
            cachedAnyHasTagsGeneration = designListGeneration;
        }
        return cachedAnyHasTags;
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

    public void RevertAppearance() => plugin.DesignApply.RevertAppearance();

    private void ApplyDesignById(Guid id) => plugin.DesignApply.ApplyDesignById(id);

    public string? ReapplyLastWorn(bool quiet = false)
    {
        var result = plugin.DesignApply.ReapplyLastWorn(quiet);
        if (result.DesignId is { } id) selectedDesign = id;
        return result.Error;
    }

    public string? ApplyRandomDesign()
    {
        var result = plugin.DesignApply.ApplyRandomDesign();
        if (result.DesignId is { } id) selectedDesign = id;
        return result.Error;
    }

    public string? ApplyRandomByTags(IReadOnlyCollection<string> tags, bool favouritesOnly = false)
    {
        var result = plugin.DesignApply.ApplyRandomByTags(tags, favouritesOnly);
        if (result.DesignId is { } id) selectedDesign = id;
        return result.Error;
    }

    public string? ApplyDesignByName(string name)
    {
        var result = plugin.DesignApply.ApplyByName(name);
        if (result.DesignId is { } id) selectedDesign = id;
        return result.Error;
    }

    public string? ApplyRandomFavourite(bool matchCurrentJob)
    {
        var result = plugin.DesignApply.ApplyRandomFavourite(matchCurrentJob);
        if (result.DesignId is { } id) selectedDesign = id;
        return result.Error;
    }

    public string? ApplyRandomByCurrentJob()
    {
        var result = plugin.DesignApply.ApplyRandomByCurrentJob();
        if (result.DesignId is { } id) selectedDesign = id;
        return result.Error;
    }

    private sealed class FolderNode
    {
        public SortedDictionary<string, FolderNode> Folders { get; } = new(NaturalStringComparer.OrdinalIgnoreCase);
        public List<DesignLeaf> Designs { get; } = new();
    }

    // SourceSubGroup is display-only grouping above "folder path" - null for every provider except
    // SimpleGlamourSwitcher (see MainWindow.SourceTree.cs / MainWindow.GallerySourceGroups.cs, the only
    // consumers of this field). Never persisted, rebuilt fresh every refresh like FullPath itself.
    private sealed record DesignLeaf(Guid Id, string DisplayName, string FullPath, uint Color, string? SourceSubGroup = null);
}
