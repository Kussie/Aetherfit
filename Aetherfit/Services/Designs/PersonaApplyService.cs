using System;
using System.Linq;

namespace Aetherfit.Services.Designs;

// Applies one of a persona's assigned designs together with the persona's own context (Penumbra
// collection, Customize+ profile, Honorific title, base layer). Deliberately the only entry point -
// applying a design directly (regular tree/gallery/random-apply/Automation/chat command) never routes
// through here and never touches these systems, even for a design that happens to be persona-assigned.
public sealed class PersonaApplyService
{
    private readonly Plugin plugin;

    public PersonaApplyService(Plugin plugin) => this.plugin = plugin;

    public readonly record struct ApplyResult(Guid? PersonaId, string? Error)
    {
        public static ApplyResult Ok(Guid id) => new(id, null);
        public static ApplyResult Fail(string error) => new(null, error);
    }

    // Chat-command entry point: resolves a persona (and, optionally, one of its assigned designs) by
    // name, then delegates to ApplyDesignWithinPersona - mirrors DesignApplyService.ApplyByName's shape.
    public ApplyResult ApplyByName(string personaName, string? designName)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return ApplyResult.Fail("Log in to a character first.");

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        var persona = settings.Personas.FirstOrDefault(p => string.Equals(p.Name, personaName, StringComparison.OrdinalIgnoreCase));
        if (persona == null)
            return ApplyResult.Fail($"No persona named \"{personaName}\" found.");

        Guid designId;
        if (designName != null)
        {
            var matches = persona.AssignedDesignIds
                .Where(id => plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit)
                             && string.Equals(outfit.Name, designName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                return ApplyResult.Fail($"\"{persona.Name}\" has no design named \"{designName}\".");
            if (matches.Count > 1)
                return ApplyResult.Fail($"{matches.Count} designs named \"{designName}\" in \"{persona.Name}\" — can't tell which one you mean.");
            designId = matches[0];
        }
        else
        {
            var candidates = persona.AssignedDesignIds.Where(id => plugin.Configuration.CachedOutfits.ContainsKey(id)).ToList();
            if (candidates.Count == 0)
                return ApplyResult.Fail($"\"{persona.Name}\" has no assigned designs.");
            designId = candidates[Random.Shared.Next(candidates.Count)];
        }

        return ApplyDesignWithinPersona(persona.Id, designId) ? ApplyResult.Ok(persona.Id) : ApplyResult.Fail("Failed to apply persona.");
    }

    public bool ApplyDesignWithinPersona(Guid personaId, Guid designId)
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Log in to a character first.");
            return false;
        }

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        var persona = settings.Personas.FirstOrDefault(p => p.Id == personaId);
        if (persona == null)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Persona not found.");
            return false;
        }

        if (!plugin.Configuration.CachedOutfits.ContainsKey(designId))
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Design not found.");
            return false;
        }

        // InheritBaseLayer -> pass null (defer to the global default, same as a direct apply); an explicit
        // "None" (PersonaBaseLayerId unset) -> Guid.Empty, a sentinel ResolveBaseDesignLayer recognises as
        // "no base layer" rather than falling through to the global default; a specific design passes through.
        var personaBaseLayerId = persona.InheritBaseLayer ? (Guid?)null : (persona.PersonaBaseLayerId ?? Guid.Empty);
        plugin.DesignApply.ApplyDesignById(designId, personaBaseLayerId: personaBaseLayerId);

        if (persona.PenumbraCollectionId is { } collectionId)
        {
            var result = plugin.Penumbra.SetCollectionForObject(0, collectionId);
            if (result != Penumbra.Api.Enums.PenumbraApiEc.Success)
                Plugin.Log.Warning("Failed to set persona collection for \"{Persona}\": {Result}", persona.Name, result);
        }

        if (persona.CustomizePlusProfileId is { } profileId && plugin.Configuration.CustomizePlusIntegrationEnabled)
        {
            if (settings.LastPersonaCustomizeProfileId is { } previousId && previousId != profileId)
                plugin.CustomizePlus.DisableProfile(previousId);

            plugin.CustomizePlus.EnableProfile(profileId);
            settings.LastPersonaCustomizeProfileId = profileId;
            plugin.Configuration.Save();
        }

        if (!string.IsNullOrEmpty(persona.HonorificTitle) && plugin.Configuration.HonorificIntegrationEnabled)
            plugin.Honorific.SetTitle(persona.HonorificTitle, persona.HonorificTitleIsPrefix);

        return true;
    }
}
