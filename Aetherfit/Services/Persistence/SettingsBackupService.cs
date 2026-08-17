using System;
using System.IO;
using Newtonsoft.Json;

namespace Aetherfit.Services.Persistence;

public sealed class SettingsBackupService
{
    public const string FileExtension = ".afbackup";

    public void ExportToFile(Configuration config, string path)
        => File.WriteAllText(path, config.BuildBackupJson());

    public bool TryReadBackup(string path, out Configuration? imported, out string? error)
    {
        try
        {
            imported = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path));
            if (imported == null)
            {
                error = "The file is empty or not a valid backup.";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            imported = null;
            error = $"Couldn't read that file: {ex.Message}";
            return false;
        }
    }
}
