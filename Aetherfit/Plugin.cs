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
                        + "/aetherfit tag <tag1,tag2,...> — apply a random outfit matching any of the tags.\n"
                        + "/aetherfit job — apply a random outfit associated with your current job.\n"
                        + "/aetherfit revert — revert appearance to the game state."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        ClientState.Login += OnLogin;
        Glamourer.OnExternalStateFinalized += OnGlamourerStateFinalized;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.Login -= OnLogin;
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
                var tags = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var err = MainWindow.ApplyRandomByTags(tags);
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

            case "revert":
                MainWindow.RevertAppearance();
                break;

            default:
                MainWindow.Toggle();
                break;
        }
    }

    // Set while the post-login sequence is running: Glamourer state changes during this window are its
    // own restoration work (gearset load, automation), not the user changing outfits.
    private bool loginSequenceActive;
    private DateTime lastGlamourerActivityUtc;

    private void OnLogin()
    {
        // PlayerState loads before the character object spawns into the world, and Glamourer needs the
        // actual object (applying earlier returns ActorNotFound), so poll until the local player exists
        // rather than relying on a fixed delay. Loading screens can easily exceed any fixed timer.
        loginSequenceActive = true;
        WaitForPlayerThenApply(attemptsLeft: 60);
    }

    private void WaitForPlayerThenApply(int attemptsLeft)
    {
        Framework.RunOnTick(() =>
        {
            if (!ClientState.IsLoggedIn)
            {
                loginSequenceActive = false;
                return;
            }

            if (ObjectTable.LocalPlayer == null)
            {
                if (attemptsLeft > 0)
                {
                    WaitForPlayerThenApply(attemptsLeft - 1);
                }
                else
                {
                    loginSequenceActive = false;
                    Log.Warning("Local player never spawned after login; skipping the login action.");
                }

                return;
            }

            // Glamourer keeps touching the character for a while after spawn (gearset load, automation),
            // and whatever applies last wins. Wait until its state changes go quiet before we apply.
            lastGlamourerActivityUtc = DateTime.UtcNow;
            WaitForGlamourerQuiet(deadlineUtc: DateTime.UtcNow + TimeSpan.FromSeconds(20));
        }, TimeSpan.FromSeconds(1));
    }

    private void WaitForGlamourerQuiet(DateTime deadlineUtc)
    {
        Framework.RunOnTick(() =>
        {
            if (!ClientState.IsLoggedIn)
            {
                loginSequenceActive = false;
                return;
            }

            var quiet = DateTime.UtcNow - lastGlamourerActivityUtc >= TimeSpan.FromSeconds(3);
            if (!quiet && DateTime.UtcNow < deadlineUtc)
            {
                WaitForGlamourerQuiet(deadlineUtc);
                return;
            }

            loginSequenceActive = false;
            RunLoginAction();
        }, TimeSpan.FromSeconds(1));
    }

    private void OnGlamourerStateFinalized(nint actor, StateFinalizationType type)
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || actor != localPlayer.Address)
            return;

        if (loginSequenceActive)
        {
            lastGlamourerActivityUtc = DateTime.UtcNow;
            return;
        }

        if (type != StateFinalizationType.DesignApplied)
            return;

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
            return;

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
            ChatGui.PrintError($"{ChatPrefix}{err}");
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
