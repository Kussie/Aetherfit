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

    // Import guardrails against malicious bundles (oversized files, zip/gzip bombs, manifest floods). These
    // are deliberately generous - a real showcase won't come close - they just stop a crafted file from
    // exhausting memory and crashing the game client.
    private const long MaxBundleFileBytes = 512L * 1024 * 1024;   // on-disk size of the .afgallery
    private const long MaxImageBytes = 15L * 1024 * 1024;         // per-image, after decompression/decode
    private const long MaxManifestBytes = 32L * 1024 * 1024;      // the gallery.json manifest, decompressed
    private const int MaxDesigns = 5000;
    // Total images (cover + additional) a single design may carry in a bundle. Enforced on both export and import.
    private const int MaxImagesPerDesign = 10;

    private readonly Configuration configuration;
    private readonly ImageStorageService imageStorage;
    private readonly GameDataService gameData;
    private readonly DesignAttributionService attribution;

    public GallerySharingService(Configuration configuration, ImageStorageService imageStorage,
        GameDataService gameData, DesignAttributionService attribution)
    {
        this.configuration = configuration;
        this.imageStorage = imageStorage;
        this.gameData = gameData;
        this.attribution = attribution;
    }

    // onlyIds, when given, limits the export to those designs (e.g. the currently filtered list); null exports all.
    public bool ExportToFile(string sharerLabel, string path, IReadOnlySet<Guid>? onlyIds = null)
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
                if (onlyIds != null && !onlyIds.Contains(id))
                    continue;

                // Hidden designs are never exported, regardless of which export path was taken.
                if (configuration.HiddenDesigns.Contains(id))
                    continue;

                var attributed = attribution.Build(outfit);
                var design = new SharedDesign
                {
                    SourceId = id,
                    Name = outfit.Name,
                    Description = outfit.Description,
                    Tags = new List<string>(outfit.Tags),
                    Jobs = new List<uint>(configuration.GetJobAssociations(id)),
                    Equipment = outfit.Equipment.Select(e => new SharedEquipment
                    {
                        Slot = e.Slot,
                        ItemId = e.ItemId,
                        Stain = e.Stain,
                        Stain2 = e.Stain2,
                        Apply = e.Apply,
                        ApplyStain = e.ApplyStain,
                    }).ToList(),
                    BonusItems = outfit.BonusItems.Select(b => new SharedBonusItem
                    {
                        Slot = b.Slot,
                        ItemId = b.ItemId,
                        Apply = b.Apply,
                    }).ToList(),
                    Mods = outfit.Mods.Select(m => new SharedMod
                    {
                        Name = DesignAttributionService.ModDisplayName(m),
                        State = m.State,
                    }).ToList(),
                    AffectedItems = attributed.Items.ToDictionary(
                        kv => kv.Key, kv => DesignAttributionService.ModDisplayName(kv.Value)),
                };

                // Budget the design to MaxImagesPerDesign images total, the cover counting as one.
                var imageCount = 0;
                var cover = EncodeForBundle(imageStorage.GetCoverPath(id));
                if (cover is { } c)
                {
                    var entry = $"images/{id:N}{c.Ext}";
                    imageEntries.Add((entry, c.Bytes));
                    design.Cover = new SharedImage { Ext = c.Ext, Entry = entry };
                    imageCount++;
                }

                var additionalPaths = imageStorage.GetAdditionalPaths(id);
                for (var i = 0; i < additionalPaths.Count && imageCount < MaxImagesPerDesign; i++)
                {
                    var encoded = EncodeForBundle(additionalPaths[i]);
                    if (encoded is not { } a) continue;
                    var entry = $"images/{id:N}_{i}{a.Ext}";
                    imageEntries.Add((entry, a.Bytes));
                    design.AdditionalImages.Add(new SharedImage { Ext = a.Ext, Entry = entry });
                    imageCount++;
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
            var sizeOnDisk = new FileInfo(path).Length;
            if (sizeOnDisk > MaxBundleFileBytes)
                throw new InvalidDataException("Gallery file is too large to import.");

            var raw = File.ReadAllBytes(path);

            // Zip bundle ("PK"): read the manifest and resolve images from the archive entries.
            if (IsZip(raw))
            {
                using var ms = new MemoryStream(raw);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
                var manifest = archive.GetEntry(ManifestEntryName)
                               ?? throw new InvalidDataException("Bundle is missing its manifest.");
                if (manifest.Length > MaxManifestBytes)
                    throw new InvalidDataException("Gallery manifest is too large.");
                string json;
                using (var manifestStream = manifest.Open())
                    json = Encoding.UTF8.GetString(ReadBounded(manifestStream, MaxManifestBytes));

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

        try
        {
            var designs = snapshot.Designs ?? new();
            if (designs.Count > MaxDesigns)
                throw new InvalidDataException($"Gallery contains too many designs ({designs.Count}).");

            foreach (var design in designs)
            {
                var cover = DecodeImage(design.Cover, archive);

                // Cap the design to MaxImagesPerDesign images total, the cover counting as one.
                var additionalBudget = Math.Max(0, MaxImagesPerDesign - (cover != null ? 1 : 0));
                var additional = new List<(byte[] Bytes, string Ext)>();
                foreach (var img in (design.AdditionalImages ?? new()).Take(additionalBudget))
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
                    // Drop null elements/values a crafted bundle could deserialize into, so the read-only
                    // viewer can iterate these without null checks (a null entry would otherwise crash it).
                    Equipment = (design.Equipment ?? new()).OfType<SharedEquipment>().ToList(),
                    BonusItems = (design.BonusItems ?? new()).OfType<SharedBonusItem>().ToList(),
                    Mods = (design.Mods ?? new()).OfType<SharedMod>().ToList(),
                    AffectedItems = SanitizeAffectedItems(design.AffectedItems),
                });
            }

            return gallery;
        }
        catch
        {
            // Don't leave a half-written cache folder behind if the import blows up partway through.
            imageStorage.ClearForeign(originKey);
            throw;
        }
    }

    // Keep only entries whose key and value are both present - a bundle could carry null values, which the
    // viewer would otherwise hand straight to ImGui.
    private static Dictionary<string, string> SanitizeAffectedItems(Dictionary<string, string>? src)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (src == null)
            return result;
        foreach (var (key, value) in src)
            if (key is not null && value is not null)
                result[key] = value;
        return result;
    }

    private static SharedGallery Deserialize(string json)
    {
        // Strip a leading UTF-8 BOM: StreamWriter(Encoding.UTF8) writes one into the manifest, and decoding
        // the bytes with Encoding.UTF8.GetString keeps it, which Json.NET then rejects.
        if (json.Length > 0 && json[0] == '﻿')
            json = json[1..];

        return JsonConvert.DeserializeObject<SharedGallery>(json)
               ?? throw new InvalidDataException("The file did not contain a valid gallery.");
    }

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
            byte[]? bytes = null;
            if (!string.IsNullOrEmpty(image.Entry) && archive != null)
            {
                var entry = archive.GetEntry(image.Entry);
                if (entry == null)
                    return null;
                if (entry.Length > MaxImageBytes)
                {
                    Plugin.Log.Warning("Skipped oversized shared image entry {Entry}", image.Entry);
                    return null;
                }
                using var es = entry.Open();
                bytes = ReadBounded(es, MaxImageBytes);
            }
            else if (!string.IsNullOrEmpty(image.Data))
            {
                // base64 decodes to roughly length * 3/4 bytes; reject before decoding so a giant blob
                // can't allocate a huge array only to be thrown away by the size check below.
                if ((long)image.Data.Length / 4 * 3 > MaxImageBytes)
                {
                    Plugin.Log.Warning("Skipped oversized inline shared image");
                    return null;
                }
                bytes = Convert.FromBase64String(image.Data);
            }

            if (bytes == null || bytes.Length == 0)
                return null;
            if (bytes.Length > MaxImageBytes)
            {
                Plugin.Log.Warning("Skipped oversized shared image");
                return null;
            }
            // Only accept bytes that actually look like a supported image, so a bundle can't smuggle a
            // non-image payload (script/executable) onto disk under an image extension.
            if (!LooksLikeImage(bytes))
            {
                Plugin.Log.Warning("Skipped shared image with unrecognised format");
                return null;
            }
            return (bytes, image.Ext);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to decode shared image");
            return null;
        }
    }

    // Reads at most maxBytes from a stream, throwing if the source produces more (a zip/gzip bomb would).
    private static byte[] ReadBounded(Stream source, long maxBytes)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new InvalidDataException("Decompressed data exceeds the allowed size.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    // Magic-byte sniff for the image formats we're willing to persist (PNG, JPEG, GIF, BMP, WEBP).
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
            return true;                                              // PNG
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return true;                                              // JPEG
        if (b.Length >= 6 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F'
            && b[3] == (byte)'8' && (b[4] == (byte)'7' || b[4] == (byte)'9') && b[5] == (byte)'a')
            return true;                                              // GIF
        if (b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M')
            return true;                                              // BMP
        if (b.Length >= 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return true;                                              // WEBP
        return false;
    }

    // Makes the small preview copy that goes in the bundle. If re-encoding chokes on some odd format we just ship the
    // original bytes instead. Null means there's no image to ship.
    private static (byte[] Bytes, string Ext)? EncodeForBundle(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        (byte[] Bytes, string Ext)? result;
        try
        {
            result = (ScreenshotCaptureService.EncodePreviewJpeg(path, MaxImageDimension, JpegQuality), ".jpg");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to re-encode {Path} for bundle; storing the original.", path);
            try { result = (File.ReadAllBytes(path), Path.GetExtension(path)); }
            catch { return null; }
        }

        // Keep each bundled image within the same per-image limit we enforce on import.
        if (result is { } r && r.Bytes.Length > MaxImageBytes)
        {
            Plugin.Log.Warning("Skipping {Path} for bundle: image exceeds the {Max} byte limit.", path, MaxImageBytes);
            return null;
        }
        return result;
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
            return Encoding.UTF8.GetString(ReadBounded(gzip, MaxManifestBytes));
        }
        if (raw.Length > MaxManifestBytes)
            throw new InvalidDataException("Gallery manifest is too large.");
        return Encoding.UTF8.GetString(raw);
    }
}
