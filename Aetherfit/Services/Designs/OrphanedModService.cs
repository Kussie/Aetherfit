using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;

namespace Aetherfit.Services.Designs;

// The inverse of HealthReportService's missing-mod check: mods Penumbra says are enabled in the active
// collection but that no cached design references at all - candidates for cleanup.
public sealed class OrphanedModService
{
    private readonly Configuration configuration;
    private readonly PenumbraService penumbra;

    public OrphanedModService(Configuration configuration, PenumbraService penumbra)
    {
        this.configuration = configuration;
        this.penumbra = penumbra;
    }

    public sealed record OrphanedMod(string Directory, string DisplayName, string? PenumbraPath);
    public sealed record Report(bool Success, string? Error, IReadOnlyList<OrphanedMod> OrphanedMods);

    public Report BuildReport()
    {
        var collectionId = penumbra.GetLocalPlayerCollectionId();
        if (collectionId == null)
            return new Report(false, "Couldn't resolve your active Penumbra collection — are you logged in?", Array.Empty<OrphanedMod>());

        var names = penumbra.GetModDisplayNames();
        var usedDirectories = new HashSet<string>(
            configuration.DistinctMods().Select(m => m.Directory), StringComparer.OrdinalIgnoreCase);

        var orphaned = new List<OrphanedMod>();
        foreach (var (directory, displayName) in names)
        {
            if (usedDirectories.Contains(directory) || configuration.IsOrphanedModIgnored(directory))
                continue;
            if (penumbra.GetCurrentModSettingsWithTemp(collectionId.Value, directory, displayName) is not { Enabled: true })
                continue;

            var path = penumbra.GetModPath(directory, displayName);
            if (configuration.IsOrphanedModPathIgnored(path))
                continue;

            orphaned.Add(new OrphanedMod(directory, displayName, path));
        }

        return new Report(true, null, orphaned.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
