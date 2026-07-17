using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Aetherfit.Services;

// Maps a fired booru tag to a composite "category/type" tag (e.g. "micro bikini" -> "swimsuit/bikini").
// The map is derived offline from Danbooru's tag-implication graph and embedded as a resource; see
// Resources/composite_tags.json. Keys and values are in the tagger's space-separated form.
public static class CompositeTags
{
    private const string ResourceName = "Aetherfit.composite_tags.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyMap = new(Load);

    public static IReadOnlyDictionary<string, string> Map => LazyMap.Value;

    private static IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                Plugin.Log.Warning("Composite tag map resource {Name} was not found", ResourceName);
                return new Dictionary<string, string>();
            }

            using var reader = new StreamReader(stream);
            var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
            return new Dictionary<string, string>(raw ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not load composite tag map");
            return new Dictionary<string, string>();
        }
    }
}
