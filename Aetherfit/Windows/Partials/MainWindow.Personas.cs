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
    private string personaAddDesignFilter = string.Empty;
    private string personaBaseLayerFilter = string.Empty;
    private bool personaImagesPanelOpen = true;
    private bool personaTagsPanelOpen = true;
    private bool personaDescriptionPanelOpen = true;
    private bool personaBaseLayerPanelOpen = true;
    private bool personaProfilesPanelOpen = true;
    private bool personaAssignedDesignsPanelOpen = true;

    private const string AddPersonaTagPopupId = "AddPersonaTagPopup";
    private string addPersonaTagSearchText = string.Empty;
    private bool addPersonaTagReclaimFocus;

    private Guid? personaDescriptionEditId;
    private bool personaDescriptionEditing;
    private string personaDescriptionEditBuffer = string.Empty;
    private string? personaDescriptionOriginalValue;

    // Renders as a top-level tree node parallel to the regular design tree/grouping, at the bottom of
    // the left pane. Each persona expands to show only its own AssignedDesignIds - clicking one of
    // those applies it through PersonaApplyService (bundling the persona's collection/C+ profile/
    // title/base layer), never the plain DesignApplyService path the regular tree uses.
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
            // Snapshot the list before iterating - deleting a persona from within the loop (via the
            // detail pane) would otherwise invalidate settings.Personas mid-enumeration.
            foreach (var persona in settings.Personas.ToList())
                DrawPersonaNode(persona);

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
    // tree node. Browsing/applying a persona's own assigned designs happens in its detail pane
    // (DrawSelectedPersonaDetails) instead of expanding inline here.
    private void DrawPersonaNode(PersonaProfile persona)
    {
        var selected = selectedPersona == persona.Id;
        if (ImGui.Selectable($"   {persona.Name}##persona_{persona.Id}", selected))
        {
            selectedPersona = persona.Id;
            selectedDesign = null;
        }
        DrawLeafDot(ImGui.GetColorU32(ImGuiCol.Text));
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
        var persona = settings.Personas.FirstOrDefault(p => p.Id == id);
        if (persona == null)
        {
            selectedPersona = null;
            return;
        }

        var frameH = ImGui.GetFrameHeight();
        float renameW, trashW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            renameW = ImGui.CalcTextSize(FontAwesomeIcon.Pen.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
            trashW = ImGui.CalcTextSize(FontAwesomeIcon.Trash.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
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

        // The action icons sit on their own row under the title, matching the design detail header.
        ImGui.SetCursorPosX(iconRowX);
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
                $"Delete the persona \"{persona.Name}\"? This doesn't delete any of its assigned designs, only the persona itself.",
                "Delete", holdToConfirm: true))
        {
            settings.Personas.Remove(persona);
            plugin.ImageStorage.RemoveCover(persona.Id);
            selectedPersona = null;
            plugin.Configuration.Save();
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
            ImGui.Unindent();
            ImGui.Spacing();
        }

        if (Pills.DrawCollapsibleSubheader($"Assigned Designs ({persona.AssignedDesignIds.Count})", ref personaAssignedDesignsPanelOpen))
        {
            ImGui.Indent();
            DrawPersonaAddDesignPicker(persona);
            ImGui.Spacing();

            if (persona.AssignedDesignIds.Count == 0)
            {
                ImGui.TextDisabled("No designs assigned yet.");
            }
            else
            {
                float removeW;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                    removeW = ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);

                // Bordered and height-capped so a long assignment list scrolls in place instead of pushing
                // the rest of the detail pane down - matches other capped lists (bulk-layer preview, etc).
                var listHeight = Math.Min(persona.AssignedDesignIds.Count, MaxVisibleDesignRows) * ImGui.GetFrameHeightWithSpacing();
                Guid? toRemove = null;
                using (ImRaii.Child("##personaAssignedDesignsList", new Vector2(-1, listHeight), true))
                {
                    foreach (var designId in persona.AssignedDesignIds)
                    {
                        using var rowId = ImRaii.PushId(designId.ToString());
                        if (DrawPersonaAssignedDesignRow(persona, designId, removeW, frameH))
                            toRemove = designId;
                    }
                }

                if (toRemove is { } removeId)
                {
                    persona.AssignedDesignIds.Remove(removeId);
                    plugin.Configuration.Save();
                }
            }
            ImGui.Unindent();
        }
    }

    // A cover-thumbnail + name + remove-button row - deliberately not a plain Selectable, so the
    // Assigned Designs list reads as a card list rather than a bare filename dump. Returns true if the
    // caller should remove this design from the persona (removal deferred so the list isn't mutated
    // mid-iteration). The cover thumbnail only shows on hover, as a tooltip - matching how a design's
    // own tree leaf (DrawDesignLeafTooltip) previews it, rather than an always-visible inline thumbnail.
    private bool DrawPersonaAssignedDesignRow(PersonaProfile persona, Guid designId, float removeW, float frameH)
    {
        var displayName = designLeafById.TryGetValue(designId, out var leaf) ? leaf.DisplayName : "(missing design)";

        var rowWidth = ImGui.GetContentRegionAvail().X - removeW - ImGui.GetStyle().ItemInnerSpacing.X;
        if (ImGui.Selectable(displayName, false, ImGuiSelectableFlags.None, new Vector2(rowWidth, 0)))
        {
            if (ImGui.GetIO().KeyShift)
            {
                selectedDesign = designId;
                RevealDesignInTree(designId);
            }
            else
            {
                plugin.PersonaApply.ApplyDesignWithinPersona(persona.Id, designId);
            }
        }
        if (ImGui.IsItemHovered())
            DrawPersonaAssignedDesignTooltip(persona, leaf, displayName, designId);

        ImGui.SameLine();
        var remove = HeaderIconButton("remove", FontAwesomeIcon.Times, null, new Vector2(removeW, frameH));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove from this persona");

        return remove;
    }

    // Same title line (the design's full tree path) and image as DrawDesignLeafTooltip, plus this
    // row's own "wear as part of persona" hint in place of the tree's apply/open-in-Glamourer hints.
    private void DrawPersonaAssignedDesignTooltip(PersonaProfile persona, DesignLeaf? leaf, string displayName, Guid designId)
    {
        var hasPath = !string.IsNullOrEmpty(leaf?.FullPath);
        var imagePath = plugin.Configuration.ShowThumbnailOnHover ? plugin.ImageStorage.GetCoverPath(designId) : null;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(hasPath ? leaf!.FullPath : displayName);
        if (imagePath != null)
            DrawImageScaled(imagePath, TooltipImageMax * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled($"Click to wear as part of \"{persona.Name}\"");
        ImGui.TextDisabled("Shift + click to jump to this design in the edit view");
        ImGui.EndTooltip();
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
            ImGui.SetTooltip("Applied instead of the global Base Design Layer for this persona's designs\nthat don't override their own Base Design Layer.\nInherit falls through to the global Base Design Layer set in Settings.");
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

    private void DrawPersonaAddDesignPicker(PersonaProfile persona)
    {
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##personaAddDesign", "Assign a design...", ImGuiComboFlags.HeightLargest))
        {
            if (combo.Success)
            {
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##personaAddDesignFilter", "Filter by name...", ref personaAddDesignFilter, 64);
                ImGui.Separator();

                var matches = plugin.Configuration.CachedOutfits
                    .Where(kv => !persona.AssignedDesignIds.Contains(kv.Key))
                    .Where(kv => personaAddDesignFilter.Length == 0
                                 || kv.Value.Name.Contains(personaAddDesignFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (matches.Count == 0)
                {
                    ImGui.TextDisabled("No matching designs.");
                }
                else
                {
                    var listHeight = Math.Min(matches.Count, 15) * ImGui.GetTextLineHeightWithSpacing();
                    using var scroll = ImRaii.Child("##personaAddDesignList", new Vector2(-1, listHeight), false);
                    foreach (var (designId, outfit) in matches)
                    {
                        if (ImGui.Selectable($"{outfit.Name}##personaAddDesign{designId}"))
                        {
                            persona.AssignedDesignIds.Add(designId);
                            plugin.Configuration.Save();
                        }
                    }
                }
            }
        }
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
