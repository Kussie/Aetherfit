using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aetherfit.Utils;

namespace Aetherfit.Services.Screenshots;

public sealed class ImageStorageService
{
    public const int MaxAdditionalImages = 8;
    private const string AdditionalImagesSubdir = "additional";
    private const string ForeignSubdir = "foreign";
    private const string TempSubdir = "temp";

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

    // In-flight screenshot captures and crops. Wiped wholesale on startup, so a crash mid-flow
    // can't leave files behind among the user's real images.
    public static string TempDirectoryPath =>
        Path.Combine(ImagesDirectoryPath, TempSubdir);

    public string ForeignDirectory(string originKey) =>
        Path.Combine(ForeignRootDirectory, originKey);

    public bool HasCover(Guid id) => configuration.OutfitImages.ContainsKey(id);

    public string? GetCoverPath(Guid id)
        => Resolve(coverPathCache, id, LookupCoverPath);

    private string? LookupCoverPath(Guid id)
    {
        if (!configuration.OutfitImages.TryGetValue(id, out var filename) || string.IsNullOrEmpty(filename))
            return null;
        var path = Path.Combine(ImagesDirectory, filename);
        return File.Exists(path) ? path : null;
    }

    public List<string> GetAdditionalPaths(Guid id)
        => Resolve(additionalPathsCache, id, LookupAdditionalPaths);

    private List<string> LookupAdditionalPaths(Guid id)
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

    // Shared by the two lookups above: hand back the cached value, or compute it once and remember it.
    // Mirrors GameDataService.Resolve, but for a plain Dictionary - these caches are only ever touched
    // from the single UI thread, so no concurrency support is needed.
    private static TValue Resolve<TKey, TValue>(Dictionary<TKey, TValue> cache, TKey key, Func<TKey, TValue> compute) where TKey : notnull
    {
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var value = compute(key);
        cache[key] = value;
        return value;
    }

    public void SetCover(Guid id, string sourcePath)
    {
        try
        {
            var imagesDir = EnsureImagesDirectory();
            DeleteCoverFilesFor(id, imagesDir);

            var ext = NormalizeExtension(Path.GetExtension(sourcePath));
            var targetName = CoverFileName(id, ext);
            var targetPath = Path.Combine(imagesDir, targetName);
            File.Copy(sourcePath, targetPath, overwrite: true);

            configuration.OutfitImages[id] = targetName;
            configuration.Save();
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Failed to set image: {ex.Message}", "Failed to set image for {Id} from {Path}", id, sourcePath);
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

        // Keep a cover as long as any images remain: promote the first additional into the empty slot.
        if (configuration.OutfitAdditionalImages.TryGetValue(id, out var list) && list.Count > 0)
            PromoteToCover(id, 0);
    }

    public void PromoteToCover(Guid id, int index)
    {
        if (!configuration.OutfitAdditionalImages.TryGetValue(id, out var list))
            return;
        if (index < 0 || index >= list.Count)
            return;

        try
        {
            var additionalDir = EnsureAdditionalImagesDirectory();
            var imagesDir = EnsureImagesDirectory();

            var promotedPath = Path.Combine(additionalDir, list[index]);
            if (!File.Exists(promotedPath))
                return;
            var coverExt = NormalizeExtension(Path.GetExtension(list[index]));

            if (configuration.OutfitImages.TryGetValue(id, out var oldCoverName)
                && !string.IsNullOrEmpty(oldCoverName)
                && File.Exists(Path.Combine(imagesDir, oldCoverName)))
            {
                var demotedName = $"{id:N}_{Guid.NewGuid():N}{NormalizeExtension(Path.GetExtension(oldCoverName))}";
                File.Move(Path.Combine(imagesDir, oldCoverName), Path.Combine(additionalDir, demotedName));
                list[index] = demotedName;
            }
            else
            {
                list.RemoveAt(index);
                if (list.Count == 0)
                    configuration.OutfitAdditionalImages.Remove(id);
            }

            DeleteCoverFilesFor(id, imagesDir);
            var coverName = CoverFileName(id, coverExt);
            File.Move(promotedPath, Path.Combine(imagesDir, coverName));
            configuration.OutfitImages[id] = coverName;

            configuration.Save();
        }
        catch (Exception ex)
        {
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Failed to update cover image: {ex.Message}", "Failed to promote additional image {Index} to cover for {Id}", index, id);
        }
        finally
        {
            coverPathCache.Remove(id);
            additionalPathsCache.Remove(id);
        }
    }

    public void DemoteCoverToAdditional(Guid id)
    {
        if (!configuration.OutfitImages.TryGetValue(id, out var coverName) || string.IsNullOrEmpty(coverName))
            return;

        try
        {
            var imagesDir = EnsureImagesDirectory();
            var coverPath = Path.Combine(imagesDir, coverName);
            if (!File.Exists(coverPath))
            {
                configuration.OutfitImages.Remove(id);
                return;
            }

            if (!configuration.OutfitAdditionalImages.TryGetValue(id, out var list))
            {
                list = new List<string>();
                configuration.OutfitAdditionalImages[id] = list;
            }

            if (list.Count >= MaxAdditionalImages)
            {
                DeleteFileQuietly(Path.Combine(AdditionalImagesDirectory, list[0]));
                list.RemoveAt(0);
            }

            var additionalDir = EnsureAdditionalImagesDirectory();
            var demotedName = $"{id:N}_{Guid.NewGuid():N}{NormalizeExtension(Path.GetExtension(coverName))}";
            File.Move(coverPath, Path.Combine(additionalDir, demotedName));
            list.Add(demotedName);

            configuration.OutfitImages.Remove(id);
            configuration.Save();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to demote cover image to additional for {Id}", id);
        }
        finally
        {
            coverPathCache.Remove(id);
            additionalPathsCache.Remove(id);
        }
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
            ServiceErrors.Fail(ex, $"{Plugin.ChatPrefix}Failed to add image: {ex.Message}", "Failed to add additional image for {Id} from {Path}", id, sourcePath);
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
            coverPath = TryWriteContained(dir, $"{sourceId:N}{NormalizeExtension(c.Ext)}", c.Bytes, sourceId, "cover");

        var additionalPaths = new List<string>();
        for (var i = 0; i < additional.Count; i++)
        {
            var (bytes, ext) = additional[i];
            if (bytes.Length == 0) continue;
            if (TryWriteContained(dir, $"{sourceId:N}_{i}{NormalizeExtension(ext)}", bytes, sourceId, "additional image") is { } path)
                additionalPaths.Add(path);
        }

        return (coverPath, additionalPaths);
    }

    // NormalizeExtension already strips dangerous extensions; the containment check here is a second
    // line of defence in case the file name itself ever resolves outside the foreign cache directory.
    private static string? TryWriteContained(string dir, string fileName, byte[] bytes, Guid sourceId, string label)
    {
        try
        {
            var candidate = Path.Combine(dir, fileName);
            if (!IsContainedIn(dir, candidate))
            {
                Plugin.Log.Warning("Skipped foreign {Label} for {Id}: path escaped the cache directory", label, sourceId);
                return null;
            }

            File.WriteAllBytes(candidate, bytes);
            return candidate;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to write foreign {Label} for {Id}", label, sourceId);
            return null;
        }
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

    public void ClearAllTemp()
    {
        try
        {
            if (Directory.Exists(TempDirectoryPath))
                Directory.Delete(TempDirectoryPath, recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to clear temp capture directory");
        }
    }

    public static readonly IReadOnlyCollection<string> AllowedImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    public static string NormalizeExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return ".png";
        ext = ext.ToLowerInvariant();
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        return AllowedImageExtensions.Contains(ext) ? ext : ".png";
    }

    // Belt-and-braces guard against path traversal
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

    // Wipes every design's cover and additional images - reuses CleanupRemovedDesigns' stale-file
    // sweep by treating the entire library as "removed", so the deletion logic only lives in one place.
    public void ClearAllImages()
    {
        CleanupRemovedDesigns(new HashSet<Guid>());
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

    // A unique cover filename per set/promote.
    private static string CoverFileName(Guid id, string ext) => $"{id:N}_{Guid.NewGuid():N}{ext}";

    private static void DeleteCoverFilesFor(Guid id, string imagesDir)
    {
        if (!Directory.Exists(imagesDir))
            return;
        // Matches both the old "{id:N}.ext" covers and the newer "{id:N}_{token}.ext" ones. Only cover
        // files live at the top level of the images dir, so a prefix glob won't catch anything else.
        var prefix = id.ToString("N");
        foreach (var file in Directory.EnumerateFiles(imagesDir, prefix + "*"))
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
