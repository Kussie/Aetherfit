using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private const float RightPaneImageMax = 220f;
    private const float TooltipImageMax = 160f;
    private const float AdditionalThumbSize = 72f;
    private const string ImageHelpText =
        "Click an image to view it full size. Hold Shift and right-click to remove. \"+\" picks a file; \"Snap\" captures from the game.";

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

        // Widen the vertical gap between rows so the mouse rarely sits on the seam between two items and reports both as hovered in the same frame.
        var spacing = ImGui.GetStyle().ItemSpacing;
        hoveredDesignForTooltip = null;
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, spacing.Y + 3)))
            DrawTree(root, hasFilter);

        if (hoveredDesignForTooltip is { } hovered)
            DrawDesignLeafTooltip(hovered);
    }

    private void DrawTree(FolderNode node, bool hasFilter)
    {
        foreach (var (name, folder) in node.Folders)
        {
            if (!FolderHasMatch(folder)) continue;

            if (hasFilter)
            {
                var id = ImGui.GetID(name);
                if (!treeOpenSnapshot.ContainsKey(id))
                    treeOpenSnapshot[id] = ImGui.GetStateStorage().GetInt(id, 0) != 0;
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            }

            if (ImGui.TreeNodeEx(name, ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                DrawTree(folder, hasFilter);
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
            hoveredDesignForTooltip = design;
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
                ImGui.Spacing();

                DrawEquipmentPanel(details);
                DrawModsPanel(details);
            }
        }

        if (details.CreatedAt is { } created)
            DrawDateLine("Created", created);
        if (details.LastEdit is { } edited)
            DrawDateLine("Last edited", edited);
    }

    private void DrawOutfitImageBlock(Guid id)
    {
        var imagePath = plugin.ImageStorage.GetCoverPath(id);
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
                plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.SetCover(id, croppedPath));
        }

        if (deleteRequested)
            plugin.ImageStorage.RemoveCover(id);
    }

    private void DrawAdditionalImagesBlock(Guid id)
    {
        var paths = plugin.ImageStorage.GetAdditionalPaths(id);
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

        if (paths.Count < ImageStorageService.MaxAdditionalImages)
        {
            if (paths.Count > 0)
                ImGui.SameLine();
            if (ImGui.Button("+", new Vector2(thumb, thumb)))
                OpenAdditionalImagePicker(id);

            ImGui.SameLine();
            if (ImGui.Button("Snap", new Vector2(thumb, thumb)))
                plugin.ScreenshotSetup.Begin(croppedPath => plugin.ImageStorage.AddAdditional(id, croppedPath));
        }

        if (toRemoveIndex >= 0)
            plugin.ImageStorage.RemoveAdditional(id, toRemoveIndex);
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
                plugin.ImageStorage.SetCover(id, paths[0]);
            },
            1);
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
                plugin.ImageStorage.AddAdditional(id, paths[0]);
            },
            1);
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
}
