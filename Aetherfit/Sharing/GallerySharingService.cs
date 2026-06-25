using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Aetherfit.Services;
using Newtonsoft.Json;

namespace Aetherfit.Sharing;

// Writes and reads ".afgallery" bundles. A bundle is just a zip: a "gallery.json" manifest plus the image bytes as
// separate entries. We keep the images raw (they're already png/jpg, base64 would have padded them ~33% bigger).
// Import still understands the two earlier formats too — gzip'd JSON, and plain JSON with the images inline as base64.
public sealed class GallerySharingService
{
    public const string FileExtension = ".afgallery";
    private const string ManifestEntryName = "gallery.json";

    // A shared gallery is a showcase, not a backup, so shrink the images down before they go in.
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
                CreatedAt = DateTimeOffset.Now,
            };

            // Each design just names its image entries; the shrunk bytes get written into the zip further down.
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
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Exported {snapshot.Designs.Count} design(s) to {Path.GetFileName(path)}.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to export gallery: {ex.Message}");
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
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to import gallery: {ex.Message}");
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
            CreatedAt = snapshot.CreatedAt,
        };

        foreach (var design in snapshot.Designs)
        {
            var cover = DecodeImage(design.Cover, archive);

            var additional = new List<(byte[] Bytes, string Ext)>();
            foreach (var img in design.AdditionalImages ?? new())
                if (DecodeImage(img, archive) is { } bytes)
                    additional.Add(bytes);

            var (coverPath, additionalPaths) =
                imageStorage.WriteForeignImages(originKey, design.SourceId, cover, additional);

            gallery.Designs.Add(new ForeignDesign
            {
                SourceId = design.SourceId,
                Name = design.Name,
                Description = design.Description,
                Tags = design.Tags ?? new(),
                Jobs = design.Jobs ?? new(),
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
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}This gallery was made with a newer version of Aetherfit; some details may not show correctly.");
    }

    // Pulls an image's bytes out of the zip entry (new bundles) or the inline base64 (old ones), whichever it has.
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

    // Makes the small preview copy that goes in the bundle. If re-encoding chokes on some odd format we just ship the
    // original bytes instead. Null means there's no image to ship.
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
            // Already jpeg, so don't bother deflating them again — it'd cost time and save basically nothing.
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }

    private static bool IsZip(byte[] raw) =>
        raw.Length >= 2 && raw[0] == 0x50 && raw[1] == 0x4B; // "PK"

    // Reads an old (non-zip) bundle as UTF-8 JSON, un-gzipping first if it starts with the gzip magic bytes.
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
