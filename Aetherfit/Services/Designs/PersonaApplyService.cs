using System;
using System.Linq;

namespace Aetherfit.Services.Designs;

// Activates a persona - a "which character am I right now" context (Penumbra collection, Customize+
// profile, Honorific title, base layer, and an optional design to apply automatically). Activation is
// the single entry point for all of this; applying a design directly (regular tree/gallery/random-apply/
// Automation/chat command) never routes through here, though it does still inherit the active persona's
// base layer ambiently (Configuration.ResolveBaseDesignLayer) since that's the whole point of "being"
// that persona.
public sealed class PersonaApplyService
{
    private readonly Plugin plugin;

    public PersonaApplyService(Plugin plugin) => this.plugin = plugin;

    public readonly record struct ApplyResult(Guid? PersonaId, string? Error)
    {
        public static ApplyResult Ok(Guid id) => new(id, null);
        public static ApplyResult Fail(string error) => new(null, error);
    }

    // Undoes the outgoing persona's side effects unconditionally first, then applies the target's if it
    // has any. persona == null (Default) means "no side effects to (re)apply" - the unconditional reset
    // above is therefore the entire effect: collection override removed, Customize+ profile disabled,
    // Honorific title cleared, all back to their defaults. Deliberately does not touch appearance/design
    // itself - that's ActivatePersona's job.
    private void EnterPersonaContext(CharacterLoginSettings settings, PersonaProfile? persona)
    {
        plugin.Penumbra.SetCollectionForObject(0, null);

        if (settings.LastPersonaCustomizeProfileId is { } previousProfileId && plugin.Configuration.CustomizePlusIntegrationEnabled)
            plugin.CustomizePlus.DisableProfile(previousProfileId);
        settings.LastPersonaCustomizeProfileId = null;

        if (plugin.Configuration.HonorificIntegrationEnabled)
            plugin.Honorific.ClearTitle();

        if (persona != null)
        {
            if (persona.PenumbraCollectionId is { } collectionId)
            {
                var result = plugin.Penumbra.SetCollectionForObject(0, collectionId);
                if (result != Penumbra.Api.Enums.PenumbraApiEc.Success)
                    Plugin.Log.Warning("Failed to set persona collection for \"{Persona}\": {Result}", persona.Name, result);
            }

            if (persona.CustomizePlusProfileId is { } profileId && plugin.Configuration.CustomizePlusIntegrationEnabled)
            {
                plugin.CustomizePlus.EnableProfile(profileId);
                settings.LastPersonaCustomizeProfileId = profileId;
            }

            if (!string.IsNullOrEmpty(persona.HonorificTitle) && plugin.Configuration.HonorificIntegrationEnabled)
                plugin.Honorific.SetTitle(persona.HonorificTitle, persona.HonorificTitleIsPrefix);
        }

        settings.ActivePersonaId = persona?.Id;
        plugin.Configuration.Save();
    }

    // The single activation entry point - UI Activate button, tree double-click, login/zone-change
    // restore, and the chat command with no design name all go through here. personaId == null activates
    // Default.
    public ApplyResult ActivatePersona(Guid? personaId)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return ApplyResult.Fail("Log in to a character first.");

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        PersonaProfile? persona = null;
        if (personaId is { } pid)
        {
            persona = settings.Personas.FirstOrDefault(p => p.Id == pid);
            if (persona == null)
                return ApplyResult.Fail("Persona not found.");
        }

        EnterPersonaContext(settings, persona);
        ApplyPersonaDesign(settings, persona);
        return ApplyResult.Ok(personaId ?? Guid.Empty);
    }

    // Default (persona == null): revert to the game's own state, then the global Base Design Layer alone
    // if one is set. A real persona: its own Default Design if set, else leave the current design as-is,
    // layering the persona's own resolved base layer underneath it (reapplying the current design and
    // its own Additional Layers afterward so it still reads on top - the base layer only fills in slots
    // the current design leaves untouched, same semantics ApplyDesignById already gives a design's base
    // layer).
    private void ApplyPersonaDesign(CharacterLoginSettings settings, PersonaProfile? persona)
    {
        if (persona == null)
        {
            plugin.DesignApply.RevertAppearance();
            ApplyResolvedBaseLayerOnly(null);
            return;
        }

        if (persona.DefaultDesignId is { } defaultId && plugin.Configuration.CachedOutfits.ContainsKey(defaultId))
        {
            plugin.DesignApply.ApplyDesignById(defaultId);
            return;
        }

        ApplyResolvedBaseLayerOnly(persona);
        if (settings.LastWornDesign is { } currentId && plugin.Configuration.CachedOutfits.ContainsKey(currentId))
            plugin.DesignApply.ApplyDesignById(currentId);
    }

    private void ApplyResolvedBaseLayerOnly(PersonaProfile? persona)
    {
        if (!plugin.Configuration.EnableRandomLayers)
            return;

        var baseLayerId = persona == null
            ? plugin.Configuration.BaseDesignLayerId
            : (persona.InheritBaseLayer ? plugin.Configuration.BaseDesignLayerId : persona.PersonaBaseLayerId);
        if (baseLayerId is { } id && plugin.DesignApply.SupportsLayers(id))
            plugin.DesignApply.ApplyLayerOnly(id);
    }

    // A given design name overrides DefaultDesignId/keep-current for this one apply, without changing
    // it; "random" picks from AssignedDesignIds instead of resolving a name globally.
    public ApplyResult ApplyByName(string personaName, string? designName)
    {
        if (!Plugin.PlayerState.IsLoaded)
            return ApplyResult.Fail("Log in to a character first.");

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);
        var persona = settings.Personas.FirstOrDefault(p => string.Equals(p.Name, personaName, StringComparison.OrdinalIgnoreCase));
        if (persona == null)
            return ApplyResult.Fail($"No persona named \"{personaName}\" found.");

        EnterPersonaContext(settings, persona);

        if (designName == null)
        {
            ApplyPersonaDesign(settings, persona);
            return ApplyResult.Ok(persona.Id);
        }

        if (string.Equals(designName, "random", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = persona.AssignedDesignIds.Where(plugin.DesignApply.IsUsable).ToList();
            if (candidates.Count == 0)
                return ApplyResult.Fail($"\"{persona.Name}\" has no assigned designs to pick from.");

            plugin.DesignApply.ApplyDesignById(candidates[Random.Shared.Next(candidates.Count)]);
            return ApplyResult.Ok(persona.Id);
        }

        var result = plugin.DesignApply.ApplyByName(designName);
        return result.Error != null ? ApplyResult.Fail(result.Error) : ApplyResult.Ok(persona.Id);
    }
}
