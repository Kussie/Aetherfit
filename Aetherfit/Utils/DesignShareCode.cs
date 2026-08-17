using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Aetherfit.Utils;

// Not for security - just a compact, copy/paste-safe blob (Base64 over gzip'd JSON) with a version
// prefix so a garbage paste fails fast with a clear message instead of a stack trace. Gear only, no
// tags/description - same scope as the local "Import to Glamourer" feature.
internal static class DesignShareCode
{
    private const string Prefix = "AFDESIGN1:";

    [Serializable]
    private class SharedSlot
    {
        public string Slot { get; set; } = string.Empty;
        public ulong ItemId { get; set; }
        public byte Stain { get; set; }
        public byte Stain2 { get; set; }
    }

    [Serializable]
    private class SharedDesignData
    {
        public string Name { get; set; } = "Imported Design";
        public List<SharedSlot> Equipment { get; set; } = new();
    }

    public static string Encode(string name, List<CachedEquipmentSlot> equipment)
    {
        var data = new SharedDesignData
        {
            Name = name,
            Equipment = equipment
                .Where(e => e.Apply && e.ItemId != 0)
                .Select(e => new SharedSlot { Slot = e.Slot.ToString(), ItemId = e.ItemId, Stain = e.Stain, Stain2 = e.Stain2 })
                .ToList(),
        };
        var json = JsonConvert.SerializeObject(data);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
            writer.Write(json);

        return Prefix + Convert.ToBase64String(output.ToArray());
    }

    public static bool TryDecode(string code, out string? name, out List<CachedEquipmentSlot>? equipment, out string? error)
    {
        name = null;
        equipment = null;
        error = null;
        code = code.Trim();

        if (!code.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "That doesn't look like an Aetherfit design code.";
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(code[Prefix.Length..]);
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var data = JsonConvert.DeserializeObject<SharedDesignData>(reader.ReadToEnd());
            if (data == null || data.Equipment.Count == 0)
            {
                error = "This code doesn't contain any equipment.";
                return false;
            }

            name = data.Name;
            equipment = data.Equipment
                .Where(s => Enum.TryParse<EquipmentSlot>(s.Slot, out _))
                .Select(s => new CachedEquipmentSlot
                {
                    Slot = Enum.Parse<EquipmentSlot>(s.Slot),
                    ItemId = s.ItemId,
                    Stain = s.Stain,
                    Stain2 = s.Stain2,
                    Apply = true,
                    ApplyStain = true,
                })
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            error = "Couldn't read that design code.";
            Plugin.Log.Warning(ex, "Failed to decode design share code");
            return false;
        }
    }
}
