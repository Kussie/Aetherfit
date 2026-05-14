using System;
using System.Collections.Generic;
using System.Linq;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services;

public sealed class GlamourerService
{
    private readonly GetDesignListExtended getDesignListExtended;
    private readonly GetDesignJObject getDesignJObject;
    private readonly ApplyDesign applyDesign;
    private readonly RevertState revertState;

    public GlamourerService()
    {
        getDesignListExtended = new GetDesignListExtended(Plugin.PluginInterface);
        getDesignJObject = new GetDesignJObject(Plugin.PluginInterface);
        applyDesign = new ApplyDesign(Plugin.PluginInterface);
        revertState = new RevertState(Plugin.PluginInterface);
    }

    public sealed record DesignInfo(Guid Id, string DisplayName, string FullPath, uint Color);

    public sealed record FetchResult(
        IReadOnlyList<DesignInfo> Designs,
        Dictionary<Guid, CachedOutfit> Metadata,
        string? Error);

    public FetchResult FetchDesigns()
    {
        try
        {
            var data = getDesignListExtended.Invoke();
            var designs = new List<DesignInfo>(data.Count);
            var metadata = new Dictionary<Guid, CachedOutfit>(data.Count);

            foreach (var (guid, tuple) in data)
            {
                designs.Add(new DesignInfo(guid, tuple.DisplayName, tuple.FullPath, tuple.DisplayColor));

                try
                {
                    var jobject = getDesignJObject.Invoke(guid);
                    if (jobject != null)
                        metadata[guid] = ParseOutfit(jobject);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to cache metadata for design {Id}", guid);
                }
            }

            return new FetchResult(designs, metadata, null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to fetch Glamourer designs");
            return new FetchResult(Array.Empty<DesignInfo>(), new Dictionary<Guid, CachedOutfit>(), ex.Message);
        }
    }

    public void Apply(Guid id, string designName)
    {
        try
        {
            var result = applyDesign.Invoke(id, 0, 0);
            Plugin.ChatGui.Print($"[Aetherfit] Applied \"{designName}\": {result}");
            Plugin.Log.Info("Applied design {Name} ({Id}): {Result}", designName, id, result);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Apply failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to apply Glamourer design {Id}", id);
        }
    }

    public void Revert()
    {
        try
        {
            var result = revertState.Invoke(0);
            Plugin.ChatGui.Print($"[Aetherfit] Reverted appearance to game state: {result}");
            Plugin.Log.Info("Reverted appearance to game state: {Result}", result);
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[Aetherfit] Revert failed: {ex.Message}");
            Plugin.Log.Warning(ex, "Failed to revert appearance");
        }
    }

    private static CachedOutfit ParseOutfit(JObject j)
    {
        var name = ReadString(j["Name"]) ?? "(unnamed)";
        var description = ReadString(j["Description"]);
        if (string.IsNullOrWhiteSpace(description))
            description = null;

        var tags = j["Tags"] is JArray tagArray
            ? tagArray.Select(t => ReadString(t) ?? string.Empty)
                      .Where(t => !string.IsNullOrWhiteSpace(t))
                      .ToList()
            : new List<string>();

        return new CachedOutfit
        {
            Name = name,
            Description = description,
            Tags = tags,
            CreatedAt = ReadDateTimeOffset(j["CreationDate"]),
            LastEdit = ReadDateTimeOffset(j["LastEdit"]),
        };
    }

    // Avoid JToken.Value<string>(): it goes through Convert.ChangeType, which throws
    // "Object must implement IConvertible" on token values like DateTimeOffset.
    private static string? ReadString(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
            return null;
        if (token is JValue v)
            return v.Value?.ToString();
        return token.ToString();
    }

    private static DateTimeOffset? ReadDateTimeOffset(JToken? token)
    {
        if (token is not JValue v || v.Value is null)
            return null;
        return v.Value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
            string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
            _ => null,
        };
    }
}
