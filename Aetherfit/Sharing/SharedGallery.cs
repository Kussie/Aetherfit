using System;
using System.Collections.Generic;

namespace Aetherfit.Sharing;

// Wire format for a shared, read-only gallery bundle (".afgallery"). Kept independent of Configuration/CachedOutfit
// so the on-disk format can evolve separately. Deliberately carries only "basic info" (name, description, tags,
// jobs) plus images — no equipment/mod data, because shared designs are never applied on the viewer's machine.
[Serializable]
public sealed class SharedGallery
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string SharerLabel { get; set; } = string.Empty;
    public ulong SharerContentId { get; set; }   // FFXIV ContentId of the character that exported the bundle
    public DateTimeOffset CreatedAt { get; set; }
    public List<SharedDesign> Designs { get; set; } = new();
}

[Serializable]
public sealed class SharedDesign
{
    public Guid SourceId { get; set; }               // opaque key from the sharer; used only for dedupe/file naming
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<uint> Jobs { get; set; } = new();    // ClassJob RowIds — universal game data, portable across clients
    public SharedImage? Cover { get; set; }
    public List<SharedImage> AdditionalImages { get; set; } = new();
}

// An image referenced by the bundle. Ext preserves the original encoding so the bytes decode correctly.
// Newer (zip) bundles store the bytes as a separate zip entry named by Entry, avoiding base64 inflation.
// Older bundles embedded the bytes inline as base64 in Data; both are still read on import.
[Serializable]
public sealed class SharedImage
{
    public string Ext { get; set; } = ".png";
    public string? Entry { get; set; }  // zip entry name holding the raw bytes (zip bundles)
    public string? Data { get; set; }   // base64 of the raw image bytes (legacy inline bundles)
}

// In-memory materialised form the read-only viewer renders. Images have been decoded to a sandboxed cache dir
// (keyed by OriginKey) so the existing TextureProvider.GetFromFile(path) rendering path works unchanged.
public sealed class ForeignGallery
{
    public string OriginKey { get; init; } = string.Empty;
    public string SharerLabel { get; init; } = string.Empty;
    public ulong SharerContentId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<ForeignDesign> Designs { get; init; } = new();
}

public sealed class ForeignDesign
{
    public Guid SourceId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public List<string> Tags { get; init; } = new();
    public List<uint> Jobs { get; init; } = new();
    public string? CoverPath { get; init; }
    public List<string> AdditionalPaths { get; init; } = new();
}
