using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace Aetherfit;

public enum LoginAction
{
    None,
    ApplyRandom,
    ApplyRandomByTag,
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
    public LoginAction LoginAction { get; set; } = LoginAction.None;
    public List<string> LoginTags { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
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
    public List<CachedMod> Mods { get; set; } = new();

    // null = the design does not apply this toggle (grey circle). true/false = the design forces the toggle on/off.
    public bool? HatVisible { get; set; }
    public bool? WeaponVisible { get; set; }
    public bool? VisorToggled { get; set; }
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
    // Raw slot key as serialized by Glamourer (e.g. "Glasses"). Stored as a string so
    // future bonus slots Glamourer might add don't require a model change.
    public string Slot { get; set; } = string.Empty;
    public ulong ItemId { get; set; }
    public bool Apply { get; set; }
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
    // Option group name -> comma-joined selected option indices/values.
    public Dictionary<string, string> Settings { get; set; } = new();
}
