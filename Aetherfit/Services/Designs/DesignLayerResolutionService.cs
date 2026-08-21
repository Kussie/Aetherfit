using System;
using System.Collections.Generic;
using System.Linq;

namespace Aetherfit.Services.Designs;

// Works out, for a base design with layers configured, which design's value actually wins each
// equipment slot/customization/toggle once the full layer stack is composited - purely from cached
// data, no Glamourer calls. Mirrors DesignApplyService's real apply order exactly (Base Design Layer
// -> Before slots top-to-bottom -> the base design -> After slots top-to-bottom), so the edit view can
// show the effective value instead of just the base design's own.
public sealed class DesignLayerResolutionService
{
    private readonly Plugin plugin;

    public DesignLayerResolutionService(Plugin plugin) => this.plugin = plugin;

    // Which design's value won a field, and/or whether a random (2+ candidate) layer slot could still
    // contest it. Contested is independent of Winner - a field can have no concrete winner at all and
    // still be contested (never guess a specific value for a random pick). Job isn't a factor here:
    // this previews what's configured, not what your currently-live job happens to allow.
    public readonly record struct FieldSource(Guid SourceDesignId, bool IsBaseDesign);

    public readonly record struct FieldResolution(FieldSource? Winner, bool Contested = false, int ContestedCandidateCount = 0);

    public sealed class Result
    {
        // Keyed by EquipmentSlot; ItemSource/StainSource are independent (Apply vs ApplyStain).
        public Dictionary<EquipmentSlot, FieldResolution> ItemSource { get; } = new();
        public Dictionary<EquipmentSlot, FieldResolution> StainSource { get; } = new();
        // Keyed by bonus slot key (e.g. "Glasses").
        public Dictionary<string, FieldResolution> BonusItemSource { get; } = new();
        // Keyed by customization Key (e.g. "Hairstyle"). Union of every key any step in the chain applies.
        public Dictionary<string, FieldResolution> CustomizationSource { get; } = new();
        public FieldResolution HatVisibleSource { get; set; }
        public FieldResolution WeaponVisibleSource { get; set; }
        public FieldResolution VisorToggledSource { get; set; }

        // True as soon as there's anything at all to show (a resolvable base layer, or >=1 before/after
        // slot with at least one candidate) - lets callers cheaply skip everything else when a design
        // has no layers configured.
        public bool HasAnyLayers { get; init; }
    }

    // One step in the effective apply order: either a deterministic single design, or a "contested"
    // slot with several job-matching candidates that could each independently win a field.
    private readonly record struct Step(Guid? DesignId, IReadOnlyList<Guid> ContestedCandidates)
    {
        public static Step Deterministic(Guid id) => new(id, Array.Empty<Guid>());
        public static Step Contested(IReadOnlyList<Guid> candidates) => new(null, candidates);
        public bool IsContested => ContestedCandidates.Count > 0;
    }

    public Result Resolve(Guid baseDesignId)
    {
        if (!plugin.Configuration.EnableRandomLayers)
            return new Result { HasAnyLayers = false };

        var baseLayerId = plugin.Configuration.ResolveBaseDesignLayer(baseDesignId) is { } bl && plugin.DesignApply.SupportsLayers(bl)
            ? bl
            : (Guid?)null;

        var allSlots = plugin.Configuration.GetLayerSlots(baseDesignId);
        var beforeSteps = BuildSteps(allSlots.Where(s => s.IsBefore));
        var afterSteps = BuildSteps(allSlots.Where(s => !s.IsBefore));

        var hasAnyLayers = baseLayerId != null || beforeSteps.Count > 0 || afterSteps.Count > 0;
        if (!hasAnyLayers)
            return new Result { HasAnyLayers = false };

        var steps = new List<Step>(beforeSteps.Count + afterSteps.Count + 2);
        if (baseLayerId is { } baseLayer)
            steps.Add(Step.Deterministic(baseLayer));
        steps.AddRange(beforeSteps);
        steps.Add(Step.Deterministic(baseDesignId));
        steps.AddRange(afterSteps);

        var result = new Result { HasAnyLayers = true };
        ResolveEquipment(result, steps, baseDesignId);
        ResolveBonusItems(result, steps, baseDesignId);
        ResolveCustomizations(result, steps, baseDesignId);
        ResolveToggles(result, steps, baseDesignId);
        return result;
    }

    // One Step per DesignLayerSlot, in list order: 0 candidates contributes nothing (no step at all),
    // 1 is deterministic, 2+ is contested (never guessed - see FieldResolution). Job restrictions on a
    // slot's own designs aren't considered - this previews what's configured, not what your current
    // job happens to allow at apply time.
    private List<Step> BuildSteps(IEnumerable<DesignLayerSlot> slots)
    {
        var steps = new List<Step>();
        foreach (var slot in slots)
        {
            var candidates = slot.Designs
                .Where(l => plugin.DesignApply.SupportsLayers(l.DesignId))
                .Select(l => l.DesignId)
                .ToList();

            if (candidates.Count == 1)
                steps.Add(Step.Deterministic(candidates[0]));
            else if (candidates.Count > 1)
                steps.Add(Step.Contested(candidates));
        }
        return steps;
    }

    private FieldSource ToSource(Guid designId, Guid baseDesignId) => new(designId, designId == baseDesignId);

    private void ResolveEquipment(Result result, List<Step> steps, Guid baseDesignId)
    {
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
        {
            FieldSource? itemWinner = null, stainWinner = null;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].DesignId is not { } id) continue;
                if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)) continue;
                var entry = outfit.Equipment.FirstOrDefault(e => e.Slot == slot);
                if (entry == null) continue;

                if (entry.Apply) itemWinner = ToSource(id, baseDesignId);
                if (entry.ApplyStain) stainWinner = ToSource(id, baseDesignId);
            }

            result.ItemSource[slot] = ApplyContested(itemWinner, steps,
                candidateId => plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var o)
                    && o.Equipment.FirstOrDefault(e => e.Slot == slot)?.Apply == true);
            result.StainSource[slot] = ApplyContested(stainWinner, steps,
                candidateId => plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var o)
                    && o.Equipment.FirstOrDefault(e => e.Slot == slot)?.ApplyStain == true);
        }
    }

    private void ResolveBonusItems(Result result, List<Step> steps, Guid baseDesignId)
    {
        var bonusKeys = plugin.Configuration.CachedOutfits.TryGetValue(baseDesignId, out var baseOutfit)
            ? baseOutfit.BonusItems.Select(b => b.Slot).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (step.DesignId is { } id && plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit))
                foreach (var b in outfit.BonusItems)
                    bonusKeys.Add(b.Slot);
            foreach (var candidateId in step.ContestedCandidates)
                if (plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var cOutfit))
                    foreach (var b in cOutfit.BonusItems)
                        bonusKeys.Add(b.Slot);
        }

        foreach (var slotKey in bonusKeys)
        {
            FieldSource? winner = null;
            foreach (var step in steps)
            {
                if (step.DesignId is not { } id) continue;
                if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)) continue;
                var entry = outfit.BonusItems.FirstOrDefault(b => b.Slot == slotKey);
                if (entry is { Apply: true })
                    winner = ToSource(id, baseDesignId);
            }

            result.BonusItemSource[slotKey] = ApplyContested(winner, steps,
                candidateId => plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var o)
                    && o.BonusItems.FirstOrDefault(b => b.Slot == slotKey)?.Apply == true);
        }
    }

    private void ResolveCustomizations(Result result, List<Step> steps, Guid baseDesignId)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (step.DesignId is { } id && plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit))
                foreach (var c in outfit.Customizations)
                    keys.Add(c.Key);
            foreach (var candidateId in step.ContestedCandidates)
                if (plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var cOutfit))
                    foreach (var c in cOutfit.Customizations)
                        keys.Add(c.Key);
        }

        foreach (var key in keys)
        {
            FieldSource? winner = null;
            foreach (var step in steps)
            {
                if (step.DesignId is not { } id) continue;
                if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)) continue;
                if (outfit.Customizations.Any(c => c.Key == key))
                    winner = ToSource(id, baseDesignId);
            }

            result.CustomizationSource[key] = ApplyContested(winner, steps,
                candidateId => plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var o)
                    && o.Customizations.Any(c => c.Key == key));
        }
    }

    private void ResolveToggles(Result result, List<Step> steps, Guid baseDesignId)
    {
        result.HatVisibleSource = ResolveToggle(steps, baseDesignId, o => o.HatVisible);
        result.WeaponVisibleSource = ResolveToggle(steps, baseDesignId, o => o.WeaponVisible);
        result.VisorToggledSource = ResolveToggle(steps, baseDesignId, o => o.VisorToggled);
    }

    private FieldResolution ResolveToggle(List<Step> steps, Guid baseDesignId, Func<CachedOutfit, bool?> selector)
    {
        FieldSource? winner = null;
        foreach (var step in steps)
        {
            if (step.DesignId is not { } id) continue;
            if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)) continue;
            if (selector(outfit) != null)
                winner = ToSource(id, baseDesignId);
        }

        return ApplyContested(winner, steps,
            candidateId => plugin.Configuration.CachedOutfits.TryGetValue(candidateId, out var o) && selector(o) != null);
    }

    // A contested slot can only actually flip a field's displayed winner if no later deterministic
    // step conclusively re-manages that field afterward - if one does, the contested slot's
    // candidates are moot for that field regardless of which might be rolled, so no caveat is shown.
    private static FieldResolution ApplyContested(FieldSource? winner, List<Step> steps, Func<Guid, bool> candidateManagesField)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (!step.IsContested) continue;
            if (!step.ContestedCandidates.Any(candidateManagesField)) continue;

            var laterStepManages = false;
            for (var j = i + 1; j < steps.Count; j++)
            {
                if (steps[j].DesignId is { } laterId && candidateManagesField(laterId))
                {
                    laterStepManages = true;
                    break;
                }
            }
            if (laterStepManages) continue;

            return new FieldResolution(winner, Contested: true, ContestedCandidateCount: step.ContestedCandidates.Count);
        }

        return new FieldResolution(winner);
    }
}
