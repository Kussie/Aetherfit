using Dalamud.Bindings.ImGui;

namespace Aetherfit.Windows;

// Cover Mode's "Group by Folder" - groups by each design's position in the tree's own folder structure
// (FullPath), reusing BuildFolderTree/FolderNode from the Edit Mode tree rather than a parallel type.
public partial class MainWindow
{
    private FolderNode cachedCoverFolderRoot = new();
    private int cachedCoverFolderTreeVersion = -1;

    private void DrawCoverGroupedByFolder()
    {
        if (IsGalleryCacheStale())
            RebuildGalleryCache();

        if (cachedCoverFolderTreeVersion != galleryCacheVersion)
        {
            cachedCoverFolderRoot = BuildFolderTree(cachedVisible, sortAlphabetically: false);
            SortCoverFolderNode(cachedCoverFolderRoot);
            cachedCoverFolderTreeVersion = galleryCacheVersion;
        }

        foreach (var (name, child) in cachedCoverFolderRoot.Folders)
            DrawCoverFolderNode(name, name, child);

        if (cachedCoverFolderRoot.Designs.Count > 0)
        {
            ImGui.Separator();
            if (DrawPersistedSubheader($"Uncategorized ({cachedCoverFolderRoot.Designs.Count})", "folder:##rootFolder"))
            {
                ImGui.Spacing();
                var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
                DrawCoverGridRange(cachedCoverFolderRoot.Designs, 0, cachedCoverFolderRoot.Designs.Count, columns, thumbWidth, thumbHeight);
                ImGui.Spacing();
            }
        }
    }

    private void DrawCoverFolderNode(string name, string path, FolderNode node)
    {
        ImGui.Separator();
        var label = $"{name} ({CountFolderDesigns(node)})";
        if (!DrawPersistedSubheader(label, $"folder:{path}"))
            return;

        ImGui.Indent();
        ImGui.Spacing();

        foreach (var (childName, child) in node.Folders)
            DrawCoverFolderNode(childName, $"{path}/{childName}", child);

        if (node.Designs.Count > 0)
        {
            var (columns, thumbWidth, thumbHeight) = ComputeGridLayout();
            DrawCoverGridRange(node.Designs, 0, node.Designs.Count, columns, thumbWidth, thumbHeight);
            ImGui.Spacing();
        }

        ImGui.Unindent();
    }

    private void SortCoverFolderNode(FolderNode node)
    {
        node.Designs.Sort(CompareGalleryDesigns);
        foreach (var child in node.Folders.Values)
            SortCoverFolderNode(child);
    }

    private static int CountFolderDesigns(FolderNode node)
    {
        var count = node.Designs.Count;
        foreach (var child in node.Folders.Values)
            count += CountFolderDesigns(child);
        return count;
    }
}
