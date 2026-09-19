using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public sealed class TagManagerWindow : Window, IDisposable
{
    private const string RenameTagPopupId = "Rename tag?##renameTagPopup";
    private const string MergeTagPopupId = "Merge tag into...##mergeTagPopup";
    private const string ConfirmMergePopupId = "Merge tags?##confirmMergeTag";
    private const int MaxVisibleRows = 10;

    private readonly Plugin plugin;

    public TagManagerWindow(Plugin plugin)
        : base("Tag Manager##AetherfitTagManager")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 300),
            MaximumSize = new Vector2(700, 900),
        };
        Size = new Vector2(420, 460);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    private List<(string Tag, int Count)> cachedTags = new();
    private int cachedTagVersion = -1;
    private string searchText = string.Empty;

    // The tag being renamed/merged, shared by both popups below since only one can be open at a time.
    private string? activeTag;
    private string renameInput = string.Empty;
    private bool renameReclaimFocus;
    private string mergeFilterText = string.Empty;
    private string? mergeTarget;

    // Set from inside a per-row PushId scope, consumed from the popup's own top-level draw call - calling
    // ImGui.OpenPopup directly from the row would compute the wrong ID and silently never open.
    private bool renamePopupRequested;
    private bool mergePopupRequested;
    private bool confirmMergePopupRequested;

    private void RefreshIfStale()
    {
        if (cachedTagVersion == plugin.Configuration.TagVersion)
            return;
        cachedTags = plugin.Configuration.TagUsageCounts();
        cachedTagVersion = plugin.Configuration.TagVersion;
    }

    public override void Draw()
    {
        RefreshIfStale();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##tagManagerSearch", "Search tags...", ref searchText, 64);
        ImGui.Spacing();

        var matches = cachedTags
            .Where(t => searchText.Length == 0 || t.Tag.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        using (ImRaii.Child("##tagManagerList", new Vector2(-1, -1), false))
        {
            if (matches.Count == 0)
                ImGui.TextDisabled(cachedTags.Count == 0 ? "No tags yet." : "No tags match your search.");
            else
                foreach (var (tag, count) in matches)
                    DrawTagRow(tag, count);
        }

        DrawRenameTagPopup();
        DrawMergeTagPopup();
        DrawConfirmMergePopup();
    }

    private void DrawTagRow(string tag, int count)
    {
        using var rowId = ImRaii.PushId(tag);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(tag);
        ImGui.SameLine();
        ImGui.TextDisabled($"({count})");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("renameTag", FontAwesomeIcon.Pen))
        {
            activeTag = tag;
            renameInput = tag;
            renameReclaimFocus = true;
            renamePopupRequested = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rename this tag everywhere");

        ImGui.SameLine();
        if (ImGui.Button("Merge Into..."))
        {
            activeTag = tag;
            mergeFilterText = string.Empty;
            mergeTarget = null;
            mergePopupRequested = true;
        }
    }

    private void DrawRenameTagPopup()
    {
        if (renamePopupRequested)
        {
            renamePopupRequested = false;
            ImGui.OpenPopup(RenameTagPopupId);
        }

        ImGui.SetNextWindowSize(new Vector2(360, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(RenameTagPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success || activeTag is not { } tag)
            return;

        ImGui.TextWrapped($"Rename \"{tag}\" everywhere it's used.");
        ImGui.Spacing();

        ImGui.TextUnformatted("New name");
        if (ImGui.IsWindowAppearing() || renameReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            renameReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##renameTagInput", "New tag name", ref renameInput, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = renameInput.Trim();
        var canConfirm = trimmed.Length > 0 && !string.Equals(trimmed, tag, StringComparison.Ordinal);

        ImGui.Spacing();
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Rename") || (submitted && canConfirm))
            {
                plugin.Configuration.RenameTag(tag, trimmed);
                activeTag = null;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            activeTag = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawMergeTagPopup()
    {
        if (mergePopupRequested)
        {
            mergePopupRequested = false;
            ImGui.OpenPopup(MergeTagPopupId);
        }

        ImGui.SetNextWindowSize(new Vector2(320, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(MergeTagPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success || activeTag is not { } tag)
            return;

        ImGui.TextWrapped($"Merge \"{tag}\" into which tag?");
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##mergeTagFilter", "Filter tags...", ref mergeFilterText, 64);
        ImGui.Separator();

        var candidates = cachedTags
            .Where(t => !string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)
                && (mergeFilterText.Length == 0 || t.Tag.Contains(mergeFilterText, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var listHeight = Math.Min(Math.Max(candidates.Count, 1), MaxVisibleRows) * ImGui.GetTextLineHeightWithSpacing();
        using (ImRaii.Child("##mergeTagList", new Vector2(-1, listHeight), false))
        {
            if (candidates.Count == 0)
                ImGui.TextDisabled("No other tags.");
            foreach (var (candidateTag, count) in candidates)
            {
                if (ImGui.Selectable($"{candidateTag} ({count})##merge{candidateTag}"))
                {
                    mergeTarget = candidateTag;
                    ImGui.CloseCurrentPopup();
                    confirmMergePopupRequested = true;
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Cancel"))
        {
            activeTag = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawConfirmMergePopup()
    {
        if (confirmMergePopupRequested)
        {
            confirmMergePopupRequested = false;
            ConfirmDialog.Open(ConfirmMergePopupId);
        }

        if (activeTag is not { } tag || mergeTarget is not { } target)
            return;

        var count = cachedTags.FirstOrDefault(t => string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)).Count;
        var message = $"This will merge \"{tag}\" into \"{target}\" across {count} design(s). "
            + $"\"{tag}\" will no longer exist as a separate tag.";

        if (ConfirmDialog.Draw(ConfirmMergePopupId, message, "Merge"))
        {
            plugin.Configuration.RenameTag(tag, target);
            activeTag = null;
            mergeTarget = null;
        }
    }
}
