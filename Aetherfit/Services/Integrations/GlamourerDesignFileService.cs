using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Integrations;

// Glamourer has no IPC to write a design's Description/Tags, so this edits Glamourer's own design file
// directly - reading it as a JObject, touching only the two keys we own, and writing it back. There's no
// way to detect whether Glamourer has the design open in its own editor at the time; that risk is
// documented for the caller to surface, not solved here.
public sealed class GlamourerDesignFileService
{
    public sealed record PushResult(bool Success, string? Error);

    public PushResult PushMetadataToGlamourer(Guid id, string? description, IReadOnlyList<string> tags)
    {
        var path = ResolveDesignFilePath(id);
        if (!File.Exists(path))
            return new PushResult(false, "Glamourer design file not found — was the design deleted?");

        try
        {
            var text = File.ReadAllText(path);

            JObject obj;
            using (var stringReader = new StringReader(text))
            using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                obj = JObject.Load(jsonReader);

            if (obj["WriteProtected"]?.Value<bool>() == true)
                return new PushResult(false, "This design is write-protected in Glamourer — remove that protection there first.");

            obj["Description"] = description ?? string.Empty;
            obj["Tags"] = new JArray(tags);

            var usesCrlf = text.Contains("\r\n");
            string output;
            using (var stringWriter = new StringWriter { NewLine = usesCrlf ? "\r\n" : "\n" })
            {
                using (var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented, IndentChar = ' ', Indentation = 2 })
                    obj.WriteTo(jsonWriter);
                output = stringWriter.ToString();
            }

            // Distinct suffix - Glamourer already writes its own "{guid}.json.bak" next to each design.
            var tempPath = path + ".aetherfit-tmp";
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, path, overwrite: true);
            return new PushResult(true, null);
        }
        catch (IOException ex)
        {
            Plugin.Log.Warning(ex, "Design file locked while pushing metadata for {Id}", id);
            return new PushResult(false, "Couldn't write the design file — it may be open in Glamourer's editor. Close it there and try again.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to push metadata to Glamourer design {Id}", id);
            return new PushResult(false, $"Failed to update the Glamourer design file: {ex.Message}");
        }
    }

    // Updates one mod association's option-group selections in the design's own file, leaving every
    // other field untouched. Same group/option shape AppendModsSection writes (GlamourerJsonSchema.cs).
    public PushResult PushModSettingsToGlamourer(Guid id, string modDirectory, IReadOnlyDictionary<string, IReadOnlyCollection<string>> changedGroups)
    {
        var path = ResolveDesignFilePath(id);
        if (!File.Exists(path))
            return new PushResult(false, "Glamourer design file not found — was the design deleted?");

        try
        {
            var text = File.ReadAllText(path);

            JObject obj;
            using (var stringReader = new StringReader(text))
            using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
                obj = JObject.Load(jsonReader);

            if (obj["WriteProtected"]?.Value<bool>() == true)
                return new PushResult(false, "This design is write-protected in Glamourer — remove that protection there first.");

            if (obj["Mods"] is not JArray mods)
                return new PushResult(false, "This design has no mod associations to update.");

            var modEntry = mods.OfType<JObject>().FirstOrDefault(m =>
                string.Equals((string?)m["Directory"], modDirectory, StringComparison.OrdinalIgnoreCase));
            if (modEntry == null)
                return new PushResult(false, "Couldn't find this mod's association in the design file.");

            var settings = modEntry["Settings"] as JArray;
            if (settings == null)
            {
                settings = new JArray();
                modEntry["Settings"] = settings;
            }

            foreach (var (groupName, selected) in changedGroups)
            {
                var options = new JArray();
                foreach (var option in selected)
                    options.Add(new JObject { ["Name"] = option, ["State"] = "Enabled" });

                var groupEntry = settings.OfType<JObject>()
                    .FirstOrDefault(g => string.Equals((string?)g["Name"], groupName, StringComparison.Ordinal));
                if (groupEntry != null)
                    groupEntry["Options"] = options;
                else
                    settings.Add(new JObject { ["Name"] = groupName, ["Options"] = options });
            }

            var usesCrlf = text.Contains("\r\n");
            string output;
            using (var stringWriter = new StringWriter { NewLine = usesCrlf ? "\r\n" : "\n" })
            {
                using (var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented, IndentChar = ' ', Indentation = 2 })
                    obj.WriteTo(jsonWriter);
                output = stringWriter.ToString();
            }

            var tempPath = path + ".aetherfit-tmp";
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, path, overwrite: true);
            return new PushResult(true, null);
        }
        catch (IOException ex)
        {
            Plugin.Log.Warning(ex, "Design file locked while pushing mod settings for {Id}", id);
            return new PushResult(false, "Couldn't write the design file — it may be open in Glamourer's editor. Close it there and try again.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to push mod settings to Glamourer design {Id}", id);
            return new PushResult(false, $"Failed to update the Glamourer design file: {ex.Message}");
        }
    }

    internal static string ResolveDesignFilePath(Guid id)
        => Path.Combine(Plugin.PluginInterface.ConfigDirectory.Parent!.FullName, "Glamourer", "designs", $"{id}.json");
}
