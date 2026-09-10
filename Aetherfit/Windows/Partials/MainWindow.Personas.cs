using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

public partial class MainWindow
{
    private const string CreatePersonaPopupId = "Create new persona?##createPersonaPopup";
    private const string DeletePersonaPopupId = "Delete persona?##deletePersonaConfirm";
    private const string RenamePersonaPopupId = "Rename persona?##renamePersonaPopup";

    private string createPersonaName = string.Empty;
    private bool createPersonaReclaimFocus;
    private string renamePersonaName = string.Empty;
    private bool renamePersonaReclaimFocus;
    private string personaBaseLayerFilter = string.Empty;
    private string personaDefaultDesignFilter = string.Empty;
    private bool personaImagesPanelOpen = true;
    private bool personaTagsPanelOpen = true;
    private bool personaDescriptionPanelOpen = true;
    private bool personaBaseLayerPanelOpen = true;
    private bool personaProfilesPanelOpen = true;

    private const string AddPersonaTagPopupId = "AddPersonaTagPopup";
    private string addPersonaTagSearchText = string.Empty;
    private bool addPersonaTagReclaimFocus;

    private Guid? personaDescriptionEditId;
    private bool personaDescriptionEditing;
    private string personaDescriptionEditBuffer = string.Empty;
    private string? personaDescriptionOriginalValue;

    // Renders as a top-level tree node parallel to the regular design tree/grouping, at the bottom of
    // the left pane. A synthetic "Default" row (no persona active) is always drawn first, then the
    // real personas - each row shows a green check when it's the currently ACTIVE persona (a real,
    // persistent "which character am I right now" state - see PersonaApplyService), distinct from
    // selectedPersona which is just which detail pane the UI happens to be showing.
    private void DrawPersonasSection()
    {
        if (!Plugin.PlayerState.IsLoaded)
            return;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);

        ImGui.Spacing();
        var sectionOpen = ImGui.TreeNodeEx($"Personas ({settings.Personas.Count})##personasRoot",
            ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen);
        if (sectionOpen)
        {
            DrawDefaultPersonaNode(settings);

            // Snapshot the list before iterating - deleting a persona from within the loop (via the
            // detail pane) would otherwise invalidate settings.Personas mid-enumeration.
            foreach (var persona in settings.Personas.ToList())
                DrawPersonaNode(persona, settings.ActivePersonaId == persona.Id);

            if (ImGui.Selectable("   Create new persona##createNewPersonaLeaf"))
            {
                createPersonaName = string.Empty;
                createPersonaReclaimFocus = true;
                ImGui.OpenPopup(CreatePersonaPopupId);
            }
            DrawCreateLeafPlusIcon();

            // Must be called at the same ID-stack depth as the OpenPopup call above - Dear ImGui
            // resolves a popup's id relative to the current ID stack, so calling this after TreePop()
            // (a shallower scope) meant OpenPopup and PopupModal never matched and the popup never opened.
            DrawCreatePersonaPopup(settings);

            ImGui.TreePop();
        }
    }

    // A single-line, non-expandable row - matches a plain design leaf's look rather than a folder-style
    // tree node. Single click selects (navigates the detail pane); double-click activates it outright -
    // mirrors DrawDesignLeaf's own single-click-selects/double-click-applies convention.
    private void DrawPersonaNode(PersonaProfile persona, bool isActive)
    {
        var selected = selectedPersona == persona.Id;
        using (ImRaii.PushColor(ImGuiCol.Text, UiTheme.StateOn, isActive))
        {
            if (ImGui.Selectable($"   {persona.Name}##persona_{persona.Id}", selected))
            {
                selectedPersona = persona.Id;
                selectedDesign = null;
            }
        }
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            selectedPersona = persona.Id;
            selectedDesign = null;
            plugin.PersonaApply.ActivatePersona(persona.Id);
        }

        DrawLeafDot(ImGui.GetColorU32(ImGuiCol.Text));
        DrawActivePersonaIndicator(isActive);
    }

    // The synthetic "Default" entry - not a real stored PersonaProfile, so it can't be renamed or
    // deleted. Represents "no persona active": plain in-game state, plus the global Base Design Layer if
    // one is set. selectedPersona == Guid.Empty is the sentinel for "Default's detail pane is open" -
    // distinct from null (nothing persona-related selected) and from any real persona's generated Guid.
    private void DrawDefaultPersonaNode(CharacterLoginSettings settings)
    {
        var isActive = settings.ActivePersonaId == null;
        var selected = selectedPersona == Guid.Empty;
        using (ImRaii.PushColor(ImGuiCol.Text, UiTheme.StateOn, isActive))
        {
            if (ImGui.Selectable("   Default##persona_default", selected))
            {
                selectedPersona = Guid.Empty;
                selectedDesign = null;
            }
        }
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            selectedPersona = Guid.Empty;
            selectedDesign = null;
            plugin.PersonaApply.ActivatePersona(null);
        }

        DrawLeafDot(ImGui.GetColorU32(ImGuiCol.Text));
        DrawActivePersonaIndicator(isActive);
    }

    private static void DrawActivePersonaIndicator(bool isActive)
    {
        if (!isActive)
            return;

        ImGui.SameLine();
        DesignDetailView.DrawFontAwesome(FontAwesomeIcon.Check, UiTheme.StateOn);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Currently active");
    }

    private void DrawCreatePersonaPopup(CharacterLoginSettings settings)
    {
        ImGui.SetNextWindowSize(new Vector2(360, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(CreatePersonaPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return;

        ImGui.TextUnformatted("Persona name");
        if (ImGui.IsWindowAppearing() || createPersonaReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            createPersonaReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##createPersonaName", "Persona name (required)", ref createPersonaName, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = createPersonaName.Trim();
        var canConfirm = trimmed.Length > 0;

        ImGui.Spacing();
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Create") || (submitted && canConfirm))
            {
                ImGui.CloseCurrentPopup();
                var persona = new PersonaProfile { Name = trimmed };
                settings.Personas.Add(persona);
                plugin.Configuration.Save();
                selectedPersona = persona.Id;
                selectedDesign = null;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void DrawRenamePersonaPopup(PersonaProfile persona)
    {
        ImGui.SetNextWindowSize(new Vector2(360, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(RenamePersonaPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return;

        ImGui.TextUnformatted("Persona name");
        if (ImGui.IsWindowAppearing() || renamePersonaReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            renamePersonaReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##renamePersonaName", "Persona name (required)", ref renamePersonaName, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = renamePersonaName.Trim();
        var canConfirm = trimmed.Length > 0;

        ImGui.Spacing();
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Rename") || (submitted && canConfirm))
            {
                persona.Name = trimmed;
                plugin.Configuration.Save();
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void DrawSelectedPersonaDetails()
    {
        if (selectedPersona is not { } id || !Plugin.PlayerState.IsLoaded)
            return;

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);

        if (id == Guid.Empty)
        {
            DrawDefaultPersonaDetails(settings);
            return;
        }

        var persona = settings.Personas.FirstOrDefault(p => p.Id == id);
        if (persona == null)
        {
            selectedPersona = null;
            return;
        }
        var isActive = settings.ActivePersonaId == persona.Id;

        var frameH = ImGui.GetFrameHeight();
        float renameW, trashW, activateW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            renameW = ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
            trashW = ImGui.CalcTextSize(FontAwesomeIcon.Trash.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
            activateW = ImGui.CalcTextSize(FontAwesomeIcon.Check.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (6f * ImGuiHelpers.GlobalScale));
        var iconRowX = ImGui.GetCursorPosX();
        ImGui.SetWindowFontScale(1.5f);
        var titleAvail = Math.Max(50f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X);
        var title = TextFit.Ellipsize(persona.Name, titleAvail);
        ImGui.TextColored(UiTheme.GoldAccent, title);
        ImGui.SetWindowFontScale(1.0f);
        if (title != persona.Name && ImGui.IsItemHovered())
            ImGui.SetTooltip(persona.Name);
        if (isActive)
        {
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.StateOn, "(Active)");
        }

        // The action icons sit on their own row under the title, matching the design detail header.
        ImGui.SetCursorPosX(iconRowX);
        using (ImRaii.Disabled(isActive))
        {
            if (HeaderIconButton("activatePersona", FontAwesomeIcon.Check, isActive ? UiTheme.StateOn : null, new Vector2(activateW, frameH)))
                plugin.PersonaApply.ActivatePersona(persona.Id);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isActive ? "This persona is currently active" : "Make this persona active");

        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        if (HeaderIconButton("renamePersona", FontAwesomeIcon.Pen, null, new Vector2(renameW, frameH)))
        {
            renamePersonaName = persona.Name;
            renamePersonaReclaimFocus = true;
            ImGui.OpenPopup(RenamePersonaPopupId);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rename this persona");

        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        if (HeaderIconButton("deletePersona", FontAwesomeIcon.Trash, UiTheme.ErrorText, new Vector2(trashW, frameH)))
            ConfirmDialog.Open(DeletePersonaPopupId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Delete this persona");

        DrawRenamePersonaPopup(persona);

        if (ConfirmDialog.Draw(DeletePersonaPopupId,
                $"Delete the persona \"{persona.Name}\"?", "Delete", holdToConfirm: true))
        {
            var wasActive = isActive;
            settings.Personas.Remove(persona);
            plugin.ImageStorage.RemoveCover(persona.Id);
            selectedPersona = null;
            plugin.Configuration.Save();
            if (wasActive)
                plugin.PersonaApply.ActivatePersona(null);
            return;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (Pills.DrawCollapsibleSubheader("Tags", ref personaTagsPanelOpen))
        {
            ImGui.Indent();
            if (persona.Tags.Count == 0)
                ImGui.TextDisabled("This persona has no tags set.");
            DrawPersonaTagsRow(persona);
            ImGui.Unindent();
            ImGui.Spacing();
        }

        if (Pills.DrawCollapsibleSubheader("Description", ref personaDescriptionPanelOpen))
        {
            ImGui.Indent();
            DrawPersonaDescriptionEditor(persona);
            ImGui.Unindent();
            ImGui.Spacing();
        }

        if (Pills.DrawCollapsibleSubheader("Images", ref personaImagesPanelOpen, ImageHelpText))
        {
            ImGui.Indent();
            DrawImagesBlock(persona.Id);
            ImGui.Unindent();
            ImGui.Spacing();
        }

        if (Pills.DrawCollapsibleSubheader("Persona Base Layer", ref personaBaseLayerPanelOpen))
        {
            ImGui.Indent();
            DrawPersonaBaseLayerPicker(persona);
            ImGui.Unindent();
            ImGui.Spacing();
        }

        if (Pills.DrawCollapsibleSubheader("Persona Profiles", ref personaProfilesPanelOpen))
        {
            ImGui.Indent();
            DrawPersonaCollectionPicker(persona);
            DrawPersonaCustomizePlusPicker(persona);
            DrawPersonaTitleEditor(persona);
            DrawPersonaDefaultDesignPicker(persona);
            ImGui.Unindent();
            ImGui.Spacing();
        }

        DrawPersonaCustomizationsSection(persona);
    }

    // Read-only, mostly static - Default can't be renamed, deleted, or given tags/description/collection/
    // Customize+/title/Default Design of its own, so none of that editing machinery applies here.
    private void DrawDefaultPersonaDetails(CharacterLoginSettings settings)
    {
        var isActive = settings.ActivePersonaId == null;

        ImGui.SetWindowFontScale(1.5f);
        ImGui.TextColored(UiTheme.GoldAccent, "Default");
        ImGui.SetWindowFontScale(1.0f);
        if (isActive)
        {
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.StateOn, "(Active)");
        }

        using (ImRaii.Disabled(isActive))
        {
            if (ImGui.Button("Activate"))
                plugin.PersonaApply.ActivatePersona(null);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isActive
                ? "Default is currently active"
                : "Revert to your in-game appearance, plus the global Base Design Layer if one is set");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Default represents your character with no persona active: your plain in-game "
            + "appearance, with only the global Base Design Layer applied on top if one is set. It can't "
            + "be renamed, deleted, or given a Default Design of its own - activate a real persona instead "
            + "for that.");

        ImGui.Spacing();
        if (Pills.DrawCollapsibleSubheader("Base Design Layer", ref personaBaseLayerPanelOpen))
        {
            ImGui.Indent();
            var baseLayerName = plugin.Configuration.BaseDesignLayerId is { } blId
                ? plugin.Configuration.ResolveDesignName(blId) : "None";
            ImGui.TextDisabled($"Resolved: {baseLayerName}");
            ImGui.TextDisabled("Set globally in Aetherfit's Settings - Default has no base layer of its own.");
            ImGui.Unindent();
            ImGui.Spacing();
        }
    }

    private void DrawPersonaCollectionPicker(PersonaProfile persona)
    {
        var collections = plugin.Penumbra.GetCollections();
        var preview = persona.PenumbraCollectionId is { } selId && collections.TryGetValue(selId, out var selName)
            ? selName : "None";

        ImGui.TextDisabled("Penumbra Collection:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("##personaCollection", preview);
        if (!combo.Success)
            return;

        if (ImGui.Selectable("None", persona.PenumbraCollectionId == null))
        {
            persona.PenumbraCollectionId = null;
            plugin.Configuration.Save();
        }
        ImGui.Separator();
        foreach (var (collectionId, collectionName) in collections.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.Selectable($"{collectionName}##personaCollection{collectionId}", persona.PenumbraCollectionId == collectionId))
            {
                persona.PenumbraCollectionId = collectionId;
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawPersonaCustomizePlusPicker(PersonaProfile persona)
    {
        var profiles = plugin.CustomizePlus.GetProfiles();
        var preview = persona.CustomizePlusProfileId is { } selId
            ? profiles.FirstOrDefault(p => p.Id == selId).Name ?? "None"
            : "None";

        ImGui.TextDisabled("Customize+ Profile:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("##personaCustomizeProfile", preview);
        if (!combo.Success)
            return;

        if (ImGui.Selectable("None", persona.CustomizePlusProfileId == null))
        {
            persona.CustomizePlusProfileId = null;
            plugin.Configuration.Save();
        }
        ImGui.Separator();
        foreach (var (profileId, profileName) in profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.Selectable($"{profileName}##personaProfile{profileId}", persona.CustomizePlusProfileId == profileId))
            {
                persona.CustomizePlusProfileId = profileId;
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawPersonaTitleEditor(PersonaProfile persona)
    {
        var title = persona.HonorificTitle ?? string.Empty;
        ImGui.TextDisabled("Honorific Title:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("##personaTitle", "None", ref title, 64))
        {
            persona.HonorificTitle = title.Length == 0 ? null : title;
            plugin.Configuration.Save();
        }

        if (!string.IsNullOrEmpty(persona.HonorificTitle))
        {
            ImGui.SameLine();
            var isPrefix = persona.HonorificTitleIsPrefix;
            if (ImGui.Checkbox("Prefix##personaTitlePrefix", ref isPrefix))
            {
                persona.HonorificTitleIsPrefix = isPrefix;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isPrefix ? "Shown before your name" : "Shown after your name");
        }
    }

    // Applied automatically whenever this persona is activated. Unlike the base-layer picker below, this
    // is a full ApplyDesignById apply rather than a layer composite, so any design source works - no
    // Glamourer-only restriction needed.
    private void DrawPersonaDefaultDesignPicker(PersonaProfile persona)
    {
        var preview = persona.DefaultDesignId is { } id ? plugin.Configuration.ResolveDesignName(id) : "None";

        ImGui.TextDisabled("Default Design:");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Applied automatically when this persona is activated. \"None\" keeps whatever "
                + "design is currently worn, just layering this persona's Base Design Layer underneath it.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("##personaDefaultDesign", preview, ImGuiComboFlags.HeightLargest);
        if (!combo.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##personaDefaultDesignFilter", "Filter by name...", ref personaDefaultDesignFilter, 64);
        ImGui.Separator();

        var matches = plugin.Configuration.CachedOutfits
            .Select(kv => (Id: kv.Key, kv.Value.Name))
            .Where(d => personaDefaultDesignFilter.Length == 0
                        || d.Name.Contains(personaDefaultDesignFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var listHeight = Math.Min(matches.Count + 1, MaxVisibleDesignRows) * ImGui.GetTextLineHeightWithSpacing();
        using var scroll = ImRaii.Child("##personaDefaultDesignList", new Vector2(-1, listHeight), false);

        if (ImGui.Selectable("None", persona.DefaultDesignId == null))
        {
            persona.DefaultDesignId = null;
            plugin.Configuration.Save();
        }
        ImGui.Separator();
        foreach (var (designId, name) in matches)
        {
            if (ImGui.Selectable($"{name}##personaDefaultDesign{designId}", persona.DefaultDesignId == designId))
            {
                persona.DefaultDesignId = designId;
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawPersonaBaseLayerPicker(PersonaProfile persona)
    {
        string preview;
        if (persona.InheritBaseLayer)
        {
            var inherited = plugin.Configuration.BaseDesignLayerId is { } inheritedId ? plugin.Configuration.ResolveDesignName(inheritedId) : "None";
            preview = $"Inherit ({inherited})";
        }
        else
        {
            preview = persona.PersonaBaseLayerId is { } specific ? plugin.Configuration.ResolveDesignName(specific) : "None";
        }

        ImGui.TextDisabled("Persona Base Layer:");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Applied instead of the global Base Design Layer while this persona is active, "
                + "for any design that doesn't override its own Base Design Layer.\nInherit falls through "
                + "to the global Base Design Layer set in Settings.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##personaBaseLayer", preview, ImGuiComboFlags.HeightLargest))
        {
            if (combo.Success)
            {
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##personaBaseLayerFilter", "Filter by name...", ref personaBaseLayerFilter, 64);
                ImGui.Separator();

                // Only Glamourer-sourced designs can be picked as a layer - a source like Glamaholic has
                // no apply-on-top mechanism, so it can never be composited into a layer stack.
                var matches = plugin.Configuration.CachedOutfits
                    .Where(kv => kv.Value.Source == DesignSource.Glamourer)
                    .Select(kv => (Id: kv.Key, Name: kv.Value.Name))
                    .Where(d => personaBaseLayerFilter.Length == 0
                                || d.Name.Contains(personaBaseLayerFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Inherit/None live inside the same scrollable child as the design list below, rather than
                // as a fixed block above it - two nested scroll regions rendered as two overlapping
                // scrollbars, and HeightLargest above keeps the popup itself from adding a third.
                var listHeight = Math.Min(matches.Count + 2, MaxVisibleDesignRows) * ImGui.GetTextLineHeightWithSpacing();
                using var scroll = ImRaii.Child("##personaBaseLayerList", new Vector2(-1, listHeight), false);

                if (ImGui.Selectable("Inherit", persona.InheritBaseLayer))
                {
                    persona.InheritBaseLayer = true;
                    plugin.Configuration.Save();
                }
                if (ImGui.Selectable("None", !persona.InheritBaseLayer && persona.PersonaBaseLayerId == null))
                {
                    persona.InheritBaseLayer = false;
                    persona.PersonaBaseLayerId = null;
                    plugin.Configuration.Save();
                }
                ImGui.Separator();

                if (matches.Count == 0)
                {
                    ImGui.TextDisabled("No matching designs.");
                }
                else
                {
                    foreach (var (layerId, layerName) in matches)
                    {
                        if (ImGui.Selectable($"{layerName}##personaBaseLayer{layerId}", !persona.InheritBaseLayer && persona.PersonaBaseLayerId == layerId))
                        {
                            persona.InheritBaseLayer = false;
                            persona.PersonaBaseLayerId = layerId;
                            plugin.Configuration.Save();
                        }
                    }
                }
            }
        }
    }

    // Reuses the exact same Customizations rendering a normal design's own detail pane uses
    // (DrawCustomizationsPanel, MainWindow.EquipmentMods.cs), pointed at whichever design currently
    // resolves as this persona's Base Design Layer - "the difference from your character's plain in-game
    // appearance" this persona's base layer would introduce, including that design's own configured
    // Additional Layers. Intentionally shares the customizationsPanelOpen collapse state with a normal
    // design's own Customizations section (that field isn't keyed per-design either).
    private void DrawPersonaCustomizationsSection(PersonaProfile persona)
    {
        var baseLayerId = persona.InheritBaseLayer ? plugin.Configuration.BaseDesignLayerId : persona.PersonaBaseLayerId;
        if (baseLayerId is { } id && plugin.Configuration.CachedOutfits.TryGetValue(id, out var outfit))
        {
            DrawCustomizationsPanel(id, outfit);
            return;
        }

        if (!Pills.DrawCollapsibleSubheader("Customizations", ref customizationsPanelOpen))
            return;
        ImGui.Indent();
        ImGui.TextDisabled("This persona has no Base Design Layer set - nothing to preview.");
        ImGui.Unindent();
        ImGui.Spacing();
    }

    // Mirrors DrawTagsRow's pill layout, minus the Glamourer-merge button - a persona has no source
    // to merge tags in from.
    private void DrawPersonaTagsRow(PersonaProfile persona)
    {
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var availRight = ImGui.GetWindowPos().X + ImGui.GetContentRegionMax().X;
        var cursorStart = ImGui.GetCursorScreenPos().X;
        var lineRight = cursorStart;
        var first = true;

        string? tagToRemove = null;
        foreach (var tag in persona.Tags)
        {
            var width = ImGui.CalcTextSize(tag).X;
            Pills.PlaceItem(width, ref first, ref lineRight, cursorStart, spacing, availRight);

            DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, tag);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Shift + right-click to remove");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && ImGui.GetIO().KeyShift)
                    tagToRemove = tag;
            }
        }

        var addWidth = ImGui.GetFrameHeight();
        Pills.PlaceItem(addWidth, ref first, ref lineRight, cursorStart, spacing, availRight);
        if (ImGuiComponents.IconButton("addPersonaTag", FontAwesomeIcon.Plus))
        {
            addPersonaTagSearchText = string.Empty;
            var popupPos = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + ImGui.GetStyle().ItemSpacing.Y + (4f * ImGuiHelpers.GlobalScale));
            ImGui.SetNextWindowPos(popupPos);
            ImGui.OpenPopup(AddPersonaTagPopupId);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add tag");

        DrawAddPersonaTagPopup(persona);

        if (tagToRemove != null)
        {
            persona.Tags.Remove(tagToRemove);
            plugin.Configuration.Save();
        }
    }

    private void DrawAddPersonaTagPopup(PersonaProfile persona)
    {
        using var popup = ImRaii.Popup(AddPersonaTagPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing() || addPersonaTagReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            addPersonaTagReclaimFocus = false;
        }

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##addPersonaTagSearch", "Type or search a tag...", ref addPersonaTagSearchText, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);

        var trimmed = addPersonaTagSearchText.Trim();
        var existingTags = plugin.Configuration.DistinctSortedTags()
            .Where(t => !persona.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Where(t => trimmed.Length == 0 || t.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var isNewTag = trimmed.Length > 0
            && !persona.Tags.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            && !existingTags.Contains(trimmed, StringComparer.OrdinalIgnoreCase);

        if (submitted)
        {
            if (trimmed.Length > 0)
            {
                persona.Tags.Add(trimmed);
                plugin.Configuration.Save();
                addPersonaTagSearchText = string.Empty;
                addPersonaTagReclaimFocus = true;
            }
            else
            {
                ImGui.CloseCurrentPopup();
            }
        }

        ImGui.Separator();

        if (isNewTag && ImGui.Selectable($"Add new tag \"{trimmed}\""))
        {
            persona.Tags.Add(trimmed);
            plugin.Configuration.Save();
            addPersonaTagSearchText = string.Empty;
            addPersonaTagReclaimFocus = true;
        }

        if (existingTags.Count == 0)
        {
            if (!isNewTag)
                ImGui.TextDisabled(trimmed.Length > 0 ? "No matching tags." : "All tags are already applied.");
            return;
        }

        if (isNewTag)
            ImGui.Separator();

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var listHeight = Math.Min(existingTags.Count, 8) * rowHeight;
        using var scroll = ImRaii.Child("AddPersonaTagList", new Vector2(220 * ImGuiHelpers.GlobalScale, listHeight), false);
        if (!scroll.Success)
            return;

        foreach (var tag in existingTags)
        {
            if (ImGui.Selectable(tag))
            {
                persona.Tags.Add(tag);
                plugin.Configuration.Save();
                addPersonaTagSearchText = string.Empty;
                addPersonaTagReclaimFocus = true;
            }
        }
    }

    // Mirrors DrawDescriptionEditor's edit/done/cancel flow, minus the Glamourer-pull button - a
    // persona has no source description to pull from.
    private void DrawPersonaDescriptionEditor(PersonaProfile persona)
    {
        if (personaDescriptionEditId != persona.Id)
        {
            personaDescriptionEditId = persona.Id;
            personaDescriptionEditing = false;
        }

        if (personaDescriptionEditing)
        {
            ImGui.SetNextItemWidth(-1);
            var boxHeight = 4 * ImGui.GetTextLineHeightWithSpacing();
            if (ImGui.InputTextMultiline("##personaDescription", ref personaDescriptionEditBuffer, 2000, new Vector2(-1, boxHeight)))
            {
                var trimmed = personaDescriptionEditBuffer.Trim();
                persona.Description = trimmed.Length == 0 ? null : trimmed;
                plugin.Configuration.Save();
            }

            if (ImGuiComponents.IconButton("personaDescDone", FontAwesomeIcon.Check))
                personaDescriptionEditing = false;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Done");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton("personaDescCancel", FontAwesomeIcon.Times))
            {
                persona.Description = personaDescriptionOriginalValue;
                plugin.Configuration.Save();
                personaDescriptionEditing = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cancel — restore the previous value");

            return;
        }

        if (!string.IsNullOrWhiteSpace(persona.Description))
            ImGui.TextWrapped(persona.Description);
        else
            ImGui.TextDisabled("This persona has no description set.");

        if (ImGuiComponents.IconButton("personaDescEdit", FontAwesomeIcon.Pen))
        {
            personaDescriptionOriginalValue = persona.Description;
            personaDescriptionEditBuffer = persona.Description ?? string.Empty;
            personaDescriptionEditing = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Edit description");
    }
}
