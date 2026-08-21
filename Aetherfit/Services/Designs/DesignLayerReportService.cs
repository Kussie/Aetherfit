using System;
using System.Collections.Generic;
using System.Linq;

namespace Aetherfit.Services.Designs;

public sealed class DesignLayerReportService
{
    private readonly Plugin plugin;

    public DesignLayerReportService(Plugin plugin) => this.plugin = plugin;

    public sealed record LayerRef(Guid DesignId, string Name);

    // One "Layer N" row: usually one design, or several when it's a random-pick slot.
    public sealed record LayerSlotInfo(IReadOnlyList<LayerRef> Designs);

    public sealed record ReportEntry(
        Guid Id,
        string Name,
        LayerRef? BaseDesignLayer,
        bool BaseDesignLayerIsOverride,
        IReadOnlyList<LayerSlotInfo> Before,
        IReadOnlyList<LayerSlotInfo> After,
        IReadOnlyList<LayerRef> UsedAsLayerBy)
    {
        public bool HasOwnLayers => BaseDesignLayer != null || Before.Count > 0 || After.Count > 0;

        public bool IsInheritOnlyBaseLayer
            => BaseDesignLayer != null && !BaseDesignLayerIsOverride && Before.Count == 0 && After.Count == 0;
    }

    public List<ReportEntry> BuildReport()
    {
        var cfg = plugin.Configuration;
        var entries = new Dictionary<Guid, ReportEntry>();
        var usedBy = new Dictionary<Guid, List<LayerRef>>();

        LayerRef ToRef(Guid id) => new(id, cfg.ResolveDesignName(id));

        void TrackUsage(Guid layerDesignId, LayerRef user)
        {
            if (!usedBy.TryGetValue(layerDesignId, out var list))
                usedBy[layerDesignId] = list = new List<LayerRef>();
            list.Add(user);
        }

        foreach (var (id, outfit) in cfg.CachedOutfits)
        {
            var baseLayerId = cfg.ResolveBaseDesignLayer(id);
            var slots = cfg.GetLayerSlots(id);
            var self = new LayerRef(id, outfit.Name);

            LayerSlotInfo ToSlotInfo(DesignLayerSlot slot)
            {
                var refs = slot.Designs.Select(d => ToRef(d.DesignId)).ToList();
                foreach (var r in refs)
                    TrackUsage(r.DesignId, self);
                return new LayerSlotInfo(refs);
            }

            var before = slots.Where(s => s.IsBefore).Select(ToSlotInfo).ToList();
            var after = slots.Where(s => !s.IsBefore).Select(ToSlotInfo).ToList();

            LayerRef? baseLayerRef = null;
            if (baseLayerId is { } bl)
            {
                baseLayerRef = ToRef(bl);
                TrackUsage(bl, self);
            }

            entries[id] = new ReportEntry(id, outfit.Name, baseLayerRef,
                cfg.DesignBaseLayerOverrides.ContainsKey(id), before, after, Array.Empty<LayerRef>());
        }

        return entries.Values
            .Select(e => usedBy.TryGetValue(e.Id, out var users)
                ? e with
                {
                    UsedAsLayerBy = users.DistinctBy(u => u.DesignId)
                        .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                }
                : e)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
