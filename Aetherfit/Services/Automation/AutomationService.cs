using System;
using System.Collections.Generic;
using System.Linq;
using Aetherfit.Services.Game;
using Aetherfit.Utils;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

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

    private DateTime? snoozeUntilUtc;
    private ulong snoozedContentId;

    public Guid? CurrentRuleId { get; private set; }
    public string? CurrentRuleName { get; private set; }

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

    public bool IsSnoozed => SnoozeRemaining != null;

    public void StartSnooze(TimeSpan duration)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;
        snoozedContentId = Plugin.PlayerState.ContentId;
        snoozeUntilUtc = DateTime.UtcNow + duration;
    }

    public void CancelSnooze() => snoozeUntilUtc = null;

    public AutomationService(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.Glamourer.OnAnyStateFinalized += OnGlamourerActivity;
    }

    public void Dispose() => plugin.Glamourer.OnAnyStateFinalized -= OnGlamourerActivity;

    private void OnGlamourerActivity(nint actor, Glamourer.Api.Enums.StateFinalizationType type)
        => lastGlamourerActivityUtc = DateTime.UtcNow;

    public void OnFrameworkTick(IFramework framework)
    {
        if (DateTime.UtcNow < nextPollUtc)
            return;
        nextPollUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        if (!Plugin.PlayerState.IsLoaded)
        {
            CurrentRuleId = null;
            CurrentRuleName = null;
            PendingSuggestion = null;
            return;
        }

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        if (!settings.AutomationsEnabled)
        {
            CurrentRuleId = null;
            CurrentRuleName = null;
            PendingSuggestion = null;
            lastAppliedRuleId = null;
            return;
        }

        var ctx = BuildMatchContext();

        var (rule, pick) = EvaluateRules(settings, ctx, AutomationRuleMode.AutoApply);
        CurrentRuleId = rule?.Id;
        CurrentRuleName = rule?.Name;

        if (rule != null && pick != null && rule.Id != lastAppliedRuleId && CanApplyNow(settings))
        {
            lastAppliedRuleId = rule.Id;
            BeginApply(pick.Value);
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
            Plugin.ToastGui.ShowNormal($"Automations: now wearing \"{plugin.Configuration.ResolveDesignName(designId)}\"");
        }, TimeSpan.FromSeconds(1));
    }

    private readonly record struct MatchContext(uint JobId, uint TerritoryId, bool Mounted, ushort MountId,
        byte WeatherId, int EorzeaHour, int ServerHour, int LocalHour, bool Swimming, bool Diving,
        GameDataService.HousingState Housing, GroupType GroupType, uint OnlineStatusId);

    private MatchContext BuildMatchContext()
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
            OnlineStatusId: Plugin.ObjectTable.LocalPlayer?.OnlineStatus.RowId ?? 0);
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
        var usable = candidates.Where(plugin.DesignApply.IsUsable).ToList();
        if (usable.Count == 0)
            return null;

        var jobSpecific = usable.Where(id => plugin.Configuration.GetJobAssociations(id).Contains(jobId)).ToList();
        var jobAgnostic = usable.Where(id => plugin.Configuration.GetJobAssociations(id).Count == 0).ToList();

        var pool = jobSpecific.Count > 0 ? jobSpecific : jobAgnostic;
        return pool.Count == 0 ? null : plugin.DesignApply.PickRandomDesign(pool);
    }

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

        var hasUsableDesign = rule.DesignIds.Any(plugin.DesignApply.IsUsable);
        var hasUsableTagPool = rule.TagPools.Any(p => ResolveTagPool(p).Any(plugin.DesignApply.IsUsable));
        if (!hasUsableDesign && !hasUsableTagPool)
            issues.Add("No usable designs assigned - nothing to apply even if this rule matches.");

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
        _ => false,
    };

    // "Would this rule match right now, on its own" - for the rule editor's dry-run preview. Independent
    // of rule order/other rules and of Enabled, so a disabled rule can still be previewed before turning it on.
    public readonly record struct RulePreview(bool WouldApply, List<(AutomationCondition Condition, bool Matches)> ConditionResults);

    public RulePreview PreviewRule(AutomationRule rule)
    {
        var ctx = BuildMatchContext();
        var results = rule.Conditions.Select(c => (c, Matches(c, ctx))).ToList();
        var wouldApply = results.Count > 0 && results.All(r => r.Item2) && PickDesign(rule, ctx.JobId) != null;
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
