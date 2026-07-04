using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private string? tagWriteError;
    private Guid tagWriteErrorDesign;

    private void DrawTagSuggestionsBlock(Guid id, CachedOutfit details)
    {
        var cover = plugin.ImageStorage.GetCoverPath(id);
        var additional = plugin.ImageStorage.GetAdditionalPaths(id);
        if (cover == null && additional.Count == 0)
            return;

        var run = plugin.TagSuggestions.GetRun(id);
        if (run == null)
        {
            DrawSuggestButton(id, details, cover, additional);
            return;
        }

        switch (run.State)
        {
            case TagSuggestionService.RunState.Running:
                ImGui.TextDisabled($"Analyzing image {run.ImageIndex}/{run.ImageCount}...");
                break;

            case TagSuggestionService.RunState.Failed:
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(UiTheme.ErrorText, run.Error ?? "Tag analysis failed.");
                ImGui.PopTextWrapPos();
                if (ImGui.SmallButton("Retry##suggest"))
                    StartTagSuggestion(id, details, cover, additional);
                ImGui.SameLine();
                if (ImGui.SmallButton("Dismiss##suggest"))
                    plugin.TagSuggestions.Dismiss(id);
                break;

            case TagSuggestionService.RunState.Done:
                DrawSuggestionResults(id, details, run, cover, additional);
                break;
        }
    }

    private void DrawSuggestButton(Guid id, CachedOutfit details, string? cover, IReadOnlyList<string> additional)
    {
        var store = plugin.TagModel;
        switch (store.CurrentState)
        {
            case TagModelStore.State.Downloading:
                ImGui.ProgressBar(store.Progress, new Vector2(-1, 0), store.CurrentFile ?? "Downloading model...");
                break;

            case TagModelStore.State.Ready:
                if (ImGui.SmallButton("Suggest tags##suggest"))
                    StartTagSuggestion(id, details, cover, additional);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Analyzes this design's screenshots locally and suggests tags.");
                break;

            default:
                if (ImGui.SmallButton("Suggest tags##suggest"))
                    plugin.ToggleConfigUi();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Requires a one-time model download — opens Settings.");
                break;
        }
    }

    private void StartTagSuggestion(Guid id, CachedOutfit details, string? cover, IReadOnlyList<string> additional)
    {
        var paths = new List<string>();
        if (cover != null)
            paths.Add(cover);
        paths.AddRange(additional);
        plugin.TagSuggestions.StartSuggestion(id, paths, details.Tags);

        if (tagWriteErrorDesign == id)
            tagWriteError = null;
    }

    private void DrawSuggestionResults(Guid id, CachedOutfit details, TagSuggestionService.DesignRun run,
        string? cover, IReadOnlyList<string> additional)
    {
        if (run.Results.Count == 0)
        {
            ImGui.TextDisabled("No tags cleared the confidence threshold.");
        }
        else
        {
            ImGui.TextDisabled("Suggested tags — click to select:");

            var style = ImGui.GetStyle();
            var spacing = style.ItemSpacing.X;
            var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
            var cursorStart = ImGui.GetCursorScreenPos().X;
            var lineRight = cursorStart;
            var first = true;

            string? toBlacklist = null;
            foreach (var suggestion in run.Results)
            {
                var width = ImGui.CalcTextSize(suggestion.Tag).X + style.FramePadding.X * 2;
                Pills.PlaceItem(width, ref first, ref lineRight, cursorStart, spacing, availRight);

                var selected = run.Selected.Contains(suggestion.Tag);
                if (Pills.DrawToggle(suggestion.Tag, suggestion.Tag, selected))
                {
                    if (selected)
                        run.Selected.Remove(suggestion.Tag);
                    else
                        run.Selected.Add(suggestion.Tag);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{suggestion.Score:P0} confidence — click to {(selected ? "deselect" : "select")}\n"
                                   + "Shift+right-click to never suggest this tag");
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                        toBlacklist = suggestion.Tag;
                }
            }

            if (toBlacklist != null)
                BlacklistSuggestedTag(toBlacklist, run);
        }

        if (run.SkippedImages > 0)
            ImGui.TextDisabled($"{run.SkippedImages} image(s) could not be read and were skipped.");

        if (tagWriteError != null && tagWriteErrorDesign == id)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(UiTheme.ErrorText, tagWriteError);
            ImGui.PopTextWrapPos();
        }

        if (run.Results.Count > 0)
        {
            using (ImRaii.Disabled(run.Selected.Count == 0))
            {
                if (ImGui.SmallButton($"Add {run.Selected.Count} selected to design##suggest"))
                    WriteSelectedTags(id, run);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(run.Selected.Count == 0
                    ? "Select one or more suggested tags first."
                    : "Writes the selected tags into this design's file in Glamourer's config.\nGlamourer picks them up after it reloads (e.g. a game restart).");

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy all##suggest"))
                ImGui.SetClipboardText(string.Join(", ", run.Results.Select(r => r.Tag)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copy all suggested tags to the clipboard.");

            ImGui.SameLine();
        }

        if (ImGui.SmallButton("Re-run##suggest"))
            StartTagSuggestion(id, details, cover, additional);
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss##suggest"))
            plugin.TagSuggestions.Dismiss(id);
    }

    private void BlacklistSuggestedTag(string tag, TagSuggestionService.DesignRun run)
    {
        var blacklist = plugin.Configuration.TagSuggestionBlacklist;
        if (!blacklist.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            blacklist.Add(tag);
            plugin.Configuration.Save();
        }

        run.Results.RemoveAll(s => string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase));
        run.Selected.Remove(tag);
    }

    private void WriteSelectedTags(Guid id, TagSuggestionService.DesignRun run)
    {
        var selected = run.Selected.ToList();
        var error = plugin.GlamourerDesignFile.WriteTags(id, selected);
        if (error != null)
        {
            tagWriteError = error;
            tagWriteErrorDesign = id;
            return;
        }

        tagWriteError = null;
        run.Results.RemoveAll(s => run.Selected.Contains(s.Tag));
        run.Selected.Clear();
    }
}
