using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;
using Aetherfit.Utils;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Newtonsoft.Json.Linq;

namespace Aetherfit.Services.Automation;

// Polls the current character's automation rules roughly once a second and applies the first matching
// one's design pick, gated on combat/duty state, a manual snooze, and a short "Glamourer has gone quiet"
// wait (same shape as RestoreSequenceService's own settle wait, just simpler - there's no login/zone
// retry logic here).
public sealed class AutomationService : IDisposable
{
    private readonly Plugin plugin;

    private DateTime nextPollUtc = DateTime.MinValue;
    private DateTime lastGlamourerActivityUtc = DateTime.MinValue;
    private Guid? lastAppliedRuleId;
    // Bumped whenever a new quiet-wait starts so a stale RunOnTick chain from a since-superseded rule falls out silently.
    private int generation;

    private Guid? lastDisabledRuleId;
    // Independent of `generation` - a Disable rule's wait-for-quiet chain must never be cancelled by an
    // unrelated AutoApply rule's chain starting, since both can legitimately be in flight at once.
    private int disableGeneration;

    private DateTime? snoozeUntilUtc;
    private ulong snoozedContentId;
    private bool snoozeUntilZoneChange;
    private uint? snoozeFromJobId;

    // Loop guard for Equipment conditions - a design applied BY automation changes gear, which could
    // otherwise satisfy a different Equipment rule and chain forever. See DetectEquipmentChange.
    private Dictionary<EquipmentSlot, ulong>? lastSeenEquipment;
    private DateTime equipmentSuppressedUntilUtc = DateTime.MinValue;
    private static readonly IReadOnlyDictionary<EquipmentSlot, ulong> EmptyEquipment = new Dictionary<EquipmentSlot, ulong>();

    public Guid? CurrentRuleId { get; private set; }
    public string? CurrentRuleName { get; private set; }
    public Guid? CurrentDisableRuleId { get; private set; }
    public string? CurrentDisableRuleName { get; private set; }

    public readonly record struct Suggestion(Guid RuleId, string RuleName, Guid DesignId);

    // 30 minutes, fixed - a dismissed suggestion (explicit Dismiss or the popup's own auto-timeout)
    // won't reappear before then even if the rule's conditions are still true next poll.
    private static readonly TimeSpan SuggestionDismissCooldown = TimeSpan.FromMinutes(30);
    private readonly Dictionary<Guid, DateTime> suggestionDismissedUntilUtc = new();

    public Suggestion? PendingSuggestion { get; private set; }

    // Both an explicit Dismiss click and the popup's own auto-timeout call this - either way it's a
    // "don't ask again for a while" signal. Applying the suggestion doesn't call this at all; the
    // suggestion clears itself next poll because the applied design becomes settings.LastWornDesign.
    public void DismissSuggestion(Guid ruleId)
    {
        suggestionDismissedUntilUtc[ruleId] = DateTime.UtcNow + SuggestionDismissCooldown;
        if (PendingSuggestion?.RuleId == ruleId)
            PendingSuggestion = null;
    }

    // Null once expired or cancelled, not just clamped to zero - callers shouldn't have to also check
    // "is this actually still in the future" themselves.
    public TimeSpan? SnoozeRemaining
    {
        get
        {
            if (snoozeUntilUtc is not { } until || !Plugin.PlayerState.IsLoaded || snoozedContentId != Plugin.PlayerState.ContentId)
                return null;
            var remaining = until - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    // True for a timer snooze still counting down, or either event snooze - those end on their own
    // event rather than a deadline, so there's no remaining time to check.
    public bool IsSnoozed
    {
        get
        {
            if (!Plugin.PlayerState.IsLoaded || snoozedContentId != Plugin.PlayerState.ContentId)
                return false;
            return snoozeUntilZoneChange || snoozeFromJobId != null || SnoozeRemaining != null;
        }
    }

    // Null unless an event snooze (not a timer one) is active - what IsSnoozed is waiting on, for UI
    // that can't show a countdown for it.
    public string? SnoozeEventDescription
    {
        get
        {
            if (!IsSnoozed)
                return null;
            if (snoozeUntilZoneChange)
                return "next zone change";
            if (snoozeFromJobId != null)
                return "next job change";
            return null;
        }
    }

    public void StartSnooze(TimeSpan duration)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;
        ResetSnoozeState();
        snoozedContentId = Plugin.PlayerState.ContentId;
        snoozeUntilUtc = DateTime.UtcNow + duration;
    }

    public void StartSnoozeUntilZoneChange()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;
        ResetSnoozeState();
        snoozedContentId = Plugin.PlayerState.ContentId;
        snoozeUntilZoneChange = true;
    }

    public void StartSnoozeUntilJobChange()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;
        ResetSnoozeState();
        snoozedContentId = Plugin.PlayerState.ContentId;
        snoozeFromJobId = Plugin.PlayerState.ClassJob.RowId;
    }

    public void CancelSnooze() => ResetSnoozeState();

    private void ResetSnoozeState()
    {
        snoozeUntilUtc = null;
        snoozeUntilZoneChange = false;
        snoozeFromJobId = null;
    }

    public AutomationService(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.Glamourer.OnAnyStateFinalized += OnGlamourerActivity;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChangedForSnooze;
    }

    public void Dispose()
    {
        plugin.Glamourer.OnAnyStateFinalized -= OnGlamourerActivity;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChangedForSnooze;
    }

    private void OnGlamourerActivity(nint actor, Glamourer.Api.Enums.StateFinalizationType type)
        => lastGlamourerActivityUtc = DateTime.UtcNow;

    private void OnTerritoryChangedForSnooze(uint territoryId)
    {
        if (snoozeUntilZoneChange)
            CancelSnooze();
    }

    public void OnFrameworkTick(IFramework framework)
    {
        if (snoozeFromJobId is { } startJobId && Plugin.PlayerState.IsLoaded && Plugin.PlayerState.ClassJob.RowId != startJobId)
            CancelSnooze();

        if (DateTime.UtcNow < nextPollUtc)
            return;
        nextPollUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        if (!Plugin.PlayerState.IsLoaded)
        {
            CurrentRuleId = null;
            CurrentRuleName = null;
            CurrentDisableRuleId = null;
            CurrentDisableRuleName = null;
            PendingSuggestion = null;
            return;
        }

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        if (!settings.AutomationsEnabled)
        {
            CurrentRuleId = null;
            CurrentRuleName = null;
            CurrentDisableRuleId = null;
            CurrentDisableRuleName = null;
            PendingSuggestion = null;
            lastAppliedRuleId = null;
            lastDisabledRuleId = null;
            return;
        }

        var needsEquipment = settings.AutomationRules.Any(RuleUsesEquipment);
        var ctx = BuildMatchContext(needsEquipment);
        var equipmentChanged = needsEquipment && DetectEquipmentChange(ctx.CurrentEquipment);

        var (rule, pick) = EvaluateRules(settings, ctx, AutomationRuleMode.AutoApply);
        CurrentRuleId = rule?.Id;
        CurrentRuleName = rule?.Name;

        if (rule != null && pick != null && ShouldFireRule(rule, lastAppliedRuleId, equipmentChanged) && CanApplyNow(settings))
        {
            lastAppliedRuleId = rule.Id;
            BeginApply(pick.Value);
        }

        var (disableRule, disableIds) = EvaluateDisableRules(settings, ctx);
        CurrentDisableRuleId = disableRule?.Id;
        CurrentDisableRuleName = disableRule?.Name;

        if (disableRule != null && disableIds != null && ShouldFireRule(disableRule, lastDisabledRuleId, equipmentChanged) && CanApplyNow(settings))
        {
            lastDisabledRuleId = disableRule.Id;
            BeginDisable(disableIds);
        }

        EvaluateSuggestion(settings, ctx);
    }

    private void EvaluateSuggestion(CharacterLoginSettings settings, MatchContext ctx)
    {
        var (rule, designId) = EvaluateRules(settings, ctx, AutomationRuleMode.Suggest);
        if (rule == null || designId == null || designId == settings.LastWornDesign)
        {
            PendingSuggestion = null;
            return;
        }

        if (suggestionDismissedUntilUtc.TryGetValue(rule.Id, out var until) && DateTime.UtcNow < until)
        {
            PendingSuggestion = null;
            return;
        }

        PendingSuggestion = new Suggestion(rule.Id, rule.Name, designId.Value);
    }

    // Combat blocks unconditionally - there's no setting to override it, since changing appearance
    // mid-fight is never something Automations should do regardless of what a rule matches.
    private bool CanApplyNow(CharacterLoginSettings settings)
    {
        if (IsSnoozed)
            return false;

        if (Plugin.Condition[ConditionFlag.InCombat])
            return false;

        if (settings.AutomationsDisableInDuties && (Plugin.Condition[ConditionFlag.BoundByDuty]
                || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95]))
            return false;

        return true;
    }

    private void BeginApply(Guid designId)
    {
        var gen = ++generation;
        WaitForQuiet(gen, DateTime.UtcNow + TimeSpan.FromSeconds(10), designId);
    }

    private void WaitForQuiet(int gen, DateTime deadlineUtc, Guid designId)
    {
        Plugin.Framework.RunOnTick(() =>
        {
            if (gen != generation || !Plugin.ClientState.IsLoggedIn)
                return;

            var quiet = DateTime.UtcNow - lastGlamourerActivityUtc >= TimeSpan.FromSeconds(2);
            if (!quiet && DateTime.UtcNow < deadlineUtc)
            {
                WaitForQuiet(gen, deadlineUtc, designId);
                return;
            }

            if (!Plugin.PlayerState.IsLoaded
                || !plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
                || !settings.AutomationsEnabled || !CanApplyNow(settings))
                return;

            plugin.DesignApply.ApplyDesignById(designId);
            // Absorb whatever gear this apply itself changes as the new baseline - see DetectEquipmentChange.
            equipmentSuppressedUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            Plugin.ToastGui.ShowNormal($"Automations: now wearing \"{plugin.Configuration.ResolveDesignName(designId)}\"");
        }, TimeSpan.FromSeconds(1));
    }

    private void BeginDisable(List<Guid> designIds)
    {
        var gen = ++disableGeneration;
        WaitForQuietDisable(gen, DateTime.UtcNow + TimeSpan.FromSeconds(10), designIds);
    }

    private void WaitForQuietDisable(int gen, DateTime deadlineUtc, List<Guid> designIds)
    {
        Plugin.Framework.RunOnTick(() =>
        {
            if (gen != disableGeneration || !Plugin.ClientState.IsLoggedIn)
                return;

            var quiet = DateTime.UtcNow - lastGlamourerActivityUtc >= TimeSpan.FromSeconds(2);
            if (!quiet && DateTime.UtcNow < deadlineUtc)
            {
                WaitForQuietDisable(gen, deadlineUtc, designIds);
                return;
            }

            if (!Plugin.PlayerState.IsLoaded
                || !plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
                || !settings.AutomationsEnabled || !CanApplyNow(settings))
                return;

            // Caught per-design, not around the whole loop - one design's failure shouldn't also skip
            // the rest of the list or the toast below.
            foreach (var id in designIds)
            {
                try
                {
                    plugin.DesignApply.DisableDesign(id, quiet: true);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Automations: failed to disable \"{Name}\"", plugin.Configuration.ResolveDesignName(id));
                }
            }

            // Absorb whatever gear this itself clears as the new baseline - see DetectEquipmentChange.
            equipmentSuppressedUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            Plugin.ToastGui.ShowNormal($"Automations: disabled \"{string.Join(", ", designIds.Select(plugin.Configuration.ResolveDesignName))}\"");
        }, TimeSpan.FromSeconds(1));
    }

    private readonly record struct MatchContext(uint JobId, uint TerritoryId, bool Mounted, ushort MountId,
        byte WeatherId, int EorzeaHour, int ServerHour, int LocalHour, bool Swimming, bool Diving,
        GameDataService.HousingState Housing, GroupType GroupType, uint OnlineStatusId, Guid ActivePersonaId,
        IReadOnlyDictionary<EquipmentSlot, ulong> CurrentEquipment);

    // needsEquipment defaults to true for PreviewRule's one-off manual call - OnFrameworkTick passes its
    // own computed value so the extra Glamourer read only happens for characters that actually use it.
    private MatchContext BuildMatchContext(bool needsEquipment = true)
    {
        var mounted = Plugin.Condition[ConditionFlag.Mounted];
        return new MatchContext(
            JobId: Plugin.PlayerState.ClassJob.RowId,
            TerritoryId: Plugin.ClientState.TerritoryType,
            Mounted: mounted,
            MountId: mounted ? plugin.GameData.GetCurrentMountId() : (ushort)0,
            WeatherId: plugin.GameData.GetCurrentWeatherId(),
            EorzeaHour: plugin.GameData.GetCurrentEorzeaHour(),
            ServerHour: plugin.GameData.GetCurrentServerHour(),
            LocalHour: plugin.GameData.GetCurrentLocalHour(),
            Swimming: Plugin.Condition[ConditionFlag.Swimming],
            Diving: Plugin.Condition[ConditionFlag.Diving],
            Housing: plugin.GameData.GetCurrentHousingState(),
            GroupType: ResolveGroupType(),
            OnlineStatusId: Plugin.ObjectTable.LocalPlayer?.OnlineStatus.RowId ?? 0,
            ActivePersonaId: plugin.Configuration.GetActivePersonaForCurrentCharacter()?.Id ?? Guid.Empty,
            CurrentEquipment: needsEquipment ? GetCurrentEquipment() : EmptyEquipment);
    }

    private IReadOnlyDictionary<EquipmentSlot, ulong> GetCurrentEquipment()
    {
        var (result, state) = plugin.Glamourer.GetState();
        if (result != GlamourerApiEc.Success || state == null)
            return EmptyEquipment;
        return GlamourerService.ParseEquipment(state["Equipment"] as JObject).ToDictionary(s => s.Slot, s => s.ItemId);
    }

    private static bool RuleUsesEquipment(AutomationRule rule)
        => rule.Conditions.Any(c => c.Type == AutomationConditionType.Equipment);

    // For an Equipment-conditioned rule, "already fired, don't refire" has to mean "already fired for
    // this exact gear transition" (equipmentChanged - a single-poll pulse, see DetectEquipmentChange),
    // not "the winning rule id is unchanged since last time" - otherwise the same rule could only ever
    // fire once, since putting the same shirt on a second time still resolves to the same one rule.
    private static bool ShouldFireRule(AutomationRule rule, Guid? lastFiredRuleId, bool equipmentChanged)
        => RuleUsesEquipment(rule) ? equipmentChanged : rule.Id != lastFiredRuleId;

    // See lastSeenEquipment/equipmentSuppressedUntilUtc - only called from the real poll (never from
    // PreviewRule), since a preview click must never mutate this loop-guard bookkeeping.
    private bool DetectEquipmentChange(IReadOnlyDictionary<EquipmentSlot, ulong> current)
    {
        var suppressed = DateTime.UtcNow < equipmentSuppressedUntilUtc;
        var changed = !suppressed && lastSeenEquipment != null && !EquipmentEquals(lastSeenEquipment, current);
        lastSeenEquipment = new Dictionary<EquipmentSlot, ulong>(current);
        return changed;
    }

    private static bool EquipmentEquals(Dictionary<EquipmentSlot, ulong> a, IReadOnlyDictionary<EquipmentSlot, ulong> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (slot, itemId) in a)
            if (!b.TryGetValue(slot, out var otherId) || otherId != itemId)
                return false;
        return true;
    }

    // Party/Raid boundary matches what "raid comp" colloquially means (a full 8-man party) rather than
    // any game-exposed threshold - Dalamud's party list only ever gives us Length + IsAlliance.
    private static GroupType ResolveGroupType()
    {
        if (Plugin.PartyList.IsAlliance)
            return GroupType.Alliance;

        return Plugin.PartyList.Length switch
        {
            0 => GroupType.Solo,
            <= 4 => GroupType.Party,
            _ => GroupType.Raid,
        };
    }

    private (AutomationRule? Rule, Guid? DesignId) EvaluateRules(CharacterLoginSettings settings, MatchContext ctx, AutomationRuleMode mode)
    {
        foreach (var rule in settings.AutomationRules)
        {
            // A rule with no conditions at all would otherwise match unconditionally (Conditions.All on
            // an empty list is vacuously true) - require at least one, same as requiring a usable design below.
            if (!rule.Enabled || rule.Mode != mode || rule.Conditions.Count == 0)
                continue;
            if (!rule.Conditions.All(c => Matches(c, ctx)))
                continue;

            // A matching rule with nothing usable for the current job isn't a match after all - keep looking.
            if (PickDesign(rule, ctx.JobId) is { } designId)
                return (rule, designId);
        }

        return (null, null);
    }

    private static bool Matches(AutomationCondition c, MatchContext ctx) => c.Type switch
    {
        AutomationConditionType.Job => c.JobIds.Contains(ctx.JobId),
        AutomationConditionType.Territory => c.TerritoryIds.Contains(ctx.TerritoryId),
        AutomationConditionType.Mounted => c.MountedValue
            ? ctx.Mounted && (c.MountIds.Count == 0 || c.MountIds.Contains(ctx.MountId))
            : !ctx.Mounted,
        AutomationConditionType.Weather => c.WeatherIds.Contains(ctx.WeatherId),
        AutomationConditionType.Time => InTimeRange(c.StartHour, c.EndHour, ctx.EorzeaHour),
        AutomationConditionType.ServerTime => InTimeRange(c.StartHour, c.EndHour, ctx.ServerHour),
        AutomationConditionType.LocalTime => InTimeRange(c.StartHour, c.EndHour, ctx.LocalHour),
        AutomationConditionType.Swimming => (c.SwimStates.Contains(SwimState.Swimming) && ctx.Swimming)
            || (c.SwimStates.Contains(SwimState.Diving) && ctx.Diving),
        AutomationConditionType.Housing => ctx.Housing.InHousing && c.HousingTargets.Any(t => MatchesHousing(t, ctx.Housing)),
        AutomationConditionType.Group => c.GroupTypes.Contains(ctx.GroupType),
        AutomationConditionType.OnlineStatus => c.OnlineStatuses.Any(s => (uint)s == ctx.OnlineStatusId),
        AutomationConditionType.ActivePersona => c.PersonaIds.Contains(ctx.ActivePersonaId),
        AutomationConditionType.Equipment => c.EquipmentMode == EquipmentConditionMode.Empty
            ? !ctx.CurrentEquipment.TryGetValue(c.EquipmentSlot, out var emptyCheck) || GameDataService.IsEmptySlotItem(emptyCheck)
            : ctx.CurrentEquipment.TryGetValue(c.EquipmentSlot, out var heldItem) && !GameDataService.IsEmptySlotItem(heldItem)
                && (c.EquipmentItemIds.Count == 0 || c.EquipmentItemIds.Contains(heldItem)),
        _ => false,
    };

    private static bool MatchesHousing(HousingTarget target, GameDataService.HousingState housing) => target.Type switch
    {
        HousingTargetType.AnyHouse => !housing.IsApartment,
        HousingTargetType.AnyApartment => housing.IsApartment,
        HousingTargetType.SpecificPlot => !housing.IsApartment && housing.TerritoryTypeId == target.TerritoryTypeId
            && housing.Ward == target.Ward && housing.Plot == target.Plot,
        HousingTargetType.SpecificApartmentRoom => housing.IsApartment && housing.TerritoryTypeId == target.TerritoryTypeId
            && housing.Ward == target.Ward && housing.Room == target.Room,
        _ => false,
    };

    private static bool InTimeRange(int start, int end, int hour)
        => end <= start ? hour >= start || hour < end : hour >= start && hour < end;

    // Job-specific matches win over job-agnostic ones; a design explicitly for a different job is never picked.
    private Guid? PickDesign(AutomationRule rule, uint jobId)
    {
        var candidates = rule.DesignIds.Concat(rule.TagPools.SelectMany(ResolveTagPool)).Distinct();
        var usable = candidates.Where(plugin.DesignApply.IsUsableForAutomation).ToList();
        if (usable.Count == 0)
            return null;

        var jobSpecific = usable.Where(id => plugin.Configuration.GetJobAssociations(id).Contains(jobId)).ToList();
        var jobAgnostic = usable.Where(id => plugin.Configuration.GetJobAssociations(id).Count == 0).ToList();

        var pool = jobSpecific.Count > 0 ? jobSpecific : jobAgnostic;
        return pool.Count == 0 ? null : plugin.DesignApply.PickRandomDesign(pool);
    }

    // Parallel to EvaluateRules, but Disable acts on every resolved design at once (see
    // ResolveDisableTargets) rather than picking one, so it has its own evaluation loop.
    private (AutomationRule? Rule, List<Guid>? DesignIds) EvaluateDisableRules(CharacterLoginSettings settings, MatchContext ctx)
    {
        foreach (var rule in settings.AutomationRules)
        {
            if (!rule.Enabled || rule.Mode != AutomationRuleMode.Disable || rule.Conditions.Count == 0)
                continue;
            if (!rule.Conditions.All(c => Matches(c, ctx)))
                continue;

            var targets = ResolveDisableTargets(rule);
            if (targets.Count > 0)
                return (rule, targets);
        }

        return (null, null);
    }

    // Unlike PickDesign, acts on every resolved design at once (not a random pick) with no job filtering -
    // undoing an accessory shouldn't depend on what job you're currently on.
    private List<Guid> ResolveDisableTargets(AutomationRule rule)
        => rule.DesignIds.Concat(rule.TagPools.SelectMany(ResolveTagPool))
            .Distinct()
            .Where(plugin.DesignApply.IsUsableForAutomation)
            .ToList();

    // Resolved fresh every poll rather than stored as fixed ids, so the pool follows tag edits automatically.
    private IEnumerable<Guid> ResolveTagPool(List<string> tags)
        => tags.Count == 0
            ? Enumerable.Empty<Guid>()
            : plugin.Configuration.CachedOutfits
                .Where(kv => tags.All(t => TagMatching.AnyMatch(kv.Value.Tags, t)))
                .Select(kv => kv.Key);

    // Standing configuration problems - not whether the rule currently matches (see PreviewRule), just
    // whether it's set up in a way that could ever do anything useful.
    public List<string> GetRuleIssues(AutomationRule rule)
    {
        var issues = new List<string>();

        if (rule.Conditions.Count == 0)
            issues.Add("No conditions set - this rule will never run. Add at least one condition.");

        foreach (var condition in rule.Conditions)
        {
            if (IsConditionEmpty(condition))
                issues.Add($"{condition.Type} condition has nothing selected - this rule can never match.");
        }

        var deletedDesigns = rule.DesignIds.Count(id => !plugin.Configuration.CachedOutfits.ContainsKey(id));
        if (deletedDesigns > 0)
            issues.Add($"{deletedDesigns} assigned design(s) no longer exist.");

        var hasUsableDesign = rule.DesignIds.Any(plugin.DesignApply.IsUsableForAutomation);
        var hasUsableTagPool = rule.TagPools.Any(p => ResolveTagPool(p).Any(plugin.DesignApply.IsUsableForAutomation));
        if (!hasUsableDesign && !hasUsableTagPool)
        {
            var action = rule.Mode == AutomationRuleMode.Disable ? "disable" : "apply";
            issues.Add($"No usable designs assigned - nothing to {action} even if this rule matches.");
        }

        foreach (var pool in rule.TagPools.Where(p => !ResolveTagPool(p).Any()))
            issues.Add($"Tag pool \"{string.Join(" + ", pool)}\" currently matches no designs.");

        return issues;
    }

    // Mounted and the three time conditions are always well-defined even at their default values
    // (MountedValue alone is meaningful; StartHour == EndHour == 0 means "all day", not "never") -
    // every other condition type needs at least one selected value to ever match anything.
    private static bool IsConditionEmpty(AutomationCondition c) => c.Type switch
    {
        AutomationConditionType.Job => c.JobIds.Count == 0,
        AutomationConditionType.Territory => c.TerritoryIds.Count == 0,
        AutomationConditionType.Weather => c.WeatherIds.Count == 0,
        AutomationConditionType.Swimming => c.SwimStates.Count == 0,
        AutomationConditionType.Housing => c.HousingTargets.Count == 0,
        AutomationConditionType.Group => c.GroupTypes.Count == 0,
        AutomationConditionType.OnlineStatus => c.OnlineStatuses.Count == 0,
        AutomationConditionType.ActivePersona => c.PersonaIds.Count == 0,
        _ => false,
    };

    // "Would this rule match right now, on its own" - for the rule editor's dry-run preview. Independent
    // of rule order/other rules and of Enabled, so a disabled rule can still be previewed before turning it on.
    public readonly record struct RulePreview(bool WouldApply, List<(AutomationCondition Condition, bool Matches)> ConditionResults);

    public RulePreview PreviewRule(AutomationRule rule)
    {
        var ctx = BuildMatchContext();
        var results = rule.Conditions.Select(c => (c, Matches(c, ctx))).ToList();
        var hasTarget = rule.Mode == AutomationRuleMode.Disable
            ? ResolveDisableTargets(rule).Count > 0
            : PickDesign(rule, ctx.JobId) != null;
        var wouldApply = results.Count > 0 && results.All(r => r.Item2) && hasTarget;
        return new RulePreview(wouldApply, results);
    }

    // Returns the new enabled state, or null if nobody's logged in. Used by both the hotkey and the UI toggle.
    public bool? ToggleEnabled()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return null;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        return SetEnabled(!settings.AutomationsEnabled);
    }

    // Explicit on/off (as opposed to ToggleEnabled's flip) - used by the "/aetherfit automations on|off" command.
    public bool? SetEnabled(bool enabled)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return null;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        settings.AutomationsEnabled = enabled;
        plugin.Configuration.Save();
        return settings.AutomationsEnabled;
    }
}
