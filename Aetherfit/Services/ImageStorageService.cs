using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Aetherfit.Services;

public sealed class ImageStorageService
{
    public const int MaxAdditionalImages = 5;
    private const string AdditionalImagesSubdir = "additional";
    private const string ForeignSubdir = "foreign";

    private readonly Configuration configuration;
    private readonly Dictionary<Guid, string?> coverPathCache = [];
    private readonly Dictionary<Guid, List<string>> additionalPathsCache = [];

    public ImageStorageService(Configuration configuration)
    {
        this.configuration = configuration;
    }

    // The top-level "images" folder. Static so other services (e.g. screenshots) can point at the same place.
    public static string ImagesDirectoryPath =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images");

    public string ImagesDirectory => ImagesDirectoryPath;

    public string AdditionalImagesDirectory =>
        Path.Combine(ImagesDirectory, AdditionalImagesSubdir);

    // Where imported galleries live. Each gets its own subfolder (by origin key), well away from the user's own
    // images so we can wipe it without touching anything local.
    public string ForeignRootDirectory =>
        Path.Combine(ImagesDirectory, ForeignSubdir);

    public string ForeignDirectory(string originKey) =>
        Path.Combine(ForeignRootDirectory, originKey);

    public bool HasCover(Guid id) => configuration.OutfitImages.ContainsKey(id);

    public string? GetCoverPath(Guid id)
    {
        if (coverPathCache.TryGetValue(id, out var cached))
            return cached;

        if (!configuration.OutfitImages.TryGetValue(id, out var filename) || string.IsNullOrEmpty(filename))
        {
            coverPathCache[id] = null;
            return null;
        }
        var path = Path.Combine(ImagesDirectory, filename);
        var result = File.Exists(path) ? path : null;
        coverPathCache[id] = result;
        return result;
    }

    public List<string> GetAdditionalPaths(Guid id)
    {
        if (additionalPathsCache.TryGetValue(id, out var cached))
            return cached;

        var result = new List<string>();
        if (!configuration.OutfitAdditionalImages.TryGetValue(id, out var filenames) || filenames.Count == 0)
        {
            additionalPathsCache[id] = result;
            return result;
        }

        var dir = AdditionalImagesDirectory;
        foreach (var name in filenames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                result.Add(path);
        }
        additionalPathsCache[id] = result;
        return result;
    }

    public void SetCover(Guid id, string sourcePath)
    {
        try
        {
            var imagesDir = EnsureImagesDirectory();
            DeleteCoverFilesFor(id, imagesDir);

            var ext = NormalizeExtension(Path.GetExtension(sourcePath));
            var targetName = id.ToString("N") + ext;
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            configuration.OutfitImages[id] = targetName;
            configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to set image: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to set image for {Id} from {Path}", id, sourcePath);
        }
        finally
        {
            coverPathCache.Remove(id);
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

        coverPathCache.Remove(id);
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
            var ext = NormalizeExtension(Path.GetExtension(sourcePath));

            var targetName = $"{id:N}_{Guid.NewGuid():N}{ext}";
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            list.Add(targetName);
            configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to add image: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to add additional image for {Id} from {Path}", id, sourcePath);
        }
        finally
        {
            additionalPathsCache.Remove(id);
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

        DeleteFileQuietly(Path.Combine(AdditionalImagesDirectory, filename));

        configuration.Save();
        additionalPathsCache.Remove(id);
    }

    // Dumps one imported design's images into the foreign cache and hands back their paths, ready for
    // TextureProvider.GetFromFile. We keep the original extension so the bytes still decode.
    public (string? Cover, List<string> Additional) WriteForeignImages(
        string originKey, Guid sourceId,
        (byte[] Bytes, string Ext)? cover,
        IReadOnlyList<(byte[] Bytes, string Ext)> additional)
    {
        var dir = ForeignDirectory(originKey);
        Directory.CreateDirectory(dir);

        string? coverPath = null;
        if (cover is { } c && c.Bytes.Length > 0)
        {
            try
            {
                var candidate = Path.Combine(dir, $"{sourceId:N}{NormalizeExtension(c.Ext)}");
                // NormalizeExtension already strips dangerous extensions; this is a second line of defence
                // in case the file name itself ever resolves outside the foreign cache directory.
                if (IsContainedIn(dir, candidate))
                {
                    File.WriteAllBytes(candidate, c.Bytes);
                    coverPath = candidate;
                }
                else
                {
                    Plugin.Log.Warning("Skipped foreign cover for {Id}: path escaped the cache directory", sourceId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to write foreign cover for {Id}", sourceId);
                coverPath = null;
            }
        }

        var additionalPaths = new List<string>();
        for (var i = 0; i < additional.Count; i++)
        {
            var (bytes, ext) = additional[i];
            if (bytes.Length == 0) continue;
            try
            {
                var path = Path.Combine(dir, $"{sourceId:N}_{i}{NormalizeExtension(ext)}");
                if (!IsContainedIn(dir, path))
                {
                    Plugin.Log.Warning("Skipped foreign image for {Id}: path escaped the cache directory", sourceId);
                    continue;
                }
                File.WriteAllBytes(path, bytes);
                additionalPaths.Add(path);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to write foreign additional image for {Id}", sourceId);
            }
        }

        return (coverPath, additionalPaths);
    }

    public void ClearForeign(string originKey)
    {
        if (string.IsNullOrEmpty(originKey))
            return;
        try
        {
            var dir = ForeignDirectory(originKey);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to clear foreign gallery cache {Origin}", originKey);
        }
    }

    public void ClearAllForeign()
    {
        try
        {
            if (Directory.Exists(ForeignRootDirectory))
                Directory.Delete(ForeignRootDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to clear foreign gallery cache root");
        }
    }

    // Image extensions we're willing to write. Anything outside this set (including separators or
    // traversal sequences smuggled in via a shared bundle's Ext field) is forced back to ".png".
    private static readonly HashSet<string> AllowedImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    // Lower-cases an extension and makes sure it has a leading dot. Untrusted or unrecognised extensions
    // (e.g. ".png/../../evil.exe" from an imported gallery) collapse to ".png" so they can't escape the
    // target directory or write an executable type.
    public static string NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return ".png";
        ext = ext.ToLowerInvariant();
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        return AllowedImageExtensions.Contains(ext) ? ext : ".png";
    }

    // Belt-and-braces guard against path traversal: confirm a built path actually resolves inside the
    // directory we intended to write to. Returns false for anything that escapes via "..", separators, etc.
    private static bool IsContainedIn(string directory, string path)
    {
        var root = Path.GetFullPath(directory);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    // Delete a file if it's there, and shrug off (just log) any failure. Saves repeating this try/catch everywhere.
    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to delete {File}", path);
        }
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
                    DeleteFileQuietly(Path.Combine(additionalDir, name));
            }
            configuration.OutfitAdditionalImages.Remove(id);
        }

        SweepOrphanFiles(coverDir, validIds);
        SweepOrphanFiles(additionalDir, validIds);

        coverPathCache.Clear();
        additionalPathsCache.Clear();
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
            DeleteFileQuietly(file);
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
            DeleteFileQuietly(path);
        }
    }
}
