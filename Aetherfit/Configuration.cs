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
}
