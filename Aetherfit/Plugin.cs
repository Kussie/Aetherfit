using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Aetherfit.Services;
using Aetherfit.Windows;

namespace Aetherfit;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/aetherfit";

    public Configuration Configuration { get; init; }
    public GlamourerService Glamourer { get; init; }
    public PenumbraService Penumbra { get; init; }
    public GameDataService GameData { get; init; }
    public ImageStorageService ImageStorage { get; init; }

    public readonly WindowSystem WindowSystem = new("Aetherfit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    public ImageViewerWindow ImageViewer { get; init; }
    public ScreenshotSetupWindow ScreenshotSetup { get; init; }
    public ScreenshotCropWindow ScreenshotCrop { get; init; }

    private bool mainWindowOpenBeforeCapture;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Glamourer = new GlamourerService();
        Penumbra = new PenumbraService();
        GameData = new GameDataService();
        ImageStorage = new ImageStorageService(Configuration);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        ImageViewer = new ImageViewerWindow();
        ScreenshotSetup = new ScreenshotSetupWindow(this);
        ScreenshotCrop = new ScreenshotCropWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ImageViewer);
        WindowSystem.AddWindow(ScreenshotSetup);
        WindowSystem.AddWindow(ScreenshotCrop);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/aetherfit — toggle the Aetherfit window.\n"
                        + "/aetherfit random — apply a random outfit.\n"
                        + "/aetherfit tag <tag1,tag2,...> — apply a random outfit matching any of the tags.\n"
                        + "/aetherfit revert — revert appearance to the game state."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;
        ClientState.Login += OnLogin;

        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.Login -= OnLogin;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        ImageViewer.Dispose();
        ScreenshotSetup.Dispose();
        ScreenshotCrop.Dispose();

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
                    ChatGui.PrintError($"[Aetherfit] {err}");
                break;
            }

            case "tag":
            case "tags":
            {
                var tags = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var err = MainWindow.ApplyRandomByTags(tags);
                if (err != null)
                    ChatGui.PrintError($"[Aetherfit] {err}");
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

    private void OnLogin()
    {
        // Give Glamourer a couple of seconds to finish initializing after login before we ask it to apply.
        // PlayerState.ContentId also needs the same window to populate.
        Framework.RunOnTick(() =>
        {
            if (!PlayerState.IsLoaded)
                return;

            var settings = Configuration.GetOrCreateLoginSettings(PlayerState.ContentId);
            if (settings.LoginAction == LoginAction.None)
                return;

            string? err = settings.LoginAction switch
            {
                LoginAction.ApplyRandom => MainWindow.ApplyRandomDesign(),
                LoginAction.ApplyRandomByTag => MainWindow.ApplyRandomByTags(settings.LoginTags),
                _ => null,
            };

            if (err != null)
                ChatGui.PrintError($"[Aetherfit] {err}");
        }, TimeSpan.FromSeconds(3));
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
