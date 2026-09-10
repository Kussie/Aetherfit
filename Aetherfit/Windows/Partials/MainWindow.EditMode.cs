using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;

namespace Aetherfit.Windows;

// The design detail pane's own orchestration: the welcome placeholder, and DrawSelectedOutfitDetails
// itself (the header/action-icon row, then delegating each collapsible section out to its own partial -
// Tags, Description, Images, Variants, equipment/mods/layers panels elsewhere in MainWindow), plus the
// footer date row shared by nothing else.
public partial class MainWindow
{
    private const string ForceSyncPopupId = "Force Sync?##forceSyncConfirm";

    private void DrawWelcomePlaceholder()
    {
        const string message = "Select a design on the left to see its details.";
        var avail = ImGui.GetContentRegionAvail();

        var iconDir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        var iconPath = iconDir != null ? Path.Combine(iconDir, "icon-square.png") : null;
        var tex = iconPath != null && File.Exists(iconPath)
            ? Plugin.TextureProvider.GetFromFile(iconPath).GetWrapOrEmpty()
            : null;

        Vector2 imageSize = Vector2.Zero;
        if (tex is { Width: > 0, Height: > 0 })
        {
            var maxSide = Math.Min(256f * ImGuiHelpers.GlobalScale, Math.Min(avail.X, avail.Y * 0.6f));
            var scale = Math.Min(maxSide / tex.Width, maxSide / tex.Height);
            if (scale > 0f)
                imageSize = new Vector2(tex.Width * scale, tex.Height * scale);
        }

        var textSize = ImGui.CalcTextSize(message);
        var spacing = imageSize.Y > 0 ? ImGui.GetStyle().ItemSpacing.Y : 0f;
        var totalHeight = imageSize.Y + spacing + textSize.Y;
        var startY = ImGui.GetCursorPosY() + Math.Max(0f, (avail.Y - totalHeight) * 0.5f);

        if (imageSize.Y > 0 && tex != null)
        {
            ImGui.SetCursorPosY(startY);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail.X - imageSize.X) * 0.5f));
            ImGui.Image(tex.Handle, imageSize);
            startY = ImGui.GetCursorPosY() + spacing;
        }

        ImGui.SetCursorPosY(startY);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (avail.X - textSize.X) * 0.5f));
        ImGui.TextDisabled(message);
    }

    private void DrawSelectedOutfitDetails()
    {
        if (selectedDesign is not { } id)
        {
            DrawWelcomePlaceholder();
            return;
        }

        if (!plugin.Configuration.CachedOutfits.TryGetValue(id, out var details))
        {
            ImGui.TextDisabled("No cached metadata for this design. Click Refresh.");
            return;
        }

        // Always exactly two rows now: Created/Last worn, then Last edited/Source - Created and Last
        // edited are omitted when absent, but Last worn and Source always occupy their row regardless.
        var datesBlockHeight = 2 * ImGui.GetTextLineHeightWithSpacing();

        var bodyHeight = Math.Max(0, ImGui.GetContentRegionAvail().Y - datesBlockHeight);

        using (var body = ImRaii.Child("DesignBody", new Vector2(0, bodyHeight), false))
        {
            if (body.Success)
            {
                var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(id);
                var isHidden = plugin.Configuration.HiddenDesigns.Contains(id);
                var isGlamourer = details.Source == DesignSource.Glamourer;
                var isGlamaholic = details.Source == DesignSource.Glamaholic;
                var isGlamourPlate = details.Source == DesignSource.GlamourPlate;
                var showForceSync = isGlamourer || isGlamaholic;
                var hasGearOverride = isGlamourer && plugin.Configuration.GearImportOverrides.ContainsKey(id);
                var showImport = isGlamaholic || isGlamourPlate || hasGearOverride;
                var showBulkLayer = isGlamourer && plugin.Configuration.EnableRandomLayers;
                var style = ImGui.GetStyle();
                var inner = style.ItemInnerSpacing.X;

                // Measure the action cluster first so the title can be ellipsized to the space that remains.
                var frameH = ImGui.GetFrameHeight();
                float starW, eyeW, revealW, linkW, syncW, importW, importGearW, bulkLayerW, variantW, shareW;
                using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
                {
                    shareW = ImGui.CalcTextSize(FontAwesomeIcon.Share.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    starW = ImGui.CalcTextSize(FontAwesomeIcon.Star.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    eyeW = ImGui.CalcTextSize((isHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye).ToIconString()).X
                         + (style.FramePadding.X * 2);
                    revealW = ImGui.CalcTextSize(FontAwesomeIcon.Sitemap.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    linkW = ImGui.CalcTextSize(FontAwesomeIcon.ExternalLinkAlt.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    syncW = ImGui.CalcTextSize(FontAwesomeIcon.CloudUploadAlt.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    importW = ImGui.CalcTextSize(FontAwesomeIcon.FileImport.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    importGearW = ImGui.CalcTextSize(FontAwesomeIcon.Tshirt.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    bulkLayerW = ImGui.CalcTextSize(FontAwesomeIcon.LayerGroup.ToIconString()).X
                          + (style.FramePadding.X * 2);
                    variantW = ImGui.CalcTextSize(FontAwesomeIcon.CodeBranch.ToIconString()).X
                          + (style.FramePadding.X * 2);
                }
                // Glamourer gets both the open-in-native-UI link and the sync button; Glamaholic (no native UI
                // concept - see GlamaholicService.OpenInNativeUi) only gets the sync button. Glamaholic and
                // Glamour Plate both get the import-into-Glamourer button. Only Glamourer designs can be used
                // as a layer at all (AllDesignsSorted), so the bulk-assign button only ever shows for those,
                // and only while the layers feature itself is switched on.

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (6f * ImGuiHelpers.GlobalScale));
                var iconRowX = ImGui.GetCursorPosX();
                ImGui.SetWindowFontScale(1.5f);
                var titleAvail = Math.Max(50f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X);
                var title = TextFit.Ellipsize(details.Name, titleAvail);
                ImGui.TextColored(UiTheme.GoldAccent, title);
                ImGui.SetWindowFontScale(1.0f);
                if (title != details.Name && ImGui.IsItemHovered())
                    ImGui.SetTooltip(details.Name);

                // The action icons sit on their own row under the title now, rather than squeezed
                // alongside it, so a long design name never has to compete with them for space.
                ImGui.SetCursorPosX(iconRowX);
                if (HeaderIconButton("favStar", FontAwesomeIcon.Star,
                        isFavourite ? UiTheme.FavouriteStar : UiTheme.FavouriteButtonOff,
                        new Vector2(starW, frameH)))
                {
                    if (isFavourite)
                        plugin.Configuration.FavouriteDesigns.Remove(id);
                    else
                        plugin.Configuration.FavouriteDesigns.Add(id);
                    plugin.Configuration.Save();
                    favouriteVersion++;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isFavourite ? "Click to remove from favourites" : "Click to add to favourites");

                ImGui.SameLine(0, inner);
                if (HeaderIconButton("hideEye", isHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye,
                        isHidden ? UiTheme.HiddenEyeOn : UiTheme.HiddenButtonOff,
                        new Vector2(eyeW, frameH)))
                {
                    if (isHidden)
                        plugin.Configuration.HiddenDesigns.Remove(id);
                    else
                        plugin.Configuration.HiddenDesigns.Add(id);
                    plugin.Configuration.Save();
                    hiddenVersion++;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(isHidden
                        ? "Hidden — click to show in the gallery and exports"
                        : "Click to hide from the gallery and exports");

                ImGui.SameLine(0, inner);
                if (HeaderIconButton("revealInTree", FontAwesomeIcon.Sitemap, null, new Vector2(revealW, frameH)))
                    RevealDesignInTree(id);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Show in the tree");

                ImGui.SameLine(0, inner);
                var currentVariant = plugin.Configuration.GetVariantInfo(id);
                if (HeaderIconButton("addVariant", FontAwesomeIcon.CodeBranch,
                        currentVariant != null ? UiTheme.GoldAccent : null, new Vector2(variantW, frameH)))
                {
                    variantPickerFilter = string.Empty;
                    ImGui.OpenPopup(AddVariantPopupId);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(currentVariant != null ? "Change this design's variant parent" : "Add as Variant");
                DrawAddVariantPopup(id);

                // Unlike the Import-to-Glamourer button below, this works for every source - it only
                // reads from CachedOutfit.Equipment, which every provider already fills in the same way.
                ImGui.SameLine(0, inner);
                if (HeaderIconButton("shareDesignCode", FontAwesomeIcon.Share, null, new Vector2(shareW, frameH)))
                {
                    ImGui.SetClipboardText(DesignShareCode.Encode(details.Name, details.Equipment));
                    Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Copied \"{details.Name}\" as a shareable design code.");
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Copy as a shareable design code (gear only)");

                if (isGlamourer)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("openGlamourer", FontAwesomeIcon.ExternalLinkAlt, null, new Vector2(linkW, frameH)))
                        plugin.Glamourer.OpenInGlamourer(id, details.Name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Open in Glamourer");

                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("importGear", FontAwesomeIcon.Tshirt, null, new Vector2(importGearW, frameH)))
                    {
                        importGearCreateNew = false;
                        ImGui.OpenPopup(ImportGearPopupId);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Import current gear - capture what you're actually wearing (including anything Glamourer is forcing), plus any Penumbra mods affecting it");
                }

                DrawImportGearPopup(id, details);

                if (showForceSync)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("forceSync", FontAwesomeIcon.CloudUploadAlt, null, new Vector2(syncW, frameH)))
                        ConfirmDialog.Open(ForceSyncPopupId);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(isGlamourer
                            ? "Force sync Tags and Description to the Glamourer design file"
                            : "Force sync Tags to the Glamaholic config file");

                    var forceSyncMessage = isGlamourer
                        ? $"This will overwrite the Description and Tags stored in the Glamourer design file for \"{details.Name}\" "
                          + "with what's shown here in Aetherfit, without touching anything else in the file.\n\n"
                          + "If this design is currently open in Glamourer's own editor, Glamourer may discard this change "
                          + "the moment you touch it there or on its next autosave. Close the design in Glamourer first."
                        : $"This will overwrite the Tags stored in the Glamaholic config file for \"{details.Name}\" "
                          + "with what's shown here in Aetherfit, without touching anything else in the file.";

                    if (ConfirmDialog.Draw(ForceSyncPopupId, forceSyncMessage, "Force Sync"))
                    {
                        if (isGlamourer)
                        {
                            var result = plugin.GlamourerDesignFile.PushMetadataToGlamourer(id, details.Description, details.Tags);
                            if (result.Success)
                                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Pushed Tags and Description to Glamourer for \"{details.Name}\"");
                            else
                                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
                        }
                        else
                        {
                            var result = plugin.Glamaholic.PushTagsToGlamaholic(details.ProviderDesignId, details.Tags);
                            if (result.Success)
                                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Pushed Tags to Glamaholic for \"{details.Name}\"");
                            else
                                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
                        }
                    }
                }

                if (showImport)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("importGlamourer", FontAwesomeIcon.FileImport, null, new Vector2(importW, frameH)))
                    {
                        importDesignName = isGlamaholic ? details.Name : string.Empty;
                        importDeleteFromGlamaholic = false;
                        importReclaimFocus = true;
                        ImGui.OpenPopup(ImportToGlamourerPopupId);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Import into Glamourer");
                }

                DrawImportToGlamourerPopup(id, details, isGlamaholic, hasGearOverride);

                if (showBulkLayer)
                {
                    ImGui.SameLine(0, inner);
                    if (HeaderIconButton("bulkLayerAssign", FontAwesomeIcon.LayerGroup, null, new Vector2(bulkLayerW, frameH)))
                        OpenBulkLayerAssignPopup(id);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Apply as a layer to multiple designs");
                }

                DrawBulkLayerAssignPopup();

                if (hasGearOverride)
                    DrawGearImportPendingBanner(id, details);

                ImGui.Spacing();

                DrawJobAssociations(id);

                if (plugin.Configuration.GetVariantInfo(id) is { } variant)
                    DrawVariantSection(id, variant);

                var childVariants = plugin.Configuration.GetVariantsOf(id).Select(kv => kv.Key).ToList();
                if (childVariants.Count > 0)
                    DrawVariantsOfSection(childVariants);

                if (Pills.DrawCollapsibleSubheader("Tags", ref tagsPanelOpen))
                {
                    ImGui.Indent();
                    if (!plugin.Configuration.CompositeTagsHelpDismissed)
                        DrawCompositeTagsHelpNote();
                    if (details.Tags.Count == 0)
                        ImGui.TextDisabled("This design has no tags set.");
                    DrawTagsRow(id, details);
                    ImGui.Spacing();
                    DrawTagSuggestionsBlock(id, details);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (Pills.DrawCollapsibleSubheader("Description", ref descriptionPanelOpen))
                {
                    ImGui.Indent();
                    DrawDescriptionEditor(id, details);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                if (Pills.DrawCollapsibleSubheader("Images", ref imagesPanelOpen, ImageHelpText))
                {
                    ImGui.Indent();
                    DrawImagesBlock(id);
                    ImGui.Unindent();
                    ImGui.Spacing();
                }

                DrawEquipmentPanel(id, details);
                DrawCustomizationsPanel(id, details);
                DrawDesignLinksPanel(details);
                if (details.Source is DesignSource.Glamourer or DesignSource.SimpleGlamourSwitcher)
                    DrawModsPanel(id, details);
                // Layers are applied via their own provider on top of whatever base was applied, so the
                // base design's source doesn't matter here - only Glamourer-sourced designs can be picked
                // as a layer (see AllDesignsSorted), not which designs can carry layers.
                if (plugin.Configuration.EnableRandomLayers)
                    DrawAdditionalLayersPanel(id);
            }
        }

        // Nudge the floating footer in one level so the dates line up with the indented content above.
        var sourceName = plugin.DesignProviders.FirstOrDefault(p => p.Source == details.Source)?.DisplayName
            ?? details.Source.ToString();
        var sourceText = $"Source: {sourceName}";

        // Last worn is always shown ("Never" rather than hidden - it's itself a meaningful, common
        // state), paired on a row with Created above Last edited/Source below, mirroring how Created
        // and Last worn both describe "when," same as Last edited and Source both sit on the bottom row.
        var lastWornText = details.LastAppliedAt is { } worn ? $"Last worn: {FormatFriendlyRelative(worn)}" : "Last worn: Never";
        var lastWornTooltip = details.LastAppliedAt is { } w ? FormatFullDate(w) : null;

        string? createdText = null, createdTooltip = null;
        if (details.CreatedAt is { } created)
        {
            createdText = $"Created: {FormatFriendlyRelative(created)}";
            createdTooltip = FormatFullDate(created);
        }

        string? lastEditedText = null, lastEditedTooltip = null;
        if (details.LastEdit is { } edited)
        {
            lastEditedText = $"Last edited: {FormatFriendlyRelative(edited)}";
            lastEditedTooltip = FormatFullDate(edited);
        }

        ImGui.Indent();
        DrawFooterRow(createdText, createdTooltip, lastWornText, lastWornTooltip);
        DrawFooterRow(lastEditedText, lastEditedTooltip, sourceText, null);
        ImGui.Unindent();
    }

    // A footer row: an optional left-aligned label (omitted entirely when null, e.g. a source with no
    // Created/Last edited of its own) and an always-present right-aligned label.
    private static void DrawFooterRow(string? leftText, string? leftTooltip, string rightText, string? rightTooltip)
    {
        if (leftText != null)
        {
            ImGui.TextDisabled(leftText);
            if (leftTooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(leftTooltip);
        }

        var rightW = ImGui.CalcTextSize(rightText).X;
        if (leftText != null)
        {
            var style = ImGui.GetStyle();
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + style.ItemSpacing.X,
                ImGui.GetContentRegionMax().X - rightW));
        }
        else
        {
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - rightW));
        }
        ImGui.TextDisabled(rightText);
        if (rightTooltip != null && ImGui.IsItemHovered())
            ImGui.SetTooltip(rightTooltip);
    }

    private static string FormatFriendlyRelative(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.Now - dt;
        if (diff.TotalSeconds < 0) return FormatFullDate(dt);
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 2) return "a minute ago";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} minutes ago";
        if (diff.TotalHours < 2) return "an hour ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hours ago";
        if (diff.TotalDays < 2) return "yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 14) return "last week";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 60) return "last month";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        if (diff.TotalDays < 730) return "last year";
        return $"{(int)(diff.TotalDays / 365)} years ago";
    }

    private static string FormatFullDate(DateTimeOffset dt) =>
        dt.LocalDateTime.ToString("dddd, MMMM d, yyyy 'at' h:mm tt");
}
