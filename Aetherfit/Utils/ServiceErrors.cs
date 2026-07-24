using System;

namespace Aetherfit.Utils;

internal static class ServiceErrors
{
    public static void Fail(Exception ex, string chatMessage, string logTemplate, params object[] logArgs)
    {
        Plugin.ChatGui.PrintError(chatMessage);
        Plugin.Log.Warning(ex, logTemplate, logArgs);
    }
}
