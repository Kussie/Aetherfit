using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Integrations;

namespace Aetherfit.Services.Designs;

// Aggregate stats over the whole cached design library - same service/window split as HealthReportService,
// so this stays a plain, testable function over whatever's cached rather than entangled with ImGui.
public sealed class WardrobeAnalyticsService
{
    private const int TopN = 10;

    private readonly Configuration configuration;
    private readonly IReadOnlyList<IDesignProvider> designProviders;

    public WardrobeAnalyticsService(Configuration configuration, IReadOnlyList<IDesignProvider> designProviders)
    {
        this.configuration = configuration;
        this.designProviders = designProviders;
    }

    public sealed record WornEntry(Guid Id, string Name, int WornCount, DateTimeOffset? LastAppliedAt);
    public sealed record NeverWornEntry(Guid Id, string Name);
    public sealed record TagCount(string Tag, int Count);
    public sealed record ModUsage(string Directory, string DisplayName, int Count);
    public sealed record SourceCount(DesignSource Source, string DisplayName, int Count);
    public sealed record DateEntry(Guid Id, string Name, DateTimeOffset CreatedAt);

    public sealed record Report(
        int TotalDesigns,
        IReadOnlyList<WornEntry> MostWorn,
        IReadOnlyList<WornEntry> LeastWorn,
        IReadOnlyList<NeverWornEntry> NeverWorn,
        IReadOnlyList<TagCount> TagDistribution,
        IReadOnlyList<ModUsage> ModUsage,
        IReadOnlyList<SourceCount> SourceBreakdown,
        DateEntry? Oldest,
        DateEntry? Newest);

    public Report BuildReport()
    {
        var outfits = configuration.CachedOutfits
            .Where(kv => configuration.IsProviderEnabled(kv.Value.Source))
            .ToList();

        var worn = outfits.Where(kv => kv.Value.WornCount > 0).ToList();
        var mostWorn = worn.OrderByDescending(kv => kv.Value.WornCount)
            .Take(TopN)
            .Select(kv => new WornEntry(kv.Key, kv.Value.Name, kv.Value.WornCount, kv.Value.LastAppliedAt))
            .ToList();
        var leastWorn = worn.OrderBy(kv => kv.Value.WornCount)
            .Take(TopN)
            .Select(kv => new WornEntry(kv.Key, kv.Value.Name, kv.Value.WornCount, kv.Value.LastAppliedAt))
            .ToList();

        // LastAppliedAt, not WornCount == 0 - the established "has this ever been worn" convention
        // elsewhere in the codebase (MainWindow.Filters.cs's never-worn filter).
        var neverWorn = outfits.Where(kv => kv.Value.LastAppliedAt is null)
            .Select(kv => new NeverWornEntry(kv.Key, kv.Value.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tagDistribution = outfits.SelectMany(kv => kv.Value.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagCount(g.First(), g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList();

        var modUsage = outfits
            .SelectMany(kv => kv.Value.Mods.Where(m => m.State == ModState.Enabled).Select(m => (Mod: m, DesignId: kv.Key)))
            .GroupBy(x => x.Mod.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModUsage(g.Key, DesignAttributionService.ModDisplayName(g.First().Mod), g.Select(x => x.DesignId).Distinct().Count()))
            .OrderByDescending(m => m.Count)
            .ToList();

        var sourceBreakdown = outfits.GroupBy(kv => kv.Value.Source)
            .Select(g => new SourceCount(g.Key,
                designProviders.FirstOrDefault(p => p.Source == g.Key)?.DisplayName ?? g.Key.ToString(),
                g.Count()))
            .OrderByDescending(s => s.Count)
            .ToList();

        DateEntry? oldest = null, newest = null;
        foreach (var (id, outfit) in outfits)
        {
            var created = outfit.CreatedAt ?? configuration.GetLocalCreatedAt(id);
            if (created is not { } value)
                continue;
            if (oldest is null || value < oldest.CreatedAt)
                oldest = new DateEntry(id, outfit.Name, value);
            if (newest is null || value > newest.CreatedAt)
                newest = new DateEntry(id, outfit.Name, value);
        }

        return new Report(outfits.Count, mostWorn, leastWorn, neverWorn, tagDistribution, modUsage, sourceBreakdown, oldest, newest);
    }
}
