using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Aetherfit.Services;
using Newtonsoft.Json;

namespace Aetherfit.Sharing;

// Builds shareable ".afgallery" bundles from the local gallery and imports them back as read-only foreign galleries.
// A bundle is a zip archive: a "gallery.json" manifest plus the raw image bytes as separate entries. Storing images
// raw (already-compressed png/jpg/webp) avoids the ~33% base64 inflation that bloated large galleries.
// For backward compatibility, import also reads the older formats: gzip-wrapped JSON and plain JSON with inline base64.
public sealed class GallerySharingService
{
    public const string FileExtension = ".afgallery";
    private const string ManifestEntryName = "gallery.json";

    // Shared galleries are a showcase, not an archive: downscale + re-encode images to keep bundles small.
    private const int MaxImageDimension = 1600;
    private const long JpegQuality = 85;

    private readonly Configuration configuration;
    private readonly ImageStorageService imageStorage;

    public GallerySharingService(Configuration configuration, ImageStorageService imageStorage)
    {
        this.configuration = configuration;
        this.imageStorage = imageStorage;
    }

    public bool ExportToFile(string sharerLabel, string path)
    {
        try
        {
            if (!path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
                path += FileExtension;

            var snapshot = new SharedGallery
            {
                SharerLabel = string.IsNullOrWhiteSpace(sharerLabel) ? "Shared Gallery" : sharerLabel,
                SharerContentId = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0,
                CreatedAt = DateTimeOffset.Now,
            };

            // Designs reference their images by zip entry name; the (downscaled) bytes are written as entries below.
            var imageEntries = new List<(string Entry, byte[] Bytes)>();
            foreach (var (id, outfit) in configuration.CachedOutfits)
            {
                var design = new SharedDesign
                {
                    SourceId = id,
                    Name = outfit.Name,
                    Description = outfit.Description,
                    Tags = new List<string>(outfit.Tags),
                    Jobs = new List<uint>(configuration.GetJobAssociations(id)),
                };

                var cover = EncodeForBundle(imageStorage.GetCoverPath(id));
                if (cover is { } c)
                {
                    var entry = $"images/{id:N}{c.Ext}";
                    imageEntries.Add((entry, c.Bytes));
                    design.Cover = new SharedImage { Ext = c.Ext, Entry = entry };
                }

                var additionalPaths = imageStorage.GetAdditionalPaths(id);
                for (var i = 0; i < additionalPaths.Count; i++)
                {
                    var encoded = EncodeForBundle(additionalPaths[i]);
                    if (encoded is not { } a) continue;
                    var entry = $"images/{id:N}_{i}{a.Ext}";
                    imageEntries.Add((entry, a.Bytes));
                    design.AdditionalImages.Add(new SharedImage { Ext = a.Ext, Entry = entry });
                }

                snapshot.Designs.Add(design);
            }

            WriteBundle(path, snapshot, imageEntries);
            Plugin.ChatGui.Print($"[Aetherfit] Exported {snapshot.Designs.Count} design(s) to {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Failed to export gallery: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to export gallery to {Path}", path);
            return false;
        }
    }

    public ForeignGallery? ImportFromFile(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);

            // Zip bundle ("PK"): read the manifest and resolve images from the archive entries.
            if (IsZip(raw))
            {
                using var ms = new MemoryStream(raw);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
                var manifest = archive.GetEntry(ManifestEntryName)
                               ?? throw new InvalidDataException("Bundle is missing its manifest.");
                string json;
                using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8))
                    json = reader.ReadToEnd();

                var snapshot = Deserialize(json);
                WarnIfNewer(snapshot);
                return LoadSnapshot(snapshot, archive);
            }

            // Legacy bundle: gzip-wrapped or plain JSON with inline base64 images.
            var legacy = Deserialize(DecodeText(raw));
            WarnIfNewer(legacy);
            return LoadSnapshot(legacy, archive: null);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Failed to import gallery: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to import gallery from {Path}", path);
            return null;
        }
    }

    private ForeignGallery LoadSnapshot(SharedGallery snapshot, ZipArchive? archive)
    {
        var originKey = Guid.NewGuid().ToString("N");
        var gallery = new ForeignGallery
        {
            OriginKey = originKey,
            SharerLabel = snapshot.SharerLabel,
            SharerContentId = snapshot.SharerContentId,
            CreatedAt = snapshot.CreatedAt,
        };

        foreach (var design in snapshot.Designs)
        {
            var cover = DecodeImage(design.Cover, archive);
            var additional = (design.AdditionalImages ?? new List<SharedImage>())
                .Select(img => DecodeImage(img, archive))
                .Where(d => d != null)
                .Select(d => d!.Value)
                .ToList();

            var (coverPath, additionalPaths) =
                imageStorage.WriteForeignImages(originKey, design.SourceId, cover, additional);

            gallery.Designs.Add(new ForeignDesign
            {
                SourceId = design.SourceId,
                Name = design.Name,
                Description = design.Description,
                Tags = design.Tags ?? new List<string>(),
                Jobs = design.Jobs ?? new List<uint>(),
                CoverPath = coverPath,
                AdditionalPaths = additionalPaths,
            });
        }

        return gallery;
    }

    private static SharedGallery Deserialize(string json) =>
        JsonConvert.DeserializeObject<SharedGallery>(json)
        ?? throw new InvalidDataException("The file did not contain a valid gallery.");

    private static void WarnIfNewer(SharedGallery snapshot)
    {
        if (snapshot.FormatVersion > SharedGallery.CurrentFormatVersion)
            Plugin.ChatGui.Print("[Aetherfit] This gallery was made with a newer version of Aetherfit; some details may not show correctly.");
    }

    // Resolves an image's bytes either from a zip entry (newer bundles) or from inline base64 (legacy bundles).
    private static (byte[] Bytes, string Ext)? DecodeImage(SharedImage? image, ZipArchive? archive)
    {
        if (image == null)
            return null;
        try
        {
            if (!string.IsNullOrEmpty(image.Entry) && archive != null)
            {
                var entry = archive.GetEntry(image.Entry);
                if (entry == null)
                    return null;
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                return (ms.ToArray(), image.Ext);
            }

            if (!string.IsNullOrEmpty(image.Data))
                return (Convert.FromBase64String(image.Data), image.Ext);

            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to decode shared image");
            return null;
        }
    }

    // Produces a lightweight preview copy of an image for the bundle, falling back to the original bytes if the
    // image can't be re-encoded (e.g. an unexpected format). Returns null when there is no usable image.
    private static (byte[] Bytes, string Ext)? EncodeForBundle(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            return (ScreenshotCaptureService.EncodePreviewJpeg(path, MaxImageDimension, JpegQuality), ".jpg");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to re-encode {Path} for bundle; storing the original.", path);
            try { return (File.ReadAllBytes(path), Path.GetExtension(path)); }
            catch { return null; }
        }
    }

    private static void WriteBundle(string path, SharedGallery snapshot, List<(string Entry, byte[] Bytes)> images)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        using (var writer = new StreamWriter(manifest.Open(), Encoding.UTF8))
            writer.Write(JsonConvert.SerializeObject(snapshot, Formatting.None));

        foreach (var (entryName, bytes) in images)
        {
            // Bytes are already compressed (jpeg); store without re-deflating to save time for ~no size gain.
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }

    private static bool IsZip(byte[] raw) =>
        raw.Length >= 2 && raw[0] == 0x50 && raw[1] == 0x4B; // "PK"

    // Reads legacy bundle bytes as UTF-8 JSON, transparently gunzipping if the gzip magic bytes are present.
    private static string DecodeText(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B)
        {
            using var input = new MemoryStream(raw);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return Encoding.UTF8.GetString(raw);
    }
}
