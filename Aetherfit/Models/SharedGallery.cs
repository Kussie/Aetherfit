using System;
using System.Collections.Generic;

namespace Aetherfit.Models;

// What goes inside a ".afgallery" bundle. Separate from Configuration/CachedOutfit on purpose, so we can change the
// file format without touching the live config. Only carries the basic info (name, description, tags, jobs) and
// images — no equipment or mods, since the viewer only looks at shared designs, it never applies them.
[Serializable]
public sealed class SharedGallery
{
    // v2 added the equipment / dyes / mod-association details so the shared viewer can show a design's
    // make-up the way the local view does. v1 bundles simply have those lists empty.
    public const int CurrentFormatVersion = 2;

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

    // Make-up of the design, so the recipient (who has neither the sharer's Glamourer design nor mods) can
    // still see what it applies. Item/stain ids resolve against the recipient's own game data.
    public List<SharedEquipment> Equipment { get; set; } = new();
    public List<SharedBonusItem> BonusItems { get; set; } = new();
    public List<SharedMod> Mods { get; set; } = new();

    // Item display name -> the mod responsible for its look, baked at export time using the sharer's
    // Penumbra data (the recipient can't recompute this). Keyed by resolved item name to match the viewer.
    public Dictionary<string, string> AffectedItems { get; set; } = new();
}

[Serializable]
public sealed class SharedEquipment
{
    public EquipmentSlot Slot { get; set; }
    public ulong ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }
    public bool Apply { get; set; }
    public bool ApplyStain { get; set; }
}

[Serializable]
public sealed class SharedBonusItem
{
    public string Slot { get; set; } = string.Empty;
    public ulong ItemId { get; set; }
    public bool Apply { get; set; }
}

[Serializable]
public sealed class SharedMod
{
    public string Name { get; set; } = string.Empty;
    public ModState State { get; set; }
}

[Serializable]
public sealed class SharedImage
{
    public string Ext { get; set; } = ".png";
    public string? Entry { get; set; }  // zip entry name holding the raw bytes (zip bundles)
    public string? Data { get; set; }   // base64 of the raw image bytes (legacy inline bundles)
}

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

    // Design make-up carried straight from the bundle, shown in the read-only details panel.
    public List<SharedEquipment> Equipment { get; init; } = new();
    public List<SharedBonusItem> BonusItems { get; init; } = new();
    public List<SharedMod> Mods { get; init; } = new();
    public Dictionary<string, string> AffectedItems { get; init; } = new();
}
