using System;
using System.Collections.Generic;

namespace Aetherfit.Sharing;

// What goes inside a ".afgallery" bundle. Separate from Configuration/CachedOutfit on purpose, so we can change the
// file format without touching the live config. Only carries the basic info (name, description, tags, jobs) and
// images — no equipment or mods, since the viewer only looks at shared designs, it never applies them.
[Serializable]
public sealed class SharedGallery
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string SharerLabel { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public List<SharedDesign> Designs { get; set; } = new();
}

[Serializable]
public sealed class SharedDesign
{
    public Guid SourceId { get; set; }               // the sharer's design id — we only use it to name files / dedupe
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<uint> Jobs { get; set; } = new();    // ClassJob RowIds; these are the same on everyone's client
    public SharedImage? Cover { get; set; }
    public List<SharedImage> AdditionalImages { get; set; } = new();
}

// One image in a bundle. Zip bundles point at a zip entry (Entry); the old inline bundles stuffed the bytes
// straight into Data as base64. We still read both. Ext is just the original extension so the bytes decode right.
[Serializable]
public sealed class SharedImage
{
    public string Ext { get; set; } = ".png";
    public string? Entry { get; set; }  // zip entry name holding the raw bytes (zip bundles)
    public string? Data { get; set; }   // base64 of the raw image bytes (legacy inline bundles)
}

// The unpacked version the viewer actually draws. Images have already been written out to a cache folder (named
// by OriginKey) so we can hand their paths to TextureProvider.GetFromFile like any other image.
public sealed class ForeignGallery
{
    public string OriginKey { get; init; } = string.Empty;
    public string SharerLabel { get; init; } = string.Empty;
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
