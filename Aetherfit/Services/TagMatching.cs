using System;
using System.Collections.Generic;
using System.Linq;

namespace Aetherfit.Services;

// Composite "category/type" tags count as carrying each of their segments, so a design tagged
// "swimsuit/bikini" matches filters for "swimsuit", "bikini" or the full composite.
public static class TagMatching
{
    public static bool Matches(string designTag, string filterTag)
    {
        if (string.Equals(designTag, filterTag, StringComparison.OrdinalIgnoreCase))
            return true;

        return designTag.Contains('/')
            && Segments(designTag).Any(s => string.Equals(s, filterTag, StringComparison.OrdinalIgnoreCase));
    }

    public static bool AnyMatch(IEnumerable<string> designTags, string filterTag)
        => designTags.Any(t => Matches(t, filterTag));

    // Split like the tag tree does, so "a / b" yields the same segments as "a/b".
    public static string[] Segments(string tag)
        => tag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Full tags plus their composite segments, de-duplicated and sorted for pickers.
    public static List<string> WithSegments(IEnumerable<string> tags)
        => tags.SelectMany(t => Segments(t).Prepend(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
