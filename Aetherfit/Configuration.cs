using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;
using Newtonsoft.Json;

namespace Aetherfit;

public enum LoginAction
{
    None,
    ApplyRandom,
    ApplyRandomByTag,
    ReapplyLast,
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

    // Persisted separately by OutfitCacheStore (it's derived data and large); ShouldSerialize keeps it
    // out of the plugin config while still deserializing configs from before the split.
    public Dictionary<Guid, CachedOutfit> CachedOutfits { get; set; } = new();

    public bool ShouldSerializeCachedOutfits() => false;

    // Filenames (not full paths) of user-supplied images stored in {ConfigDirectory}/images/.
    public Dictionary<Guid, string> OutfitImages { get; set; } = new();

    // Filenames (not full paths) of additional images stored in {ConfigDirectory}/images/additional/.
    public Dictionary<Guid, List<string>> OutfitAdditionalImages { get; set; } = new();

    public bool ShowThumbnailOnHover { get; set; } = true;

    public bool ImageViewerFollowsSelection { get; set; } = false;
    public bool DefaultToCoverMode { get; set; } = false;
    public GalleryFitMode GalleryFitMode { get; set; } = GalleryFitMode.Crop;

    public bool GalleryPinFavouritesFirst { get; set; } = true;
    public float GalleryThumbTargetWidth { get; set; } = 220f;
    public float ForeignGalleryThumbTargetWidth { get; set; } = 220f;

    // When disabled, the Additional Design Layers panel is hidden and applying a base design never applies layers.
    public bool EnableRandomLayers { get; set; } = false;

    public bool GlamaholicEnabled { get; set; } = true;
    public bool GlamourPlateEnabled { get; set; } = true;
    public bool SimpleGlamourSwitcherEnabled { get; set; } = true;

    public bool IsProviderEnabled(DesignSource source) => source switch
    {
        DesignSource.Glamaholic => GlamaholicEnabled,
        DesignSource.GlamourPlate => GlamourPlateEnabled,
        DesignSource.SimpleGlamourSwitcher => SimpleGlamourSwitcherEnabled,
        _ => true, // Glamourer (and any future required source) has no toggle
    };

    public bool GlamaholicResetTemporarySettingsBeforeApply { get; set; }
    public bool GlamourPlateResetTemporarySettingsBeforeApply { get; set; }
    public bool SimpleGlamourSwitcherResetTemporarySettingsBeforeApply { get; set; }

    public bool ResetTemporarySettingsBeforeApply(DesignSource source) => source switch
    {
        DesignSource.Glamaholic => GlamaholicResetTemporarySettingsBeforeApply,
        DesignSource.GlamourPlate => GlamourPlateResetTemporarySettingsBeforeApply,
        DesignSource.SimpleGlamourSwitcher => SimpleGlamourSwitcherResetTemporarySettingsBeforeApply,
        _ => false,
    };

    // Set once the user closes the help blurb in the Additional Design Layers panel.
    public bool AdditionalLayersHelpDismissed { get; set; }

    // Set once the user closes the composite-tags help note in the Add Tag popup.
    public bool CompositeTagsHelpDismissed { get; set; }

    // Legacy: replaced by GalleryFitMode. Migrated on first plugin load if it was set to true.
    public bool GalleryFitWholeImage { get; set; } = false;

    public HashSet<Guid> FavouriteDesigns { get; set; } = new();

    public HashSet<Guid> HiddenDesigns { get; set; } = new();

    // User-authored associations between a design and one or more ClassJob RowIds. Stored here (not on CachedOutfit)
    // because CachedOutfits is wholly replaced from Glamourer metadata on every Refresh.
    public Dictionary<Guid, List<uint>> DesignJobAssociations { get; set; } = new();

    public Dictionary<Guid, LocalDesignMeta> DesignMeta { get; set; } = new();

    public Dictionary<Guid, List<DesignLayerSlot>> DesignLayerSlots { get; set; } = new();

    // Variant design -> its parent + which of the parent's data it inherits. Stored here (not on
    // CachedOutfit) for the same reason as DesignJobAssociations above.
    public Dictionary<Guid, VariantInfo> DesignVariants { get; set; } = new();

    // Legacy: replaced by DesignLayerSlots. Migrated on first plugin load into a single slot per base design.
    public Dictionary<Guid, List<DesignLayer>> DesignLayers { get; set; } = new();

    // Health Report findings dismissed by the user, keyed by design id then check-type tag
    // ("MissingMod"/"BrokenItem"/"Duplicate" - see HealthReportWindow) - so a dismissed finding
    // doesn't reappear on the next check run.
    public Dictionary<Guid, HashSet<string>> IgnoredHealthChecks { get; set; } = new();

    // Provider -> (that provider's own native design id -> the stable Aetherfit-owned Guid for it).
    // Only ever grows; Glamourer never appears here (see DesignIdentity.Resolve).
    public Dictionary<DesignSource, Dictionary<Guid, Guid>> DesignIdentityMap { get; set; } = new();

    // Per-character login settings, indexed by FFXIV ContentId.  This at least stays the same even on name changes and world transfers.
    public Dictionary<ulong, CharacterLoginSettings> CharacterLoginSettings { get; set; } = new();

    public bool TagSuggestionEnabled { get; set; } = true;

    public float TagSuggestionThreshold { get; set; } = 0.35f;

    // Which tagger model to use, by TagModelStore.Models id. Unknown values fall back to the first entry.
    public string TagSuggestionModel { get; set; } = "wd-vit-tagger-v3";

    // Also suggest composite "category/type" tags (e.g. swimsuit/bikini) derived from Danbooru's
    // tag-implication graph, alongside the flat tags the model fired.
    public bool TagSuggestionComposites { get; set; } = true;

    // When a composite tag is suggested (e.g. tops/crop top), also hide the flat tag it was built
    // from (crop top) rather than suggesting both. Off by default - preserves existing behaviour.
    public bool TagSuggestionHideCompositeSources { get; set; } = false;

    // Deliberately empty here, not seeded with defaults - see Plugin.cs's constructor, which seeds
    // sensible defaults only for a genuinely fresh install. A non-empty literal here previously caused
    // Newtonsoft's default ObjectCreationHandling.Auto to reuse-and-append onto this list on every
    // deserialize instead of replacing it, silently duplicating the seeded entries on every plugin load.
    public List<string> TagSuggestionBlacklist { get; set; } = new();

    public Dictionary<string, string> TagSuggestionRenames { get; set; } = new() { ["pantyhose"] = "stockings" };

    public LoginAction LoginAction { get; set; } = LoginAction.None;
    public List<string> LoginTags { get; set; } = new();

    // Global hotkeys - detected via IKeyState polling on Framework.Update (see Plugin.cs), never
    // suppressed/consumed, so a key already bound elsewhere (the game, another plugin) still also
    // does whatever it normally does.
    public KeyBind WearRandomKeybind { get; set; } = new();
    public KeyBind WearFavouriteKeybind { get; set; } = new();
    public KeyBind WearLastKeybind { get; set; } = new();
    public KeyBind RevertKeybind { get; set; } = new();
    public KeyBind QuickSearchKeybind { get; set; } = new();

    public int LiveShareDefaultTtlMinutes { get; set; } = 30;

    public string LiveShareInstallId { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonExtensionData]
    private IDictionary<string, Newtonsoft.Json.Linq.JToken>? ExtensionData { get; set; }

    [NonSerialized]
    private Services.Persistence.ConfigurationSaver? saver;

    public void AttachSaver(Services.Persistence.ConfigurationSaver configSaver) => saver = configSaver;

    public void Save()
    {
        // Coalesced through the saver; a direct write only happens before it exists (startup migrations).
        if (saver != null)
            saver.Request();
        else
            Plugin.PluginInterface.SavePluginConfig(this);
    }

    // Every tag used across cached outfits (plus the segments of composite tags, so "bikini" is
    // offered when only "swimsuit/bikini" exists), de-duplicated case-insensitively and sorted.
    public List<string> DistinctSortedTags()
        => Utils.TagMatching.WithSegments(CachedOutfits.Values.SelectMany(o => o.Tags));

    // Only mods that at least one cached design references - there's no "list all installed mods" IPC wired up.
    public List<(string Directory, string DisplayName)> DistinctMods()
        => CachedOutfits.Values.SelectMany(o => o.Mods)
            .GroupBy(m => m.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, Services.Designs.DesignAttributionService.ModDisplayName(g.First())))
            .OrderBy(m => m.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string ResolveDesignName(Guid id)
        => CachedOutfits.TryGetValue(id, out var outfit) && !string.IsNullOrWhiteSpace(outfit.Name)
            ? outfit.Name
            : "(unknown design)";

    public List<uint> GetJobAssociations(Guid id)
        => DesignJobAssociations.TryGetValue(id, out var jobs) ? jobs : new();

    public void SetJobAssociations(Guid id, List<uint> jobs)
    {
        if (jobs.Count == 0)
            DesignJobAssociations.Remove(id);
        else
            DesignJobAssociations[id] = jobs;
    }

    public LocalDesignMeta GetOrSeedDesignMeta(Guid id, string? glamDescription, IReadOnlyList<string> glamTags)
    {
        if (DesignMeta.TryGetValue(id, out var existing))
            return existing;

        var seeded = new LocalDesignMeta
        {
            Description = glamDescription,
            Tags = new List<string>(glamTags),
        };
        DesignMeta[id] = seeded;
        return seeded;
    }

    // Bumped on every RecordLastApplied so gallery sort/filter caches (which key off the design-list
    // generation, not individual designs) know to rebuild without waiting for a full RefreshDesigns.
    [JsonIgnore]
    public int LastAppliedVersion { get; private set; }

    // Takes the live CachedOutfit too, mirroring SetDescription/AddTag/RemoveTag - writing DesignMeta
    // alone would only reach the UI on the next full RefreshDesigns, since CachedOutfit is otherwise
    // only overlaid from DesignMeta during that refresh's metadata drain.
    public void RecordLastApplied(Guid id, CachedOutfit outfit)
    {
        if (!DesignMeta.TryGetValue(id, out var meta))
        {
            meta = new LocalDesignMeta();
            DesignMeta[id] = meta;
        }
        var now = DateTimeOffset.UtcNow;
        meta.LastApplied = now;
        outfit.LastAppliedAt = now;
        LastAppliedVersion++;
        Save();
    }

    // Bumped whenever the ignore set changes, so the toolbar badge's cached "any issues?" check
    // knows to recompute without re-scanning the whole library every frame.
    [JsonIgnore]
    public int HealthCheckIgnoreVersion { get; private set; }

    public bool IsHealthCheckIgnored(Guid id, string checkType)
        => IgnoredHealthChecks.TryGetValue(id, out var set) && set.Contains(checkType);

    public void IgnoreHealthCheck(Guid id, string checkType)
    {
        if (!IgnoredHealthChecks.TryGetValue(id, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            IgnoredHealthChecks[id] = set;
        }
        set.Add(checkType);
        HealthCheckIgnoreVersion++;
        Save();
    }

    public void ClearIgnoredHealthChecks()
    {
        IgnoredHealthChecks.Clear();
        HealthCheckIgnoreVersion++;
        Save();
    }

    public int MergeTagsFromGlamourer(Guid id, CachedOutfit outfit)
    {
        var meta = GetOrSeedDesignMeta(id, outfit.GlamourerDescription, outfit.GlamourerTags);
        var added = 0;
        foreach (var tag in outfit.GlamourerTags)
        {
            if (meta.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                continue;
            meta.Tags.Add(tag);
            added++;
        }

        if (added > 0)
        {
            outfit.Tags = new List<string>(meta.Tags);
            Save();
        }
        return added;
    }

    public void PullDescriptionFromGlamourer(Guid id, CachedOutfit outfit)
    {
        var meta = GetOrSeedDesignMeta(id, outfit.GlamourerDescription, outfit.GlamourerTags);
        meta.Description = outfit.GlamourerDescription;
        outfit.Description = meta.Description;
        Save();
    }

    // Direct edits made in Aetherfit itself, now that Tags/Description are locally owned.
    public void SetDescription(Guid id, CachedOutfit outfit, string? description)
    {
        var meta = GetOrSeedDesignMeta(id, outfit.GlamourerDescription, outfit.GlamourerTags);
        meta.Description = description;
        outfit.Description = description;
        Save();
    }

    public bool AddTag(Guid id, CachedOutfit outfit, string tag)
    {
        tag = tag.Trim();
        if (string.IsNullOrEmpty(tag))
            return false;

        var meta = GetOrSeedDesignMeta(id, outfit.GlamourerDescription, outfit.GlamourerTags);
        if (meta.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            return false;

        meta.Tags.Add(tag);
        outfit.Tags = new List<string>(meta.Tags);
        Save();
        return true;
    }

    public void RemoveTag(Guid id, CachedOutfit outfit, string tag)
    {
        if (!DesignMeta.TryGetValue(id, out var meta))
            return;
        if (meta.Tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        outfit.Tags = new List<string>(meta.Tags);
        Save();
    }

    // Falls back to the parent's own slots when this design is a variant with none of its own -
    // a single hop, since variant-of-a-variant chains aren't allowed (see SetVariantParent).
    public List<DesignLayerSlot> GetLayerSlots(Guid id)
    {
        if (DesignLayerSlots.TryGetValue(id, out var ownSlots))
            return ownSlots;
        if (DesignVariants.TryGetValue(id, out var variant) && DesignLayerSlots.TryGetValue(variant.ParentId, out var parentSlots))
            return parentSlots;
        return new();
    }

    public void SetLayerSlots(Guid id, List<DesignLayerSlot> slots)
    {
        slots.RemoveAll(s => s.Designs.Count == 0);
        if (slots.Count == 0)
            DesignLayerSlots.Remove(id);
        else
            DesignLayerSlots[id] = slots;
    }

    public VariantInfo? GetVariantInfo(Guid id) => DesignVariants.TryGetValue(id, out var v) ? v : null;

    public IEnumerable<KeyValuePair<Guid, VariantInfo>> GetVariantsOf(Guid parentId)
        => DesignVariants.Where(kv => kv.Value.ParentId == parentId);

    public void SetVariantParent(Guid id, Guid parentId, bool inheritTagsAndDescription = true, bool inheritGear = false)
    {
        DesignVariants[id] = new VariantInfo
        {
            ParentId = parentId,
            InheritTagsAndDescription = inheritTagsAndDescription,
            InheritGear = inheritGear,
        };
        ApplyVariantTagDescriptionFallback(id);
    }

    public void RemoveVariant(Guid id)
    {
        DesignVariants.Remove(id);
        ApplyVariantTagDescriptionFallback(id);
    }

    // Resets to this design's own meta, then conditionally overlays the parent's Description/Tags if
    // this is a variant with inheritance on and its own values are unset - always reset first so
    // toggling inheritance off or unlinking correctly reverts the displayed value.
    public void ApplyVariantTagDescriptionFallback(Guid id)
    {
        if (!CachedOutfits.TryGetValue(id, out var outfit))
            return;

        DesignMeta.TryGetValue(id, out var meta);
        outfit.Description = meta?.Description;
        outfit.Tags = meta != null ? new List<string>(meta.Tags) : new List<string>();

        if (DesignVariants.TryGetValue(id, out var variant) && variant.InheritTagsAndDescription
            && CachedOutfits.TryGetValue(variant.ParentId, out var parentOutfit))
        {
            if (string.IsNullOrWhiteSpace(outfit.Description))
                outfit.Description = parentOutfit.Description;
            if (outfit.Tags.Count == 0)
                outfit.Tags = new List<string>(parentOutfit.Tags);
        }
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
public sealed class KeyBind
{
    public VirtualKey Key { get; set; } = VirtualKey.NO_KEY;
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }

    [JsonIgnore]
    public bool IsSet => Key != VirtualKey.NO_KEY;

    [JsonIgnore]
    public bool WasDown { get; set; }

    public override string ToString()
    {
        if (!IsSet)
            return "Not bound";

        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(Key.ToString());
        return string.Join(" + ", parts);
    }
}

[Serializable]
public class CharacterLoginSettings
{
    public LoginAction LoginAction { get; set; } = LoginAction.None;
    public List<string> LoginTags { get; set; } = new();

    public Guid? LastWornDesign { get; set; }
    public List<Guid> LastWornLayers { get; set; } = new();
    public List<Guid> LastWornBeforeLayers { get; set; } = new();
    public List<Guid> RecentDesignHistory { get; set; } = new();
    public bool ReapplyOnZoneChange { get; set; } = false;
}

[Serializable]
public class LocalDesignMeta
{
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public DateTimeOffset? LastApplied { get; set; }
}

[Serializable]
public class VariantInfo
{
    public Guid ParentId { get; set; }
    public bool InheritTagsAndDescription { get; set; } = true;
    public bool InheritGear { get; set; } = false;
}

[Serializable]
public class CachedOutfit
{
    public string Name { get; set; } = string.Empty;

    public DesignSource Source { get; set; }
    public Guid ProviderDesignId { get; set; }

    // Aetherfit's own locally-owned values (see Configuration.DesignMeta) - what's actually shown/used.
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();

    // Glamourer's current value as of the last refresh - read-only reference data for the Sync-from-Glamourer
    // actions. Not what's displayed; Description/Tags above are the locally-owned display value.
    public string? GlamourerDescription { get; set; }
    public List<string> GlamourerTags { get; set; } = new();

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastEdit { get; set; }

    public DateTimeOffset? LastAppliedAt { get; set; }

    public List<CachedEquipmentSlot> Equipment { get; set; } = new();
    public List<CachedBonusItem> BonusItems { get; set; } = new();
    public List<CachedCustomization> Customizations { get; set; } = new();
    public List<CachedMod> Mods { get; set; } = new();
    public List<CachedDesignLink> Links { get; set; } = new();

    public int CustomizeClan { get; set; }
    public int CustomizeGender { get; set; }

    public bool CustomizeClanApplied { get; set; }
    public bool CustomizeGenderApplied { get; set; }

    public bool? HatVisible { get; set; }
    public bool? WeaponVisible { get; set; }
    public bool? VisorToggled { get; set; }

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

[Serializable]
public class DesignLayerSlot
{
    public List<DesignLayer> Designs { get; set; } = new();
    public bool IsBefore { get; set; }
}

[Serializable]
public class DesignLayer
{
    public Guid DesignId { get; set; }
    public bool AllJobs { get; set; } = true;
    public List<uint> Jobs { get; set; } = new();
}
