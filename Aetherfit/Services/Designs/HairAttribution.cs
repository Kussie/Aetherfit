using System;

namespace Aetherfit.Services.Designs;

internal static class HairAttribution
{
    // The current character's clan (Tribe RowId, 1-16) and gender (0 male / 1 female), or null when not
    // logged in. The Tribe sheet RowIds line up with Glamourer's clan numbering, and Sex.Male/Female are 0/1.
    public static int? CurrentPlayerClan()
        => Plugin.PlayerState.IsLoaded ? (int)Plugin.PlayerState.Tribe.RowId : null;

    public static int? CurrentPlayerGender()
        => Plugin.PlayerState.IsLoaded ? (int)Plugin.PlayerState.Sex : null;

    public static string? HairFragment(int? clan, int? genderValue)
    {
        var race = clan is { } c ? ClanToModelRace(c) : null;
        var gender = genderValue switch { 0 => "Male", 1 => "Female", _ => null };
        return race == null || gender == null ? null : $"{race} {gender} Hair ";
    }

    public static bool KeyMatches(string key, string fragment, int expectedId)
    {
        if (!key.StartsWith("Customization:", StringComparison.Ordinal))
            return false;

        var at = key.IndexOf(fragment, StringComparison.Ordinal);
        if (at < 0)
            return false;

        var idText = key[(at + fragment.Length)..].Trim();
        // The id is the last token; guard against anything trailing it.
        var space = idText.IndexOf(' ');
        if (space >= 0)
            idText = idText[..space];
        return int.TryParse(idText, out var modelId) && modelId == expectedId;
    }

    public static string? ClanToModelRace(int clan) => clan switch
    {
        1 => "Midlander",
        2 => "Highlander",
        3 or 4 => "Elezen",
        5 or 6 => "Lalafell",
        7 or 8 => "Miqo'te",
        9 or 10 => "Roegadyn",
        11 or 12 => "Au Ra",
        13 or 14 => "Hrothgar",
        15 or 16 => "Viera",
        _ => null,
    };
}
