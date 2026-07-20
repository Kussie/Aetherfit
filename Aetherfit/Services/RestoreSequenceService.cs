using System;
using Glamourer.Api.Enums;

namespace Aetherfit.Services;

// Owns the "wait for the world to settle, then (re)apply a design" sequences that run after login and
// after zone changes, plus the grace-window bookkeeping that decides whether a Glamourer finalization
// is the tail end of our own apply, a trigger to retry, or an external change that invalidates the
// last-worn record.
public sealed class RestoreSequenceService
{
    private enum Phase
    {
        // Nothing running; finalizations are judged against the post-restore grace window only.
        Idle,
        // Polling until the local player object exists — PlayerState loads before the character
        // spawns, and Glamourer needs the actual object (applying earlier returns ActorNotFound).
        WaitingForPlayer,
        // Player exists; waiting for Glamourer to go quiet before running the continuation.
        WaitingForQuiet,
    }

    private readonly Plugin plugin;

    private Phase phase = Phase.Idle;
    private bool isLoginFlow;
    // Bumped whenever a new sequence starts so stale RunOnTick chains fall out silently.
    private int generation;
    private int retriesLeft;
    private Action? continuation;
    private DateTime lastGlamourerActivityUtc;
    private DateTime graceUntilUtc = DateTime.MinValue;

    public RestoreSequenceService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void BeginLoginRestore()
    {
        // Slow logins (heavy mod loads) can keep Glamourer busy for a long time, so be generous.
        Begin(RunLoginAction, isLogin: true, playerAttemptsLeft: 120, quietDeadline: TimeSpan.FromSeconds(60));
    }

    public void HandleTerritoryChanged()
    {
        if (phase != Phase.Idle && isLoginFlow)
            return;

        if (!Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            return;

        // Read-only lookup on purpose: don't create/save settings from an event firing every zone.
        if (!plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            || !settings.ReapplyOnZoneChange
            || settings.LastWornDesign == null)
            return;

        // A new TerritoryChanged means a new load: start the sequence over.
        Begin(RunZoneReapply, isLogin: false, playerAttemptsLeft: 60, quietDeadline: TimeSpan.FromSeconds(30));
    }

    // Sees every finalization except the synchronous echo of our own IPC call.
    public void HandleAnyStateFinalized(nint actor, StateFinalizationType type)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || actor != localPlayer.Address)
            return;

        if (phase != Phase.Idle)
        {
            lastGlamourerActivityUtc = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow >= graceUntilUtc || retriesLeft <= 0)
            return;

        retriesLeft--;
        Plugin.Log.Info($"Glamourer finalized state ({type}) right after the restore action; re-applying once it settles.");
        phase = Phase.WaitingForQuiet;
        lastGlamourerActivityUtc = DateTime.UtcNow;
        continuation = RetryRestore;
        WaitForQuiet(++generation, deadlineUtc: DateTime.UtcNow + TimeSpan.FromSeconds(30));
    }

    public void HandleExternalStateFinalized(nint actor, StateFinalizationType type)
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || actor != localPlayer.Address)
            return;

        if (phase != Phase.Idle || DateTime.UtcNow < graceUntilUtc)
            return;

        if (type != StateFinalizationType.DesignApplied)
            return;

        // A design we didn't apply landed on the character, so the last-worn record no longer reflects
        // what they are wearing. Drop it rather than reapply something stale on login.
        if (!Plugin.PlayerState.IsLoaded
            || !plugin.Configuration.CharacterLoginSettings.TryGetValue(Plugin.PlayerState.ContentId, out var settings)
            || settings.LastWornDesign == null)
            return;

        settings.LastWornDesign = null;
        settings.LastWornLayers.Clear();
        plugin.Configuration.Save();
        Plugin.Log.Info("Cleared the last-worn design record: a design was applied outside Aetherfit.");
    }

    private void Begin(Action action, bool isLogin, int playerAttemptsLeft, TimeSpan quietDeadline)
    {
        phase = Phase.WaitingForPlayer;
        isLoginFlow = isLogin;
        retriesLeft = 1;
        continuation = action;
        var gen = ++generation;
        WaitForPlayer(gen, playerAttemptsLeft, quietDeadline);
    }

    private void WaitForPlayer(int gen, int attemptsLeft, TimeSpan quietDeadline)
    {
        // Poll until the local player exists rather than relying on a fixed delay — loading screens
        // can easily exceed any fixed timer.
        Plugin.Framework.RunOnTick(() =>
        {
            if (gen != generation)
                return;

            if (!Plugin.ClientState.IsLoggedIn)
            {
                phase = Phase.Idle;
                return;
            }

            if (Plugin.ObjectTable.LocalPlayer == null)
            {
                if (attemptsLeft > 0)
                {
                    WaitForPlayer(gen, attemptsLeft - 1, quietDeadline);
                }
                else
                {
                    phase = Phase.Idle;
                    Plugin.Log.Warning("Local player never spawned; skipping the restore action.");
                    if (isLoginFlow)
                        Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Skipped the login action: the character never finished loading.");
                }

                return;
            }

            phase = Phase.WaitingForQuiet;
            lastGlamourerActivityUtc = DateTime.UtcNow;
            WaitForQuiet(gen, deadlineUtc: DateTime.UtcNow + quietDeadline);
        }, TimeSpan.FromSeconds(1));
    }

    private void WaitForQuiet(int gen, DateTime deadlineUtc)
    {
        Plugin.Framework.RunOnTick(() =>
        {
            if (gen != generation)
                return;

            if (!Plugin.ClientState.IsLoggedIn)
            {
                phase = Phase.Idle;
                return;
            }

            var quiet = DateTime.UtcNow - lastGlamourerActivityUtc >= TimeSpan.FromSeconds(3);
            if (!quiet && DateTime.UtcNow < deadlineUtc)
            {
                WaitForQuiet(gen, deadlineUtc);
                return;
            }

            if (!quiet)
                Plugin.Log.Warning("Glamourer was still busy at the settle deadline; applying anyway.");

            phase = Phase.Idle;
            isLoginFlow = false;
            continuation?.Invoke();
        }, TimeSpan.FromSeconds(1));
    }

    private void RunLoginAction()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            Plugin.Log.Warning("PlayerState was not loaded when the login action ran; skipping it.");
            return;
        }

        // The new character's race/gender feeds mod attribution, so drop anything cached.
        plugin.MainWindow.InvalidateAttributionCache();

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        if (settings.LoginAction == LoginAction.None)
            return;

        string? err = settings.LoginAction switch
        {
            LoginAction.ApplyRandom => plugin.MainWindow.ApplyRandomDesign(),
            LoginAction.ApplyRandomByTag => plugin.MainWindow.ApplyRandomByTags(settings.LoginTags),
            LoginAction.ReapplyLast => plugin.MainWindow.ReapplyLastWorn(),
            _ => null,
        };

        if (err != null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{err}");
            return;
        }

        // Longer than the zone grace: on a cold game start Glamourer's late work lands well after our apply.
        graceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
    }

    private void RunZoneReapply()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;

        var err = plugin.MainWindow.ReapplyLastWorn(quiet: true);
        if (err != null)
        {
            // Stale record (e.g. design deleted) — chat-erroring on every zone would spam.
            Plugin.Log.Info($"Zone-change reapply skipped: {err}");
            return;
        }

        graceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    }

    private void RetryRestore()
    {
        var err = plugin.MainWindow.ReapplyLastWorn(quiet: true);
        if (err != null)
        {
            Plugin.Log.Info($"Restore retry skipped: {err}");
            return;
        }

        graceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    }
}
