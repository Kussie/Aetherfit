using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services;

// Writes tags straight into Glamourer's design files - its IPC has no way to modify a design.
// Glamourer only re-reads these files on reload, so writes are tracked in PendingTagWrites and
// overlaid onto the cached metadata until Glamourer reports them itself.
public sealed class GlamourerDesignFileService
{
    private const int BackupsKeptPerDesign = 3;

    private readonly Configuration configuration;
    private readonly string designsDir;
    private readonly string backupDir;

    public GlamourerDesignFileService(Configuration configuration)
    {
        this.configuration = configuration;
        var configRoot = Plugin.PluginInterface.ConfigDirectory.Parent!.FullName;
        designsDir = Path.Combine(configRoot, "Glamourer", "designs");
        backupDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "glamourer-backups");
    }

    private string DesignPath(Guid id) => Path.Combine(designsDir, $"{id}.json");

    // Returns null on success, otherwise a user-facing error message.
    public string? WriteTags(Guid id, IReadOnlyCollection<string> tagsToAdd)
    {
        if (tagsToAdd.Count == 0)
            return null;

        string? error = null;
        var written = WriteTagsToFile(id, tagsToAdd, ref error);
        if (!written)
            return error;

        if (configuration.CachedOutfits.TryGetValue(id, out var cached))
        {
            foreach (var tag in tagsToAdd)
            {
                if (!cached.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    cached.Tags.Add(tag);
            }
        }

        var pending = configuration.PendingTagWrites.TryGetValue(id, out var list) ? list : new List<string>();
        foreach (var tag in tagsToAdd)
        {
            if (!pending.Contains(tag, StringComparer.OrdinalIgnoreCase))
                pending.Add(tag);
        }
        configuration.PendingTagWrites[id] = pending;
        configuration.Save();

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Added {tagsToAdd.Count} tag(s) to the design file. "
                           + "Glamourer will pick them up after it reloads (e.g. a game restart).");
        return null;
    }

    private bool WriteTagsToFile(Guid id, IReadOnlyCollection<string> tagsToAdd, ref string? error)
    {
        var path = DesignPath(id);
        try
        {
            if (!File.Exists(path))
            {
                error = "The design file could not be found in Glamourer's config folder.";
                return false;
            }

            var json = JObject.Parse(File.ReadAllText(path));

            if (!string.Equals(json["Identifier"]?.ToString(), id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                error = "The design file does not match this design — not touching it.";
                return false;
            }

            if (json["WriteProtected"]?.Value<bool>() == true)
            {
                error = "This design is write-protected in Glamourer.";
                return false;
            }

            var tags = json["Tags"] as JArray ?? new JArray();
            var existing = new HashSet<string>(tags.Select(t => t.ToString()), StringComparer.OrdinalIgnoreCase);
            var newTags = tagsToAdd.Where(t => !existing.Contains(t)).ToList();
            if (newTags.Count == 0)
                return true;

            BackupDesignFile(id, path);

            foreach (var tag in newTags)
                tags.Add(tag);
            json["Tags"] = tags;
            json["LastEdit"] = DateTimeOffset.Now.ToString("o");

            var tempPath = path + ".aetherfit.tmp";
            File.WriteAllText(tempPath, json.ToString(Formatting.Indented));
            File.Replace(tempPath, path, null);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to write tags into design file {Path}", path);
            error = $"Could not update the design file: {ex.Message}";
            return false;
        }
    }

    private void BackupDesignFile(Guid id, string path)
    {
        Directory.CreateDirectory(backupDir);
        File.Copy(path, Path.Combine(backupDir, $"{id}-{DateTime.Now:yyyyMMddHHmmss}.json"), true);

        var old = Directory.GetFiles(backupDir, $"{id}-*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(BackupsKeptPerDesign);
        foreach (var file in old)
        {
            try { File.Delete(file); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Could not prune old design backup {File}", file); }
        }
    }

    // Called right after a Refresh replaced CachedOutfits with what Glamourer reports over IPC.
    public void ReconcilePendingWrites()
    {
        if (configuration.PendingTagWrites.Count == 0)
            return;

        var changed = false;
        foreach (var (id, pending) in configuration.PendingTagWrites.ToList())
        {
            if (!configuration.CachedOutfits.TryGetValue(id, out var cached))
            {
                configuration.PendingTagWrites.Remove(id);
                changed = true;
                continue;
            }

            if (pending.All(t => cached.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                // Glamourer reloaded and now owns these tags.
                configuration.PendingTagWrites.Remove(id);
                changed = true;
                continue;
            }

            // If Glamourer re-saved the design from memory since our write, the tags are gone from
            // disk too — put them back so they survive until the next reload.
            if (!FileContainsTags(id, pending))
            {
                string? error = null;
                if (!WriteTagsToFile(id, pending, ref error))
                    Plugin.Log.Warning("Could not re-write pending tags for design {Id}: {Error}", id, error ?? "unknown");
            }

            foreach (var tag in pending)
            {
                if (!cached.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    cached.Tags.Add(tag);
            }
        }

        if (changed)
            configuration.Save();
    }

    private bool FileContainsTags(Guid id, List<string> tags)
    {
        try
        {
            var path = DesignPath(id);
            if (!File.Exists(path))
                return true;

            var json = JObject.Parse(File.ReadAllText(path));
            var fileTags = new HashSet<string>(
                (json["Tags"] as JArray ?? new JArray()).Select(t => t.ToString()),
                StringComparer.OrdinalIgnoreCase);
            return tags.All(fileTags.Contains);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not inspect design file for pending tags {Id}", id);
            return true;
        }
    }
}
