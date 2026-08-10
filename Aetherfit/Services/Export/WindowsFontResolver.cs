using System;
using System.IO;
using PdfSharp.Fonts;

namespace Aetherfit.Services.Export;

// PDFsharp's cross-platform core build carries no bundled fonts and won't touch the OS font store on
// its own - this resolves the handful of faces the look-book uses (serif Georgia for display type,
// sans Verdana for body/meta text) straight from the local Windows Fonts folder, which is fine since
// the plugin only ever runs inside the (Windows-only) game client.
internal sealed class WindowsFontResolver : IFontResolver
{
    public string DefaultFontName => "Verdana";

    public byte[] GetFont(string faceName)
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var path = Path.Combine(fontsDir, FileNameFor(faceName));
        if (!File.Exists(path))
            path = Path.Combine(fontsDir, "verdana.ttf");
        return File.ReadAllBytes(path);
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var serif = familyName.Equals("Georgia", StringComparison.OrdinalIgnoreCase);
        return new FontResolverInfo(FaceKey(serif, isBold, isItalic));
    }

    private static string FaceKey(bool serif, bool bold, bool italic)
    {
        var family = serif ? "Georgia" : "Verdana";
        var suffix = (bold, italic) switch
        {
            (true, true) => "-BoldItalic",
            (true, false) => "-Bold",
            (false, true) => "-Italic",
            _ => "-Regular",
        };
        return family + suffix;
    }

    private static string FileNameFor(string faceName) => faceName switch
    {
        "Verdana-Bold" => "verdanab.ttf",
        "Verdana-Italic" => "verdanai.ttf",
        "Verdana-BoldItalic" => "verdanaz.ttf",
        "Georgia-Regular" => "georgia.ttf",
        "Georgia-Bold" => "georgiab.ttf",
        "Georgia-Italic" => "georgiai.ttf",
        "Georgia-BoldItalic" => "georgiaz.ttf",
        _ => "verdana.ttf",
    };
}
