using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Integrations;
using Aetherfit.Utils;

namespace Aetherfit.Windows;

// Shared by the tree (MainWindow.Tree.cs) and the gallery grid (MainWindow.GalleryMode.cs).
// fromGallery only gates "Open in Edit Mode" - you're already there in the tree.
public partial class MainWindow
{
    private void DrawDesignContextMenu(DesignLeaf design, bool fromGallery)
    {
        if (ImGui.MenuItem("Apply"))
        {
            selectedDesign = design.Id;
            ApplyDesignById(design.Id);
        }
        DrawApplySingleSlotSubmenu(design);

        DrawAssignToPersonaSubmenu(design.Id);
        DrawSetAsDefaultDesignSubmenu(design.Id);

        var isFavourite = plugin.Configuration.FavouriteDesigns.Contains(design.Id);
        if (ImGui.MenuItem(isFavourite ? "Remove from Favourites" : "Add to Favourites"))
        {
            if (isFavourite)
                plugin.Configuration.FavouriteDesigns.Remove(design.Id);
            else
                plugin.Configuration.FavouriteDesigns.Add(design.Id);
            plugin.Configuration.Save();
            favouriteVersion++;
        }

        var isHidden = plugin.Configuration.HiddenDesigns.Contains(design.Id);
        if (ImGui.MenuItem(isHidden ? "Unhide" : "Hide"))
        {
            if (isHidden)
                plugin.Configuration.HiddenDesigns.Remove(design.Id);
            else
                plugin.Configuration.HiddenDesigns.Add(design.Id);
            plugin.Configuration.Save();
            hiddenVersion++;
        }

        if (ImGui.MenuItem("Duplicate"))
        {
            var result = plugin.DesignApply.DuplicateDesign(design.Id);
            if (result.Error != null)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
            else
            {
                RefreshDesigns();
                selectedDesign = result.DesignId;
            }
        }
        if (ImGui.MenuItem("Duplicate as Variant"))
        {
            var result = plugin.DesignApply.DuplicateDesign(design.Id);
            if (result.Error != null)
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}{result.Error}");
            else
            {
                plugin.Configuration.SetVariantParent(result.DesignId!.Value, design.Id);
                plugin.Configuration.Save();
                RefreshDesigns();
                selectedDesign = result.DesignId;
            }
        }

        DrawExportMenu(design);

        ImGui.Separator();

        if (fromGallery && ImGui.MenuItem("Open in Edit Mode"))
        {
            selectedDesign = design.Id;
            RevealDesignInTree(design.Id);
        }

        if (plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var outfit)
            && outfit.Source == DesignSource.Glamourer
            && ImGui.MenuItem("Open in Glamourer"))
            plugin.Glamourer.OpenInGlamourer(design.Id, design.DisplayName);
    }

    // Same two formats the detail pane's own header icon buttons produce - just reachable from a
    // right-click without opening the design first.
    private void DrawExportMenu(DesignLeaf design)
    {
        if (!plugin.Configuration.CachedOutfits.TryGetValue(design.Id, out var outfit))
            return;

        using var menu = ImRaii.Menu("Export");
        if (!menu.Success)
            return;

        if (ImGui.MenuItem("Shareable Code"))
        {
            ImGui.SetClipboardText(DesignShareCode.Encode(outfit.Name, outfit.Equipment));
            Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Copied \"{outfit.Name}\" as a shareable design code.");
        }

        if (ImGui.MenuItem("Image Card"))
        {
            var coverPath = plugin.ImageStorage.GetCoverPath(design.Id);
            var modNames = outfit.Mods.Where(m => m.State == ModState.Enabled)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(m => m.Name).ToList();
            using var card = plugin.ShareCard.RenderCard(outfit.Name, outfit.Tags, modNames, outfit.Description, coverPath);
            if (ClipboardImageService.TryCopyToClipboard(card))
            {
                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Copied \"{outfit.Name}\" as an image card to the clipboard.");
                Plugin.NotificationManager.AddNotification(new Notification
                {
                    Content = "Image copied to clipboard",
                    Title = "Aetherfit",
                    Type = NotificationType.Success,
                });
            }
            else
            {
                Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Failed to copy the image card to the clipboard.");
                Plugin.NotificationManager.AddNotification(new Notification
                {
                    Content = "Failed to copy image to clipboard",
                    Title = "Aetherfit",
                    Type = NotificationType.Error,
                });
            }
        }
    }
}
