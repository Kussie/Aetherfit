namespace Aetherfit.Utils;

// Background export/import work finishes off the framework thread; IChatGui isn't safe to call from there.
internal static class FrameworkChat
{
    public static void Print(string message)
        => Plugin.Framework.RunOnFrameworkThread(() => Plugin.ChatGui.Print(message));

    public static void PrintError(string message)
        => Plugin.Framework.RunOnFrameworkThread(() => Plugin.ChatGui.PrintError(message));
}
