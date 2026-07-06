using System;
using Dalamud.Bindings.ImGui;

namespace Aetherfit.Ui;

internal static class TextFit
{
    // Truncates with a trailing ellipsis to fit maxWidth. Measures with the current font and
    // window font scale, so callers must have the target scale active.
    public static string Ellipsize(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "…";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(string.Concat(text.AsSpan(0, mid), ellipsis)).X <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo == 0 ? ellipsis : string.Concat(text.AsSpan(0, lo), ellipsis);
    }
}
