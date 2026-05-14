using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Aetherfit.Services;

public sealed class ImageStorageService
{
    public const int MaxAdditionalImages = 5;
    private const string AdditionalImagesSubdir = "additional";

    private readonly Configuration configuration;

    public ImageStorageService(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public string ImagesDirectory =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images");

    public string AdditionalImagesDirectory =>
        Path.Combine(ImagesDirectory, AdditionalImagesSubdir);

    public bool HasCover(Guid id) => configuration.OutfitImages.ContainsKey(id);

    public string? GetCoverPath(Guid id)
    {
        if (!configuration.OutfitImages.TryGetValue(id, out var filename) || string.IsNullOrEmpty(filename))
            return null;
        var path = Path.Combine(ImagesDirectory, filename);
        return File.Exists(path) ? path : null;
    }

    public List<string> GetAdditionalPaths(Guid id)
    {
        var result = new List<string>();
        if (!configuration.OutfitAdditionalImages.TryGetValue(id, out var filenames) || filenames.Count == 0)
            return result;

        var dir = AdditionalImagesDirectory;
        foreach (var name in filenames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                result.Add(path);
        }
        return result;
    }

    public void SetCover(Guid id, string sourcePath)
    {
        try
        {
            var imagesDir = EnsureImagesDirectory();
            DeleteCoverFilesFor(id, imagesDir);

            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = ".png";
            var targetName = id.ToString("N") + ext;
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            configuration.OutfitImages[id] = targetName;
            configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Failed to set image: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to set image for {Id} from {Path}", id, sourcePath);
        }
    }

    public void RemoveCover(Guid id)
    {
        try
        {
            DeleteCoverFilesFor(id, ImagesDirectory);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete image file for {Id}", id);
        }

        if (configuration.OutfitImages.Remove(id))
            configuration.Save();
    }

    public void AddAdditional(Guid id, string sourcePath)
    {
        try
        {
            if (!configuration.OutfitAdditionalImages.TryGetValue(id, out var list))
            {
                list = new List<string>();
                configuration.OutfitAdditionalImages[id] = list;
            }

            if (list.Count >= MaxAdditionalImages)
                return;

            var imagesDir = EnsureAdditionalImagesDirectory();
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = ".png";

            var targetName = $"{id:N}_{Guid.NewGuid():N}{ext}";
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            list.Add(targetName);
            configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Failed to add image: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to add additional image for {Id} from {Path}", id, sourcePath);
        }
    }

    public void RemoveAdditional(Guid id, int index)
    {
        if (!configuration.OutfitAdditionalImages.TryGetValue(id, out var list))
            return;
        if (index < 0 || index >= list.Count)
            return;

        var filename = list[index];
        list.RemoveAt(index);
        if (list.Count == 0)
            configuration.OutfitAdditionalImages.Remove(id);

        try
        {
            var path = Path.Combine(AdditionalImagesDirectory, filename);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete additional image {File}", filename);
        }

        configuration.Save();
    }

    public void CleanupRemovedDesigns(IReadOnlySet<Guid> validIds)
    {
        var coverDir = ImagesDirectory;
        var additionalDir = AdditionalImagesDirectory;

        var staleCoverIds = configuration.OutfitImages.Keys
            .Where(id => !validIds.Contains(id))
            .ToList();
        foreach (var id in staleCoverIds)
        {
            DeleteCoverFilesFor(id, coverDir);
            configuration.OutfitImages.Remove(id);
        }

        var staleAdditionalIds = configuration.OutfitAdditionalImages.Keys
            .Where(id => !validIds.Contains(id))
            .ToList();
        foreach (var id in staleAdditionalIds)
        {
            if (configuration.OutfitAdditionalImages.TryGetValue(id, out var filenames))
            {
                foreach (var name in filenames)
                {
                    try
                    {
                        var path = Path.Combine(additionalDir, name);
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning(ex, "Failed to delete additional image {File}", name);
                    }
                }
            }
            configuration.OutfitAdditionalImages.Remove(id);
        }

        SweepOrphanFiles(coverDir, validIds);
        SweepOrphanFiles(additionalDir, validIds);
    }

    private string EnsureImagesDirectory()
    {
        var dir = ImagesDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string EnsureAdditionalImagesDirectory()
    {
        var dir = AdditionalImagesDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteCoverFilesFor(Guid id, string imagesDir)
    {
        if (!Directory.Exists(imagesDir))
            return;
        var prefix = id.ToString("N");
        foreach (var file in Directory.EnumerateFiles(imagesDir, prefix + ".*"))
        {
            try { File.Delete(file); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to delete {File}", file); }
        }
    }

    // Filenames in our image dirs encode the design Guid as the prefix before any underscore
    // (cover: "{guid:N}.ext"; additional: "{guid:N}_{anotherGuid:N}.ext"). Anything whose prefix
    // parses as a Guid but isn't in validIds is a leftover and gets removed.
    private static void SweepOrphanFiles(string directory, IReadOnlySet<Guid> validIds)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var underscore = name.IndexOf('_');
            var prefix = underscore >= 0 ? name[..underscore] : name;
            if (!Guid.TryParseExact(prefix, "N", out var fileId))
                continue;
            if (validIds.Contains(fileId))
                continue;
            try { File.Delete(path); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Failed to delete orphan file {File}", path); }
        }
    }
}
