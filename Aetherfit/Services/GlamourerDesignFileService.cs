using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services;

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

    private static string ResolveDesignFilePath(Guid id)
        => Path.Combine(Plugin.PluginInterface.ConfigDirectory.Parent!.FullName, "Glamourer", "designs", $"{id}.json");
}
