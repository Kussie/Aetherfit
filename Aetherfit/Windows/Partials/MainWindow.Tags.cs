using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// The design detail pane's Tags section: the tag pill row itself, the add-tag search popup, and the
// one-time composite-tags help note.
public partial class MainWindow
{
    private const string AddTagPopupId = "AddDesignTagPopup";
    private string addTagSearchText = string.Empty;
    private bool addTagReclaimFocus;

    private void DrawTagsRow(Guid id, CachedOutfit details)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;
        var first = true;

        string? tagToRemove = null;
        foreach (var tag in details.Tags)
        {
            var width = ImGui.CalcTextSize(tag).X;
            Pills.PlaceItem(width, ref first, ref lineRight, cursorStart, spacing, availRight);

            DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, tag);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"Show all designs tagged \"{tag}\"\nShift + right-click to remove");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    filterTags[tag] = true;
                else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                    tagToRemove = tag;
            }
        }

        var addWidth = ImGui.GetFrameHeight();
        Pills.PlaceItem(addWidth, ref first, ref lineRight, cursorStart, spacing, availRight);
        if (ImGuiComponents.IconButton("addTag", FontAwesomeIcon.Plus))
        {
            addTagSearchText = string.Empty;
            // Drop the popup below the button instead of centering on it, so it doesn't cover the tag(s) just added.
            var popupPos = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + ImGui.GetStyle().ItemSpacing.Y + (4f * ImGuiHelpers.GlobalScale));
            ImGui.SetNextWindowPos(popupPos);
            ImGui.OpenPopup(AddTagPopupId);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add tag");

        if (details.Source is DesignSource.Glamourer or DesignSource.Glamaholic)
        {
            var newTagCount = details.GlamourerTags.Count(t => !details.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
            var refreshWidth = ImGui.GetFrameHeight();
            Pills.PlaceItem(refreshWidth, ref first, ref lineRight, cursorStart, spacing, availRight);
            if (ImGuiComponents.IconButton("mergeTags", FontAwesomeIcon.Sync) && newTagCount > 0)
            {
                var added = plugin.Configuration.MergeTagsFromGlamourer(id, details);
                if (added > 0)
                    Plugin.ChatGui.Print($"{Plugin.ChatPrefix}+{added} tag{(added == 1 ? "" : "s")} added from {details.Source}");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(newTagCount > 0
                    ? $"Merge {newTagCount} tag{(newTagCount == 1 ? "" : "s")} in from {details.Source}"
                    : $"No new tags to merge from {details.Source}");
        }

        DrawAddTagPopup(id, details);

        if (tagToRemove != null)
            plugin.Configuration.RemoveTag(id, details, tagToRemove);
    }

    private void DrawAddTagPopup(Guid id, CachedOutfit details)
    {
        using var popup = ImRaii.Popup(AddTagPopupId);
        if (!popup.Success)
            return;

        // Refocus after each add so Enter can chain straight into the next tag without a re-click.
        if (ImGui.IsWindowAppearing() || addTagReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            addTagReclaimFocus = false;
        }

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##addTagSearch", "Type or search a tag...", ref addTagSearchText, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        var trimmed = addTagSearchText.Trim();
        var existingTags = plugin.Configuration.DistinctSortedTags()
            .Where(t => !details.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Where(t => trimmed.Length == 0 || t.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var isNewTag = trimmed.Length > 0
            && !details.Tags.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            && !existingTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase);

        if (submitted)
        {
            if (trimmed.Length > 0)
            {
                plugin.Configuration.AddTag(id, details, trimmed);
                addTagSearchText = string.Empty;
                addTagReclaimFocus = true;
            }
            else
            {
                // A blank Enter is the "I'm done" signal - stop the add-tag loop.
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.Separator();

        if (isNewTag && ImGui.Selectable($"Add new tag \"{trimmed}\""))
        {
            plugin.Configuration.AddTag(id, details, trimmed);
            addTagSearchText = string.Empty;
            addTagReclaimFocus = true;
        }

        if (existingTags.Count == 0)
        {
            if (!isNewTag)
                ImGui.TextDisabled(trimmed.Length > 0 ? "No matching tags." : "All tags are already applied.");
            return;
        }

        if (isNewTag)
            ImGui.Separator();

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var listHeight = Math.Min(existingTags.Count, 8) * rowHeight;
        using var scroll = ImRaii.Child("AddTagList", new Vector2(220 * ImGuiHelpers.GlobalScale, listHeight), false);
        if (!scroll.Success)
            return;

        foreach (var tag in existingTags)
        {
            if (ImGui.Selectable(tag))
            {
                plugin.Configuration.AddTag(id, details, tag);
                addTagSearchText = string.Empty;
                addTagReclaimFocus = true;
            }
        }
    }

    private void DrawCompositeTagsHelpNote()
    {
        const string helpText =
            "Tags can be written as category/type, e.g. swimsuit/bikini or colour/blue. A design tagged this "
            + "way matches filters for the full tag or either half on its own, so it shows up whether you "
            + "filter by swimsuit/bikini, just swimsuit, or just bikini. When designs are grouped by tags "
            + "instead of folders, composite tags also form a nested tree instead of one flat entry — "
            + "swimsuit/bikini shows up as a swimsuit branch containing a bikini branch.";

        var style = ImGui.GetStyle();
        var pad = 8f * ImGuiHelpers.GlobalScale;
        var availW = ImGui.GetContentRegionAvail().X;
        var closeSize = ImGui.GetFrameHeight();
        var wrapW = availW - (pad * 2) - closeSize - style.ItemSpacing.X;
        var textH = ImGui.CalcTextSize(helpText, false, wrapW).Y;
        var boxH = Math.Max(textH, closeSize) + (pad * 2);

        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(availW, boxH),
            ImGui.ColorConvertFloat4ToU32(UiTheme.ToggleOffBg), 4f);

        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapW);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(new Vector2(
            start.X + availW - closeSize - (pad * 0.5f), start.Y + (pad * 0.5f)));
        if (HeaderIconButton("compositeTagsHelpClose", FontAwesomeIcon.Times, UiTheme.PlaceholderText,
                new Vector2(closeSize, closeSize)))
        {
            plugin.Configuration.CompositeTagsHelpDismissed = true;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dismiss (won't be shown again)");

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + boxH));
        ImGui.Spacing();
    }
}
