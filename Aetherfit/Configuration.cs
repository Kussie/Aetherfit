using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Aetherfit;

public enum LoginAction
{
    None,
    ApplyRandom,
    ApplyRandomByTag,
}

public enum GalleryFitMode
{
    Crop,
    Letterbox,
    Stretch,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public Dictionary<Guid, CachedOutfit> CachedOutfits { get; set; } = new();

    // Filenames (not full paths) of user-supplied images stored in {ConfigDirectory}/images/.
    public Dictionary<Guid, string> OutfitImages { get; set; } = new();

    // Filenames (not full paths) of additional images stored in {ConfigDirectory}/images/additional/.
    public Dictionary<Guid, List<string>> OutfitAdditionalImages { get; set; } = new();

    public bool ShowThumbnailOnHover { get; set; } = true;
    public bool DefaultToCoverMode { get; set; } = false;
    public GalleryFitMode GalleryFitMode { get; set; } = GalleryFitMode.Crop;

    // When disabled, the Additional Design Layers panel is hidden and applying a base design never applies layers.
    public bool EnableRandomLayers { get; set; } = false;

    // Legacy: replaced by GalleryFitMode. Migrated on first plugin load if it was set to true.
    public bool GalleryFitWholeImage { get; set; } = false;

    public HashSet<Guid> FavouriteDesigns { get; set; } = new();

    // Designs the user has hidden: they are kept out of the gallery view and excluded from exports,
    // but remain visible in the design tree so they can be unhidden from the detail header.
    public HashSet<Guid> HiddenDesigns { get; set; } = new();

    // User-authored associations between a design and one or more ClassJob RowIds. Stored here (not on CachedOutfit)
    // because CachedOutfits is wholly replaced from Glamourer metadata on every Refresh.
    public Dictionary<Guid, List<uint>> DesignJobAssociations { get; set; } = new();

    // Additional design layers: applying a base design also applies its layer slots top-down. Each slot holds
    // one or more designs; one job-matching design is picked (at random when the slot holds several) and
    // applied before moving to the next slot. Keyed by base design id.
    public Dictionary<Guid, List<DesignLayerSlot>> DesignLayerSlots { get; set; } = new();

    // Legacy: replaced by DesignLayerSlots. Migrated on first plugin load into a single slot per base design.
    public Dictionary<Guid, List<DesignLayer>> DesignLayers { get; set; } = new();

    // Per-character login settings, indexed by FFXIV ContentId.  This at least stays the same even on name changes and world transfers.
    public Dictionary<ulong, CharacterLoginSettings> CharacterLoginSettings { get; set; } = new();
    
    public LoginAction LoginAction { get; set; } = LoginAction.None;
    public List<string> LoginTags { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    // Every tag used across cached outfits, de-duplicated case-insensitively and sorted for display.
    public List<string> DistinctSortedTags()
        => CachedOutfits.Values
            .SelectMany(o => o.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public List<uint> GetJobAssociations(Guid id)
        => DesignJobAssociations.TryGetValue(id, out var jobs) ? jobs : new();

    public void SetJobAssociations(Guid id, List<uint> jobs)
    {
        if (jobs.Count == 0)
            DesignJobAssociations.Remove(id);
        else
            DesignJobAssociations[id] = jobs;
    }

    public List<DesignLayerSlot> GetLayerSlots(Guid id)
        => DesignLayerSlots.TryGetValue(id, out var slots) ? slots : new();

    public void SetLayerSlots(Guid id, List<DesignLayerSlot> slots)
    {
        slots.RemoveAll(s => s.Designs.Count == 0);
        if (slots.Count == 0)
            DesignLayerSlots.Remove(id);
        else
            DesignLayerSlots[id] = slots;
    }

    public CharacterLoginSettings GetOrCreateLoginSettings(ulong contentId)
    {
        if (CharacterLoginSettings.TryGetValue(contentId, out var existing))
            return existing;

        var seeded = new CharacterLoginSettings
        {
            LoginAction = LoginAction,
            LoginTags = new List<string>(LoginTags),
        };
        CharacterLoginSettings[contentId] = seeded;
        Save();
        return seeded;
    }
}

[Serializable]
public class CharacterLoginSettings
{
    public LoginAction LoginAction { get; set; } = LoginAction.None;
    public List<string> LoginTags { get; set; } = new();
}

[Serializable]
public class CachedOutfit
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastEdit { get; set; }

    public List<CachedEquipmentSlot> Equipment { get; set; } = new();
    public List<CachedBonusItem> BonusItems { get; set; } = new();
    public List<CachedCustomization> Customizations { get; set; } = new();
    public List<CachedMod> Mods { get; set; } = new();
    public List<CachedDesignLink> Links { get; set; } = new();

    // The design's clan (subrace, 1-16) and gender (0 male / 1 female). We keep these even when the
    // design doesn't apply them, since the skin/hair colour palettes in human.cmp are picked by clan + gender.
    public int CustomizeClan { get; set; }
    public int CustomizeGender { get; set; }

    // Whether the design actually applies its clan/gender. When it doesn't, the appearance takes the wearing
    // character's race/gender instead - which matters for attributing e.g. hairstyles to a mod.
    public bool CustomizeClanApplied { get; set; }
    public bool CustomizeGenderApplied { get; set; }

    // null = the design does not apply this toggle (grey circle). true/false = the design forces the toggle on/off.
    public bool? HatVisible { get; set; }
    public bool? WeaponVisible { get; set; }
    public bool? VisorToggled { get; set; }

    // Design-level application flags (always present on a design): whether it forces a redraw on apply
    // and whether it resets temporary settings. Shown as enabled/disabled in the equipment panel.
    public bool ForcedRedraw { get; set; }
    public bool ResetTemporarySettings { get; set; }
}

public enum EquipmentSlot
{
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    RFinger,
    LFinger,
}

[Serializable]
public class CachedEquipmentSlot
{
    public EquipmentSlot Slot { get; set; }
    public ulong ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }
    public bool Apply { get; set; }
    public bool ApplyStain { get; set; }
}

[Serializable]
public class CachedBonusItem
{
    // This was annoying to figure out turns out facewear are "BonusItems" not normal slots for Facewear
    public string Slot { get; set; } = string.Empty;
    public ulong ItemId { get; set; }
    public bool Apply { get; set; }
}

[Serializable]
public class CachedCustomization
{
    // Raw Glamourer key (e.g. "HairColor"), used to resolve colour-type parameters against human.cmp.
    public string Key { get; set; } = string.Empty;
    // Friendly label resolved at parse time, e.g. "Hairstyle" or "Skin Color".
    public string Label { get; set; } = string.Empty;
    // Formatted value: a raw index for shape/colour parameters, or "On"/"Off" for toggles.
    public string Value { get; set; } = string.Empty;
    // Numeric customize value, used for colour palette lookups (meaningless for toggles).
    public int RawValue { get; set; }
    public bool IsToggle { get; set; }
}

public enum ModState
{
    Disabled,
    Enabled,
    Remove,
    Inherit,
}

[Serializable]
public class CachedMod
{
    public string Name { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public ModState State { get; set; }
    public int Priority { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
}

// A Glamourer design link: another design applied before/after this one, gated by a job/gearset condition,
// and limited to a subset of application aspects (the LinkType flags).
[Serializable]
public class CachedDesignLink
{
    public Guid DesignId { get; set; }
    // Glamourer ApplicationType flags: Armor=1, Customizations=2, Weapons=4, Dyes/Crests=8, Accessories=16.
    public int LinkType { get; set; }
    // Gearset index condition, or -1 for "any".
    public int Gearset { get; set; } = -1;
    // ClassJobCategory RowId condition, or 0 for "any job".
    public int JobGroup { get; set; }
    // true = applied before this design, false = after. Purely informational for display.
    public bool IsBefore { get; set; }
}

[Flags]
public enum DesignLinkApplication
{
    Armor = 1,
    Customizations = 2,
    Weapons = 4,
    GearCustomization = 8,
    Accessories = 16,
}

// One slot in a base design's additional-layer stack. Slots are applied top-down; when a slot holds several
// designs, one job-matching design is picked at random before continuing to the next slot.
[Serializable]
public class DesignLayerSlot
{
    public List<DesignLayer> Designs { get; set; } = new();
}

// An additional design layer: a design applied on top of the base design. AllJobs applies regardless of the
// wearer's job; otherwise it only applies for the listed jobs.
[Serializable]
public class DesignLayer
{
    public Guid DesignId { get; set; }
    public bool AllJobs { get; set; } = true;
    public List<uint> Jobs { get; set; } = new();
}
