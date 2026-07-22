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

// A tri-state filter (tags, jobs, ...) is a key -> Include/Exclude map; a key absent from the map is left alone.
public enum FilterState { None, Include, Exclude }

public static class FilterStateExtensions
{
    public static FilterState GetFilterState<TKey>(this IReadOnlyDictionary<TKey, bool> filter, TKey key) where TKey : notnull
        => filter.TryGetValue(key, out var include) ? (include ? FilterState.Include : FilterState.Exclude) : FilterState.None;

    // None -> Include -> Exclude -> None, matching a tri-state checkbox.
    public static void CycleFilterState<TKey>(this Dictionary<TKey, bool> filter, TKey key) where TKey : notnull
    {
        switch (filter.GetFilterState(key))
        {
            case FilterState.None: filter[key] = true; break;
            case FilterState.Include: filter[key] = false; break;
            case FilterState.Exclude: filter.Remove(key); break;
        }
    }

    // Tag-specific match: empty filter matches everything, otherwise every Include tag must be present
    // (matching composite segments too) and no Exclude tag may be.
    public static bool MatchesFilter(this IReadOnlyDictionary<string, bool> filter, IReadOnlyCollection<string> designTags)
    {
        if (filter.Count == 0) return true;

        var hasTags = designTags.Count > 0;
        foreach (var (tag, include) in filter)
        {
            var present = hasTags && TagMatching.AnyMatch(designTags, tag);
            if (include != present) return false;
        }
        return true;
    }

    // Plain-equality match for filters with no composite matching (e.g. jobs). The non-generic string
    // overload above is preferred by overload resolution when TKey is string, so tag filtering is unaffected.
    public static bool MatchesFilter<TKey>(this IReadOnlyDictionary<TKey, bool> filter, IReadOnlyCollection<TKey> designValues) where TKey : notnull
    {
        if (filter.Count == 0) return true;

        foreach (var (key, include) in filter)
        {
            var present = designValues.Contains(key);
            if (include != present) return false;
        }
        return true;
    }
}
