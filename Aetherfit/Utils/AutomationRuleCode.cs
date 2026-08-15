using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace Aetherfit.Utils;

// Not for security - just a compact, copy/paste-safe blob (Base64 over gzip'd JSON) with a version
// prefix so a garbage paste fails fast with a clear message instead of a stack trace.
internal static class AutomationRuleCode
{
    private const string Prefix = "AFRULE1:";

    [Serializable]
    private class SharedRuleData
    {
        public string Name { get; set; } = "Imported Rule";
        public List<AutomationCondition> Conditions { get; set; } = new();
    }

    public static string Encode(AutomationRule rule)
    {
        var data = new SharedRuleData { Name = rule.Name, Conditions = rule.Conditions };
        var json = JsonConvert.SerializeObject(data);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, Encoding.UTF8))
            writer.Write(json);

        return Prefix + Convert.ToBase64String(output.ToArray());
    }

    public static bool TryDecode(string code, out AutomationRule? rule, out string? error)
    {
        rule = null;
        error = null;
        code = code.Trim();

        if (!code.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "That doesn't look like an Aetherfit rule code.";
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(code[Prefix.Length..]);
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var data = JsonConvert.DeserializeObject<SharedRuleData>(reader.ReadToEnd());
            if (data == null)
            {
                error = "Couldn't read that rule code.";
                return false;
            }

            rule = new AutomationRule { Name = data.Name, Conditions = data.Conditions };
            return true;
        }
        catch (Exception ex)
        {
            error = "Couldn't read that rule code.";
            Plugin.Log.Warning(ex, "Failed to decode automation rule code");
            return false;
        }
    }
}
