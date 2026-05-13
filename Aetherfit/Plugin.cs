using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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

    public readonly WindowSystem WindowSystem = new("Aetherfit");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    public ImageViewerWindow ImageViewer { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        ImageViewer = new ImageViewerWindow();
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ImageViewer);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/aetherfit — toggle the Aetherfit window.\n"
                        + "/aetherfit random — apply a random outfit.\n"
                        + "/aetherfit tag <tag1,tag2,...> — apply a random outfit matching any of the tags."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
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

            default:
                MainWindow.Toggle();
                break;
        }
    }

    private void OnLogin()
    {
        if (Configuration.LoginAction == LoginAction.None)
            return;

        // Give Glamourer a couple of seconds to finish initializing after login before we ask it to apply.
        Framework.RunOnTick(() =>
        {
            string? err = Configuration.LoginAction switch
            {
                LoginAction.ApplyRandom => MainWindow.ApplyRandomDesign(),
                LoginAction.ApplyRandomByTag => MainWindow.ApplyRandomByTags(Configuration.LoginTags),
                _ => null,
            };

            if (err != null)
                ChatGui.PrintError($"[Aetherfit] {err}");
        }, TimeSpan.FromSeconds(3));
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
