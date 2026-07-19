using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

// Arrow-key navigation (opt-in via Configuration.ArrowKeyNavigation). The tree records its drawn
// leaves each frame; the key handler runs at the end of Draw on that frame's complete list, and the
// moved-to item scrolls into view when it draws on the following frame.
public partial class MainWindow
{
    private readonly List<Guid> navTreeLeaves = new();
    private readonly List<uint> navFolderIdStack = new();

    // Ancestor folder ids of the selected leaf, captured whenever it draws. Kept after the leaf is
    // hidden by a collapse so Left/Right can keep walking up and back down the chain.
    private uint[] navSelectedLeafAncestors = Array.Empty<uint>();

    // The tree scroll child's state storage (tree-node open flags live there, not in the main
    // window's storage). Captured while the child draws, only used later the same frame.
    private ImGuiStoragePtr navTreeStorage;

    private bool navPendingScroll;
    private int galleryColumns = 1;

    private void BeginNavFrame()
    {
        navTreeLeaves.Clear();
        navFolderIdStack.Clear();
    }

    private void PushNavFolder(uint id) => navFolderIdStack.Add(id);

    private void PopNavFolder() => navFolderIdStack.RemoveAt(navFolderIdStack.Count - 1);

    private void RecordNavLeaf(DesignLeaf design)
    {
        navTreeLeaves.Add(design.Id);
        if (selectedDesign == design.Id)
            navSelectedLeafAncestors = navFolderIdStack.ToArray();
    }

    private void HandleArrowKeyNavigation()
    {
        if (!plugin.Configuration.ArrowKeyNavigation)
            return;
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows))
            return;
        if (ImGui.GetIO().WantTextInput)
            return;

        if (coverMode)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true))
                MoveGallerySelection(-1);
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true))
                MoveGallerySelection(+1);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true))
                MoveGallerySelection(-galleryColumns);
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true))
                MoveGallerySelection(+galleryColumns);
        }
        else
        {
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true))
                MoveTreeSelection(-1);
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true))
                MoveTreeSelection(+1);
            if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true))
                CollapseSelectedAncestor();
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true))
                ExpandSelectedAncestor();
        }

        if ((ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            && selectedDesign is { } applyId)
            ApplyDesignById(applyId);
    }

    private void MoveTreeSelection(int delta)
    {
        if (navTreeLeaves.Count == 0)
            return;

        // In job/tag groupings the same design can be listed several times; we move from its first occurrence.
        var idx = selectedDesign is { } id ? navTreeLeaves.IndexOf(id) : -1;
        var next = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, navTreeLeaves.Count - 1);
        if (navTreeLeaves[next] == selectedDesign)
            return;

        selectedDesign = navTreeLeaves[next];
        navPendingScroll = true;
    }

    private void MoveGallerySelection(int delta)
    {
        if (cachedVisible.Count == 0)
            return;

        var idx = selectedDesign is { } id ? cachedVisible.FindIndex(d => d.Id == id) : -1;
        var next = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, cachedVisible.Count - 1);
        if (cachedVisible[next].Id == selectedDesign)
            return;

        selectedDesign = cachedVisible[next].Id;
        navPendingScroll = true;
    }

    // Left: close the deepest still-open ancestor of the selected design (repeat walks up the chain).
    private void CollapseSelectedAncestor()
    {
        for (var i = navSelectedLeafAncestors.Length - 1; i >= 0; i--)
        {
            var id = navSelectedLeafAncestors[i];
            if (navTreeStorage.GetInt(id, 0) != 0)
            {
                navTreeStorage.SetInt(id, 0);
                return;
            }
        }
    }

    // Right: reopen the shallowest closed ancestor (repeat walks back down until the leaf is visible).
    private void ExpandSelectedAncestor()
    {
        foreach (var id in navSelectedLeafAncestors)
        {
            if (navTreeStorage.GetInt(id, 0) == 0)
            {
                navTreeStorage.SetInt(id, 1);
                navPendingScroll = true;
                return;
            }
        }
    }
}
