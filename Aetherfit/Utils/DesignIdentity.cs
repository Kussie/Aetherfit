using System;
using Aetherfit.Services.Integrations;

namespace Aetherfit.Utils;

public static class DesignIdentity
{
    // Returns the stable Aetherfit-owned Guid for a given provider + that provider's own native
    // design id. For Glamourer this is always just nativeId itself - zero migration cost, every
    // existing dictionary/image filename keeps meaning exactly what it means today. For any other
    // provider, allocates and persists a fresh Guid the first time a given native id is seen, so
    // the same design resolves to the same Aetherfit id on every future rescan.
    public static Guid Resolve(Configuration config, DesignSource source, Guid nativeId)
    {
        if (source == DesignSource.Glamourer)
            return nativeId;

        var perProvider = config.DesignIdentityMap.TryGetValue(source, out var map)
            ? map
            : config.DesignIdentityMap[source] = new();

        if (perProvider.TryGetValue(nativeId, out var aetherfitId))
            return aetherfitId;

        aetherfitId = Guid.NewGuid();
        perProvider[nativeId] = aetherfitId;
        config.Save(); // persist immediately - losing this mapping orphans the design's local state
        return aetherfitId;
    }
}
