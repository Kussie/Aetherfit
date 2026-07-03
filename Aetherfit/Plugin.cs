using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Aetherfit.Services;
using Aetherfit.Sharing;
using Aetherfit.Windows;
using Glamourer.Api.Enums;

namespace Aetherfit;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    // Prefix on every chat message we print, so players can tell our output apart.
    public const string ChatPrefix = "[Aetherfit] ";

    private const string CommandName = "/aetherfit";

    public Configuration Configuration { get; init; }
    public GlamourerService Glamourer { get; init; }
    public PenumbraService Penumbra { get; init; }
    public GameDataService GameData { get; init; }
    public DesignAttributionService Attribution { get; init; }
    public ImageStorageService ImageStorage { get; init; }
    public ScreenshotService Screenshot { get; init; }
    public GallerySharingService GallerySharing { get; init; }

    public readonly WindowSystem WindowSystem = new("Aetherfit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    public ImageViewerWindow ImageViewer { get; init; }
    public ScreenshotSetupWindow ScreenshotSetup { get; init; }
    public ScreenshotCropWindow ScreenshotCrop { get; init; }
    public ForeignGalleryWindow ForeignGallery { get; init; }

    private bool mainWindowOpenBeforeCapture;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Migrate the legacy GalleryFitWholeImage bool into GalleryFitMode.
        if (Configuration.GalleryFitMode == GalleryFitMode.Crop && Configuration.GalleryFitWholeImage)
        {
            Configuration.GalleryFitMode = GalleryFitMode.Letterbox;
            Configuration.GalleryFitWholeImage = false;
            Configuration.Save();
        }

        // Migrate the legacy flat DesignLayers lists into DesignLayerSlots: the old list becomes a single
        // slot, preserving the previous "pick one layer at random" behaviour.
        if (Configuration.DesignLayers.Count > 0)
        {
            foreach (var (baseId, layers) in Configuration.DesignLayers)
            {
                if (layers.Count > 0 && !Configuration.DesignLayerSlots.ContainsKey(baseId))
                    Configuration.DesignLayerSlots[baseId] = new List<DesignLayerSlot> { new() { Designs = layers } };
            }

            Configuration.DesignLayers.Clear();
            Configuration.Save();
        }

        Glamourer = new GlamourerService();
        Penumbra = new PenumbraService();
        GameData = new GameDataService();
        Attribution = new DesignAttributionService(GameData, Penumbra);
        ImageStorage = new ImageStorageService(Configuration);
        Screenshot = new ScreenshotService();
        GallerySharing = new GallerySharingService(Configuration, ImageStorage, GameData, Attribution);

        // Clean up any imported-gallery images a previous session left behind (e.g. if we crashed before tidying up).
        ImageStorage.ClearAllForeign();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        ImageViewer = new ImageViewerWindow();
        ScreenshotSetup = new ScreenshotSetupWindow(this);
        ScreenshotCrop = new ScreenshotCropWindow(this);
        ForeignGallery = new ForeignGalleryWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ImageViewer);
        WindowSystem.AddWindow(ScreenshotSetup);
        WindowSystem.AddWindow(ScreenshotCrop);
        WindowSystem.AddWindow(ForeignGallery);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/aetherfit — toggle the Aetherfit window.\n"
                        + "/aetherfit random — apply a random outfit.\n"
                        + "/aetherfit tag [favourite] <tag1,tag2,...> — apply a random outfit matching the tags, optionally favourites only.\n"
                        + "/aetherfit job — apply a random outfit associated with your current job.\n"
                        + "/aetherfit favourite [job] — apply a random favourite outfit, optionally only one associated with your current job.\n"
                        + "/aetherfit revert — revert appearance to the game state."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        ClientState.Login += OnLogin;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Glamourer.OnExternalStateFinalized += OnGlamourerStateFinalized;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.Login -= OnLogin;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Glamourer.OnExternalStateFinalized -= OnGlamourerStateFinalized;
        Glamourer.Dispose();

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        ImageViewer.Dispose();
        ScreenshotSetup.Dispose();
        ScreenshotCrop.Dispose();
        ForeignGallery.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Length == 0)
        {
            MainWindow.Toggle();
            return;
        }

        var split = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = split[0].ToLowerInvariant();
        var rest = split.Length > 1 ? split[1] : string.Empty;

        switch (verb)
        {
            case "random":
            {
                var err = MainWindow.ApplyRandomDesign();
                if (err != null)
                    ChatGui.PrintError($"{ChatPrefix}{err}");
                break;
            }

            case "tag":
            case "tags":
            {
                // A leading "favourite" keyword restricts the pick to favourites; everything after it is
                // the tag list. Tags may themselves contain spaces, so only the first word is inspected.
                var tagArgs = rest;
                var favouritesOnly = false;
                var words = rest.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0 && words[0].ToLowerInvariant() is "favourite" or "favorite" or "fav")
                {
                    favouritesOnly = true;
                    tagArgs = words.Length > 1 ? words[1] : string.Empty;
                }

                var tags = tagArgs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var err = MainWindow.ApplyRandomByTags(tags, favouritesOnly);
                if (err != null)
                    ChatGui.PrintError($"{ChatPrefix}{err}");
                break;
            }

            case "job":
            {
                var err = MainWindow.ApplyRandomByCurrentJob();
                if (err != null)
                    ChatGui.PrintError($"{ChatPrefix}{err}");
                break;
            }

            case "favourite":
            case "favorite":
            case "fav":
            {
                var option = rest.Trim().ToLowerInvariant();
                if (option.Length > 0 && option != "job")
                {
                    ChatGui.PrintError($"{ChatPrefix}Unknown option \"{rest.Trim()}\" — usage: /aetherfit favourite [job]");
                    break;
                }

                var err = MainWindow.ApplyRandomFavourite(matchCurrentJob: option == "job");
                if (err != null)
                    ChatGui.PrintError($"{ChatPrefix}{err}");
                break;
            }

            case "revert":
                MainWindow.RevertAppearance();
                break;

            case "anims":
            case "animations":
                PrintCurrentAnimations();
                break;

            default:
                MainWindow.Toggle();
                break;
        }
    }

    private void PrintCurrentAnimations()
    {
        var snapshot = AnimationInspectionService.ReadLocalPlayerTimelines();
        if (snapshot == null)
        {
            ChatGui.PrintError($"{ChatPrefix}Log in to a character first.");
            return;
        }

        if (snapshot.ActiveSlots.Count == 0)
        {
            ChatGui.Print($"{ChatPrefix}No animation timelines are currently playing.");
            return;
        }

        ChatGui.Print($"{ChatPrefix}Currently playing animation timelines:");
        foreach (var (slot, timelineId, speed) in snapshot.ActiveSlots)
        {
            var line = $"{ChatPrefix}  {AnimationInspectionService.SlotLabel(slot)}: "
                     + $"{timelineId} — {GameData.ResolveActionTimelineName(timelineId)}";
            if (Math.Abs(speed - 1f) > 0.001f)
                line += $" (speed ×{speed:0.##})";
            ChatGui.Print(line);
        }

        if (snapshot.BaseOverride != 0)
            ChatGui.Print($"{ChatPrefix}  Base override: {snapshot.BaseOverride} — {GameData.ResolveActionTimelineName(snapshot.BaseOverride)}");
        if (snapshot.LipsOverride != 0)
            ChatGui.Print($"{ChatPrefix}  Lips override: {snapshot.LipsOverride} — {GameData.ResolveActionTimelineName(snapshot.LipsOverride)}");
        if (Math.Abs(snapshot.OverallSpeed - 1f) > 0.001f)
            ChatGui.Print($"{ChatPrefix}  Overall speed: ×{snapshot.OverallSpeed:0.##}");
    }

    // Set while a restore sequence (post-login or post-zone-change) is waiting for the player to
    // spawn and Glamourer to go quiet: Glamourer state changes during this window are its own
    // restoration work (gearset load, automation, zone reverts), not the user changing outfits.
    private bool restoreSequenceActive;
    // The login flow owns the first restore after login; TerritoryChanged fires during that load
    // too and must not start a competing zone sequence.
    private bool restoreIsLoginFlow;
    // Bumped on every sequence start so in-flight RunOnTick chains from a superseded sequence
    // (e.g. rapid consecutive zone changes) notice they are stale and stop.
    private int restoreGeneration;
    // What to run once Glamourer goes quiet: the login action or the zone-change reapply. Kept in
    // a field so the one-shot grace-window retry re-runs whichever action last applied.
    private Action? restoreContinuation;
    private DateTime lastGlamourerActivityUtc;

    // After a restore applies, external design applies within this window are Glamourer's late
    // work (automation, or the deferred finalization of our own apply on a slow redraw) rather
    // than the user changing outfits: they must not clear the last-worn record, and they get one
    // re-apply so late automation can't silently overwrite the restored outfit.
    private DateTime postRestoreGraceUntilUtc = DateTime.MinValue;
    private int restoreRetriesLeft;

    private void OnLogin()
    {
        // Slow logins (heavy mod loads) can keep Glamourer busy for a long time, so be generous.
        StartRestoreSequence(RunLoginAction, isLoginFlow: true,
            playerAttemptsLeft: 120, quietDeadline: TimeSpan.FromSeconds(60));
    }

    private void StartRestoreSequence(Action continuation, bool isLoginFlow,
                                      int playerAttemptsLeft, TimeSpan quietDeadline)
    {
        restoreSequenceActive = true;
        restoreIsLoginFlow = isLoginFlow;
        restoreRetriesLeft = 1;
        restoreContinuation = continuation;
        var gen = ++restoreGeneration;
        WaitForPlayerThenApply(gen, playerAttemptsLeft, quietDeadline);
    }

    private void WaitForPlayerThenApply(int gen, int attemptsLeft, TimeSpan quietDeadline)
    {
        // PlayerState loads before the character object spawns into the world, and Glamourer needs the
        // actual object (applying earlier returns ActorNotFound), so poll until the local player exists
        // rather than relying on a fixed delay. Loading screens can easily exceed any fixed timer.
        Framework.RunOnTick(() =>
        {
            if (gen != restoreGeneration)
                return;

            if (!ClientState.IsLoggedIn)
            {
                restoreSequenceActive = false;
                return;
            }

            if (ObjectTable.LocalPlayer == null)
            {
                if (attemptsLeft > 0)
                {
                    WaitForPlayerThenApply(gen, attemptsLeft - 1, quietDeadline);
                }
                else
                {
                    restoreSequenceActive = false;
                    Log.Warning("Local player never spawned; skipping the restore action.");
                    if (restoreIsLoginFlow)
                        ChatGui.PrintError($"{ChatPrefix}Skipped the login action: the character never finished loading.");
                }

                return;
            }

            // Glamourer keeps touching the character for a while after spawn (gearset load, automation),
            // and whatever applies last wins. Wait until its state changes go quiet before we apply.
            lastGlamourerActivityUtc = DateTime.UtcNow;
            WaitForGlamourerQuiet(gen, deadlineUtc: DateTime.UtcNow + quietDeadline);
        }, TimeSpan.FromSeconds(1));
    }

    private void WaitForGlamourerQuiet(int gen, DateTime deadlineUtc)
    {
        Framework.RunOnTick(() =>
        {
            if (gen != restoreGeneration)
                return;

            if (!ClientState.IsLoggedIn)
            {
                restoreSequenceActive = false;
                return;
            }

            var quiet = DateTime.UtcNow - lastGlamourerActivityUtc >= TimeSpan.FromSeconds(3);
            if (!quiet && DateTime.UtcNow < deadlineUtc)
            {
                WaitForGlamourerQuiet(gen, deadlineUtc);
                return;
            }

            if (!quiet)
                Log.Warning("Glamourer was still busy at the settle deadline; applying anyway.");

            restoreSequenceActive = false;
            restoreIsLoginFlow = false;
            restoreContinuation?.Invoke();
        }, TimeSpan.FromSeconds(1));
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        if (restoreSequenceActive && restoreIsLoginFlow)
            return;

        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded)
            return;

        // Read-only lookup on purpose: don't create/save settings from an event firing every zone.
        if (!Configuration.CharacterLoginSettings.TryGetValue(PlayerState.ContentId, out var settings)
            || !settings.ReapplyOnZoneChange
            || settings.LastWornDesign == null)
            return;

        // A new TerritoryChanged means a new load: StartRestoreSequence bumps restoreGeneration,
        // cancelling any in-flight zone sequence so the newest load wins.
        StartRestoreSequence(RunZoneReapply, isLoginFlow: false,
            playerAttemptsLeft: 60, quietDeadline: TimeSpan.FromSeconds(30));
    }

    private void RunZoneReapply()
    {
        if (!PlayerState.IsLoaded)
            return;

        var err = MainWindow.ReapplyLastWorn(quiet: true);
        if (err != null)
        {
            // Stale record (e.g. design deleted) — chat-erroring on every zone would spam.
            Log.Info($"Zone-change reapply skipped: {err}");
            return;
        }

        postRestoreGraceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    }

    private void OnGlamourerStateFinalized(nint actor, StateFinalizationType type)
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || actor != localPlayer.Address)
            return;

        if (restoreSequenceActive)
        {
            lastGlamourerActivityUtc = DateTime.UtcNow;
            return;
        }

        if (type != StateFinalizationType.DesignApplied)
            return;

        if (DateTime.UtcNow < postRestoreGraceUntilUtc)
        {
            // A design landed right after our restore apply: Glamourer's late automation can overwrite
            // the outfit we just restored ("whatever applies last wins"), so put ours back once.
            if (restoreRetriesLeft > 0)
            {
                restoreRetriesLeft--;
                Log.Info("A design was applied right after the restore action; re-applying once it settles.");
                restoreSequenceActive = true;
                lastGlamourerActivityUtc = DateTime.UtcNow;
                // Bump the generation so a stale chain can't collide with the retry chain.
                WaitForGlamourerQuiet(++restoreGeneration, deadlineUtc: DateTime.UtcNow + TimeSpan.FromSeconds(30));
            }

            return;
        }

        // A design we didn't apply landed on the character, so the last-worn record no longer
        // reflects what they are wearing. Drop it rather than reapply something stale on login.
        if (!PlayerState.IsLoaded
            || !Configuration.CharacterLoginSettings.TryGetValue(PlayerState.ContentId, out var settings)
            || settings.LastWornDesign == null)
            return;

        settings.LastWornDesign = null;
        settings.LastWornLayers.Clear();
        Configuration.Save();
        Log.Info("Cleared the last-worn design record: a design was applied outside Aetherfit.");
    }

    private void RunLoginAction()
    {
        if (!PlayerState.IsLoaded)
        {
            Log.Warning("PlayerState was not loaded when the login action ran; skipping it.");
            return;
        }

        // The new character's race/gender feeds mod attribution, so drop anything cached for the last one.
        MainWindow.InvalidateAttributionCache();

        var settings = Configuration.GetOrCreateLoginSettings(PlayerState.ContentId);
        if (settings.LoginAction == LoginAction.None)
            return;

        string? err = settings.LoginAction switch
        {
            LoginAction.ApplyRandom => MainWindow.ApplyRandomDesign(),
            LoginAction.ApplyRandomByTag => MainWindow.ApplyRandomByTags(settings.LoginTags),
            LoginAction.ReapplyLast => MainWindow.ReapplyLastWorn(),
            _ => null,
        };

        if (err != null)
        {
            ChatGui.PrintError($"{ChatPrefix}{err}");
            return;
        }

        postRestoreGraceUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    public void SetMainWindowHiddenForCapture(bool hide)
    {
        if (hide)
        {
            mainWindowOpenBeforeCapture = MainWindow.IsOpen;
            MainWindow.IsOpen = false;
        }
        else if (mainWindowOpenBeforeCapture)
        {
            MainWindow.IsOpen = true;
        }
    }
}
