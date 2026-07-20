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
    public OutfitCacheStore OutfitCache { get; init; }
    public GlamourerService Glamourer { get; init; }
    public PenumbraService Penumbra { get; init; }
    public GameDataService GameData { get; init; }
    public DesignAttributionService Attribution { get; init; }
    public ImageStorageService ImageStorage { get; init; }
    public ScreenshotService Screenshot { get; init; }
    public GallerySharingService GallerySharing { get; init; }
    public RestoreSequenceService Restore { get; init; }

    public readonly WindowSystem WindowSystem = new("Aetherfit");
    private ConfigWindow ConfigWindow { get; init; }
    internal MainWindow MainWindow { get; init; }
    public ImageViewerWindow ImageViewer { get; init; }
    public ScreenshotSetupWindow ScreenshotSetup { get; init; }
    public ScreenshotCropWindow ScreenshotCrop { get; init; }
    public ForeignGalleryWindow ForeignGallery { get; init; }

    private readonly ConfigurationSaver configSaver;
    private bool mainWindowOpenBeforeCapture;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        if (Configuration.GalleryFitMode == GalleryFitMode.Crop && Configuration.GalleryFitWholeImage)
        {
            Configuration.GalleryFitMode = GalleryFitMode.Letterbox;
            Configuration.GalleryFitWholeImage = false;
            Configuration.Save();
        }

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

        // Attached after the migrations above so those still write directly.
        configSaver = new ConfigurationSaver(Configuration);
        Configuration.AttachSaver(configSaver);

        OutfitCache = new OutfitCacheStore(Configuration);
        OutfitCache.Load();

        Glamourer = new GlamourerService();
        Penumbra = new PenumbraService();
        GameData = new GameDataService();
        Attribution = new DesignAttributionService(GameData, Penumbra);
        ImageStorage = new ImageStorageService(Configuration);
        Screenshot = new ScreenshotService();
        GallerySharing = new GallerySharingService(Configuration, ImageStorage, GameData, Attribution);

        // Clean up any imported-gallery images or in-flight captures a previous session left behind
        // (e.g. if we crashed before tidying up).
        ImageStorage.ClearAllForeign();
        ImageStorage.ClearAllTemp();

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

        Restore = new RestoreSequenceService(this);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = UsageText
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        ClientState.Login += OnLogin;
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Glamourer.OnExternalStateFinalized += OnGlamourerStateFinalized;
        Glamourer.OnAnyStateFinalized += OnGlamourerAnyStateFinalized;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.Login -= OnLogin;
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Glamourer.OnExternalStateFinalized -= OnGlamourerStateFinalized;
        Glamourer.OnAnyStateFinalized -= OnGlamourerAnyStateFinalized;
        Glamourer.Dispose();

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        ImageViewer.Dispose();
        ScreenshotSetup.Dispose();
        ScreenshotCrop.Dispose();
        ForeignGallery.Dispose();

        // Last, so anything the disposals above still saved gets flushed to disk.
        configSaver.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    // One source of truth for command usage: the installer help, /aetherfit help, and the
    // unknown-subcommand error all print this.
    private const string UsageText =
        "/aetherfit — toggle the Aetherfit window.\n"
      + "/aetherfit random — apply a random outfit.\n"
      + "/aetherfit tag [favourite] <tag1,tag2,...> — apply a random outfit matching the tags, optionally favourites only.\n"
      + "/aetherfit job — apply a random outfit associated with your current job.\n"
      + "/aetherfit favourite [job] — apply a random favourite outfit, optionally only one associated with your current job.\n"
      + "/aetherfit last — reapply the last known design.\n"
      + "/aetherfit revert — revert appearance to the game state.\n"
      + "/aetherfit help — show this list.";

    private void PrintUsage()
    {
        foreach (var line in UsageText.Split('\n'))
            ChatGui.Print($"{ChatPrefix}{line}");
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
                // A leading "favourite" keyword restricts the pick to favourites; everything after it is the tag list. Tags may themselves contain spaces, so only the first word is inspected.
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

            case "last":
            {
                var err = MainWindow.ReapplyLastWorn();
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

            case "help":
                PrintUsage();
                break;

            default:
                ChatGui.PrintError($"{ChatPrefix}Unknown subcommand \"{verb}\".");
                PrintUsage();
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

    private void OnLogin() => Restore.BeginLoginRestore();

    private void OnTerritoryChanged(uint territoryId) => Restore.HandleTerritoryChanged();

    private void OnGlamourerAnyStateFinalized(nint actor, StateFinalizationType type)
        => Restore.HandleAnyStateFinalized(actor, type);

    private void OnGlamourerStateFinalized(nint actor, StateFinalizationType type)
        => Restore.HandleExternalStateFinalized(actor, type);

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
    public void OpenDesignInMain(Guid id) => MainWindow.OpenDesign(id);

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
