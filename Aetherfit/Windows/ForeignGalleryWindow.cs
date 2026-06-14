using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Sharing;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// Read-only viewer for an imported gallery. Renders images + basic info only and deliberately exposes NO apply,
// favourite, edit, or job-editing actions, so a foreign gallery can never be applied or mutated.
public sealed class ForeignGalleryWindow : Window, IDisposable
{
    private const int CoverColumns = 4;
    private const float CoverMinThumbSize = 96f;
    private const float CoverAspectRatio = 3f / 2f;
    private const string AddFilterPopupId = "ForeignAddFilterPopup";

    private readonly Plugin plugin;

    private ForeignGallery? gallery;
    private readonly Dictionary<Guid, int> imageIndex = new();

    private string filterName = string.Empty;
    private readonly HashSet<string> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> filterJobs = new();
    private string filterSearchText = string.Empty;

    public ForeignGalleryWindow(Plugin plugin)
        : base("Aetherfit — Shared Gallery##AetherfitForeignGallery", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Show(ForeignGallery foreign)
    {
        // Purge any previously imported gallery's cached images before swapping in the new one.
        if (gallery != null)
            plugin.ImageStorage.ClearForeign(gallery.OriginKey);

        gallery = foreign;
        imageIndex.Clear();
        filterName = string.Empty;
        filterTags.Clear();
        filterJobs.Clear();
        filterSearchText = string.Empty;
        IsOpen = true;
    }

    public override void OnClose()
    {
        if (gallery != null)
        {
            plugin.ImageStorage.ClearForeign(gallery.OriginKey);
            gallery = null;
        }
    }

    public void Dispose()
    {
        if (gallery != null)
            plugin.ImageStorage.ClearForeign(gallery.OriginKey);
    }

    public override void Draw()
    {
        if (gallery == null)
        {
            ImGui.TextDisabled("No shared gallery loaded.");
            return;
        }

        ImGui.SetWindowFontScale(UiTheme.HeaderFontScale);
        ImGui.TextColored(UiTheme.GoldAccent, gallery.SharerLabel);
        ImGui.SetWindowFontScale(1.0f);
        ImGui.SameLine();
        ImGui.TextDisabled($"({gallery.Designs.Count} design(s), read-only)");
        ImGui.Separator();

        DrawFilters();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using var gridChild = ImRaii.Child("ForeignGridScroll", Vector2.Zero, false);
        if (!gridChild.Success)
            return;

        var visible = GetVisibleDesigns();
        if (visible.Count == 0)
        {
            ImGui.TextDisabled(gallery.Designs.Count == 0
                ? "This shared gallery is empty."
                : "No designs match the current filters.");
            return;
        }

        DrawGrid(visible);
    }

    private List<ForeignDesign> GetVisibleDesigns()
    {
        IEnumerable<ForeignDesign> query = gallery!.Designs;

        if (filterName.Length > 0)
            query = query.Where(d => d.Name.IndexOf(filterName, StringComparison.OrdinalIgnoreCase) >= 0);

        if (filterTags.Count > 0)
            query = query.Where(d => filterTags.All(t => d.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));

        if (filterJobs.Count > 0)
            query = query.Where(d => filterJobs.Any(d.Jobs.Contains));

        return query
            .OrderBy(d => d.Name, NaturalStringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool HasAnyFilter => filterName.Length > 0 || filterTags.Count > 0 || filterJobs.Count > 0;

    private void DrawFilters()
    {
        if (!ImGui.CollapsingHeader("Filters", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##foreignNameFilter", "Filter by name...", ref filterName, 64);
        ImGui.PopItemWidth();

        DrawSelectedFilterPills();

        var hasTagOrJob = filterTags.Count > 0 || filterJobs.Count > 0;
        var addLabel = hasTagOrJob ? "Add tag or job..." : "Filter by tag(s) or job...";
        if (ImGui.Button(addLabel, new Vector2(-1, 0)))
        {
            filterSearchText = string.Empty;
            ImGui.OpenPopup(AddFilterPopupId);
        }
        DrawAddFilterPopup();

        using (ImRaii.Disabled(!HasAnyFilter))
        {
            if (ImGui.SmallButton("Clear filters"))
            {
                filterName = string.Empty;
                filterTags.Clear();
                filterJobs.Clear();
            }
        }
    }

    private void DrawSelectedFilterPills()
    {
        string? tagToRemove = null;
        foreach (var tag in filterTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            if (Pills.DrawRemovable(tag, $"foreignFilterTag{tag}"))
                tagToRemove = tag;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Remove \"{tag}\"");
            ImGui.SameLine();
        }
        if (tagToRemove != null)
            filterTags.Remove(tagToRemove);

        uint? jobToRemove = null;
        foreach (var job in filterJobs.OrderBy(j => j))
        {
            var name = plugin.GameData.ResolveJobName(job);
            if (Pills.DrawRemovable(name, $"foreignFilterJob{job}"))
                jobToRemove = job;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Remove \"{name}\"");
            ImGui.SameLine();
        }
        if (jobToRemove is { } removeJob)
            filterJobs.Remove(removeJob);

        if (filterTags.Count > 0 || filterJobs.Count > 0)
            ImGui.NewLine();
    }

    private void DrawAddFilterPopup()
    {
        using var popup = ImRaii.Popup(AddFilterPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##foreignFilterSearch", "Search tags or jobs...", ref filterSearchText, 64);
        ImGui.Separator();

        var availableTags = gallery!.Designs
            .SelectMany(d => d.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !filterTags.Contains(t) &&
                        (filterSearchText.Length == 0 || t.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var availableJobs = gallery.Designs
            .SelectMany(d => d.Jobs)
            .Distinct()
            .Where(j => !filterJobs.Contains(j))
            .Select(j => (RowId: j, Name: plugin.GameData.ResolveJobName(j)))
            .Where(j => filterSearchText.Length == 0 || j.Name.Contains(filterSearchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (availableTags.Count == 0 && availableJobs.Count == 0)
        {
            ImGui.TextDisabled(filterSearchText.Length > 0 ? "No matching tags or jobs." : "Nothing left to filter by.");
        }
        else
        {
            var rowHeight = ImGui.GetTextLineHeightWithSpacing();
            var listHeight = Math.Min(availableTags.Count + availableJobs.Count, 10) * rowHeight;
            using var scroll = ImRaii.Child("ForeignTagJobList", new Vector2(240 * ImGuiHelpers.GlobalScale, listHeight), false);
            if (scroll.Success)
            {
                foreach (var tag in availableTags)
                {
                    if (ImGui.Selectable($"{tag}##foreignAddTag{tag}"))
                    {
                        filterTags.Add(tag);
                        filterSearchText = string.Empty;
                    }
                }

                var lineH = ImGui.GetTextLineHeight();
                foreach (var job in availableJobs)
                {
                    var icon = plugin.GameData.GetJobIcon(job.RowId);
                    if (icon != null)
                    {
                        ImGui.Image(icon.Handle, new Vector2(lineH, lineH));
                        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                    }
                    if (ImGui.Selectable($"{job.Name}##foreignAddJob{job.RowId}"))
                    {
                        filterJobs.Add(job.RowId);
                        filterSearchText = string.Empty;
                    }
                }
            }
        }

        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 0)))
            ImGui.CloseCurrentPopup();
    }

    private void DrawGrid(List<ForeignDesign> visible)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var thumbWidth = Math.Max(CoverMinThumbSize, (avail - (CoverColumns - 1) * spacing) / CoverColumns);
        var thumbHeight = thumbWidth * CoverAspectRatio;

        for (var i = 0; i < visible.Count; i++)
        {
            if (i % CoverColumns != 0)
                ImGui.SameLine();
            DrawCell(visible[i], thumbWidth, thumbHeight);
        }
    }

    private void DrawCell(ForeignDesign design, float thumbWidth, float thumbHeight)
    {
        using var id = ImRaii.PushId(design.SourceId.ToString());
        using var group = ImRaii.Group();

        var thumbStart = ImGui.GetCursorScreenPos();
        var thumbVec = new Vector2(thumbWidth, thumbHeight);
        var containerAspect = thumbWidth / thumbHeight;

        var images = new List<string>();
        if (design.CoverPath != null) images.Add(design.CoverPath);
        images.AddRange(design.AdditionalPaths);

        var imgIdx = GalleryDraw.ResolveImageIndex(imageIndex, design.SourceId, images.Count);
        var currentImage = images.Count > 0 ? images[imgIdx] : null;

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

        var hasArrows = design.AdditionalPaths.Count > 0;
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
            if (canPrev) GalleryDraw.DrawChevron(dl, leftMin, leftMax, isLeft: true, hovered: overLeft);
            if (canNext) GalleryDraw.DrawChevron(dl, rightMin, rightMax, isLeft: false, hovered: overRight);
        }

        if (imageHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (overLeft) imageIndex[design.SourceId] = imgIdx - 1;
                else if (overRight) imageIndex[design.SourceId] = imgIdx + 1;
            }
            if (!overLeft && !overRight)
                DrawCellTooltip(design);
        }

        var label = design.Name;
        var labelWidth = ImGui.CalcTextSize(label).X;
        var indent = Math.Max(0f, (thumbWidth - labelWidth) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + thumbWidth);
        ImGui.TextUnformatted(label);
        ImGui.PopTextWrapPos();
    }

    private void DrawCellTooltip(ForeignDesign design)
    {
        var panelWidth = 300f * ImGuiHelpers.GlobalScale;

        ImGui.BeginTooltip();

        ImGui.PushStyleColor(ImGuiCol.Text, UiTheme.GoldAccent);
        ImGui.PushTextWrapPos(panelWidth);
        ImGui.TextUnformatted(design.Name);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        var hasDetails = !string.IsNullOrWhiteSpace(design.Description)
                         || design.Tags.Count > 0
                         || design.Jobs.Count > 0;

        if (!string.IsNullOrWhiteSpace(design.Description))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Description");
            ImGui.PushTextWrapPos(panelWidth);
            ImGui.TextUnformatted(design.Description);
            ImGui.PopTextWrapPos();
        }

        if (design.Tags.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Tags");
            DrawTagPills(design.Tags, panelWidth);
        }

        if (design.Jobs.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Job Associations");
            DrawJobAssociations(design.Jobs, panelWidth);
        }

        if (!hasDetails)
            ImGui.TextDisabled("No additional details.");

        ImGui.EndTooltip();
    }

    // Renders tags as wrapping, non-interactive coloured chips within maxWidth.
    private static void DrawTagPills(IReadOnlyList<string> tags, float maxWidth)
    {
        var dl = ImGui.GetWindowDrawList();
        var padX = 6f * ImGuiHelpers.GlobalScale;
        var padY = 2f * ImGuiHelpers.GlobalScale;
        var spacing = 4f * ImGuiHelpers.GlobalScale;
        var rounding = 6f * ImGuiHelpers.GlobalScale;
        var lineH = ImGui.GetTextLineHeight();
        var pillH = lineH + padY * 2;

        var origin = ImGui.GetCursorScreenPos();
        var x = origin.X;
        var y = origin.Y;
        var pillColor = ImGui.ColorConvertFloat4ToU32(UiTheme.PillBase);
        var textColor = ImGui.ColorConvertFloat4ToU32(Vector4.One);

        foreach (var tag in tags)
        {
            var textSize = ImGui.CalcTextSize(tag);
            var pillW = textSize.X + padX * 2;
            if (x + pillW > origin.X + maxWidth && x > origin.X)
            {
                x = origin.X;
                y += pillH + spacing;
            }
            dl.AddRectFilled(new Vector2(x, y), new Vector2(x + pillW, y + pillH), pillColor, rounding);
            dl.AddText(new Vector2(x + padX, y + padY), textColor, tag);
            x += pillW + spacing;
        }

        ImGui.Dummy(new Vector2(maxWidth, (y - origin.Y) + pillH));
    }

    // Renders job associations as wrapping icon + name pairs within maxWidth.
    private void DrawJobAssociations(IReadOnlyList<uint> jobs, float maxWidth)
    {
        var lineH = ImGui.GetTextLineHeight();
        var iconSize = new Vector2(lineH, lineH);
        var iconGap = ImGui.GetStyle().ItemInnerSpacing.X;
        var itemGap = 10f * ImGuiHelpers.GlobalScale;

        var first = true;
        var lineWidth = 0f;
        foreach (var job in jobs)
        {
            var name = plugin.GameData.ResolveJobName(job);
            var icon = plugin.GameData.GetJobIcon(job);
            var itemW = iconSize.X + iconGap + ImGui.CalcTextSize(name).X;

            if (first)
                lineWidth = itemW;
            else if (lineWidth + itemGap + itemW <= maxWidth)
            {
                ImGui.SameLine(0, itemGap);
                lineWidth += itemGap + itemW;
            }
            else
                lineWidth = itemW;
            first = false;

            if (icon != null)
            {
                ImGui.Image(icon.Handle, iconSize);
                ImGui.SameLine(0, iconGap);
            }
            ImGui.TextUnformatted(name);
        }
    }
}
