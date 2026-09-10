using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Aetherfit.Services.Game;
using Aetherfit.Ui;

namespace Aetherfit.Windows;

// The "Create new design" flow reached from the tree's own leaf: From Worn (this file's own popup),
// From Code/Eorzea Collection (MainWindow.GalleryLiveShare.cs's shared import popup).
public partial class MainWindow
{
    private const string CreateNewDesignPopupId = "Create new design?##createNewDesignConfirm";
    private string createNewDesignName = string.Empty;
    private bool createNewDesignReclaimFocus;
    private bool createNewDesignIncludeCustomizations;
    private bool createNewDesignAdvancedImport;
    private readonly HashSet<EquipmentSlot> createNewDesignExcludedSlots = new();
    private readonly HashSet<string> createNewDesignExcludedBonusSlots = new();
    private List<CachedEquipmentSlot>? createNewDesignLiveEquipment;
    private List<CachedBonusItem>? createNewDesignLiveBonusItems;
    private bool createNewDesignPopupRequested;

    // Always the last row in the tree regardless of grouping mode (called once, after whichever tree
    // draws) - a plain leaf with a + in place of the usual dot, opening a name-only creation popup.
    private void DrawCreateNewDesignLeaf()
    {
        if (ImGui.Selectable("   Create new design##createNewDesignLeaf"))
            ImGui.OpenPopup("##createNewDesignMenu");

        DrawCreateLeafPlusIcon();

        DrawCreateNewDesignMenu();
        DrawCreateNewDesignPopup();
    }

    // Offered from the same click that used to jump straight into "From Worn". "From Worn" only sets a
    // flag rather than calling ImGui.OpenPopup(CreateNewDesignPopupId) directly - the modal is drawn from
    // DrawCreateNewDesignPopup, one scope up from this menu's own popup, so calling OpenPopup from in here
    // would compute the wrong ID (the exact ID-stack scoping bug the Personas "Create new persona" popup
    // hit earlier) and the modal would silently never open. "From Code / Eorzea Collection" reuses the
    // existing flag-based trigger untouched (OpenImportDesignDialog), which has no such issue since its
    // own draw call already lives at MainWindow's stable top-level location regardless of caller depth.
    private void DrawCreateNewDesignMenu()
    {
        using var popup = ImRaii.Popup("##createNewDesignMenu");
        if (!popup.Success)
            return;

        if (ImGui.Selectable("From Worn"))
        {
            createNewDesignName = string.Empty;
            createNewDesignReclaimFocus = true;
            createNewDesignIncludeCustomizations = false;
            createNewDesignAdvancedImport = false;
            createNewDesignExcludedSlots.Clear();
            createNewDesignExcludedBonusSlots.Clear();
            createNewDesignLiveEquipment = null;
            createNewDesignLiveBonusItems = null;
            createNewDesignPopupRequested = true;
        }
        if (ImGui.Selectable("From Code / Eorzea Collection"))
            OpenImportDesignDialog();
    }

    private void DrawCreateNewDesignPopup()
    {
        if (createNewDesignPopupRequested)
        {
            createNewDesignPopupRequested = false;
            ImGui.OpenPopup(CreateNewDesignPopupId);
        }

        ImGui.SetNextWindowSize(new Vector2(420, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.Always);
        using var modal = ImRaii.PopupModal(CreateNewDesignPopupId, ImGuiWindowFlags.NoResize);
        if (!modal.Success)
            return;

        ImGui.TextWrapped("This creates a brand-new Glamourer design from your currently-equipped gear "
            + "and any active Penumbra mods affecting them.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Design name");
        if (ImGui.IsWindowAppearing() || createNewDesignReclaimFocus)
        {
            ImGui.SetKeyboardFocusHere();
            createNewDesignReclaimFocus = false;
        }
        ImGui.SetNextItemWidth(-1);
        var submitted = ImGui.InputTextWithHint("##createNewDesignName", "Design name (required)", ref createNewDesignName, 128,
            ImGuiInputTextFlags.EnterReturnsTrue);
        var trimmed = createNewDesignName.Trim();
        var canConfirm = trimmed.Length > 0;

        ImGui.Spacing();
        ImGui.Checkbox("Also include customizations (race, face, hair, etc.)", ref createNewDesignIncludeCustomizations);

        ImGui.Spacing();
        if (ImGui.Checkbox("Advanced Import", ref createNewDesignAdvancedImport) && createNewDesignAdvancedImport)
            EnsureLiveEquipmentPreviewLoaded();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Choose which equipment slots to include in the new design.");

        if (createNewDesignAdvancedImport)
        {
            EnsureLiveEquipmentPreviewLoaded();
            ImGui.Spacing();
            DrawAdvancedImportSlotPicker();
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Create") || (submitted && canConfirm))
            {
                ImGui.CloseCurrentPopup();
                DoCreateNewDesignFromScratch(trimmed, createNewDesignIncludeCustomizations,
                    createNewDesignAdvancedImport ? createNewDesignExcludedSlots : null,
                    createNewDesignAdvancedImport ? createNewDesignExcludedBonusSlots : null);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
    }

    private void EnsureLiveEquipmentPreviewLoaded()
    {
        if (createNewDesignLiveEquipment != null)
            return;

        var preview = plugin.GearImport.GetLiveEquipmentForPreview();
        createNewDesignLiveEquipment = preview.Success ? preview.Equipment.ToList() : new List<CachedEquipmentSlot>();
        createNewDesignLiveBonusItems = preview.Success ? preview.BonusItems.ToList() : new List<CachedBonusItem>();
    }

    // Lets the user exclude specific equipment slots (and the one bonus/facewear slot) from the design
    // CreateFreshDesign would otherwise capture unconditionally - unworn slots aren't shown, since
    // there's nothing there to include or exclude either way.
    private void DrawAdvancedImportSlotPicker()
    {
        if (createNewDesignLiveEquipment == null)
            return;

        var equipmentBySlot = createNewDesignLiveEquipment.ToDictionary(e => e.Slot);
        var worn = DesignDetailView.SlotDisplay
            .Where(sd => equipmentBySlot.TryGetValue(sd.Slot, out var e)
                         && plugin.GameData.ResolveItemName(e.ItemId) != GameDataService.NothingItemName)
            .ToList();

        var bonusBySlot = (createNewDesignLiveBonusItems ?? new List<CachedBonusItem>()).ToDictionary(b => b.Slot);
        var wornBonus = DesignDetailView.BonusSlotDisplay
            .Where(bd => bonusBySlot.TryGetValue(bd.SlotKey, out var b)
                         && plugin.GameData.ResolveBonusItemName(bd.SlotKey, b.ItemId) != GameDataService.NothingItemName)
            .ToList();

        if (worn.Count == 0 && wornBonus.Count == 0)
        {
            ImGui.TextDisabled("Nothing currently equipped.");
            return;
        }

        float toggleW;
        using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            toggleW = ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
        var frameH = ImGui.GetFrameHeight();
        var labelWidth = 90f * ImGuiHelpers.GlobalScale;

        var totalRows = worn.Count + wornBonus.Count;
        var listHeight = Math.Min(totalRows, MaxVisibleDesignRows) * ImGui.GetFrameHeightWithSpacing();
        using var scroll = ImRaii.Child("##advancedImportSlots", new Vector2(-1, listHeight), true);
        foreach (var (slot, label) in worn)
        {
            var entry = equipmentBySlot[slot];
            var excluded = createNewDesignExcludedSlots.Contains(slot);

            using (ImRaii.PushId((int)slot))
            {
                if (HeaderIconButton("advToggle", excluded ? FontAwesomeIcon.Times : FontAwesomeIcon.Check,
                        excluded ? UiTheme.ErrorText : UiTheme.StateOn, new Vector2(toggleW, frameH)))
                {
                    if (!createNewDesignExcludedSlots.Remove(slot))
                        createNewDesignExcludedSlots.Add(slot);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(excluded ? "Excluded - click to include" : "Included - click to exclude");
            }

            ImGui.SameLine();
            var itemName = plugin.GameData.ResolveItemName(entry.ItemId);
            DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
                entry.Stain, entry.Stain2, entry.ApplyStain, applied: !excluded, EmptyAffectedMap);
        }

        foreach (var (slotKey, label) in wornBonus)
        {
            var entry = bonusBySlot[slotKey];
            var excluded = createNewDesignExcludedBonusSlots.Contains(slotKey);

            using (ImRaii.PushId(slotKey))
            {
                if (HeaderIconButton("advToggle", excluded ? FontAwesomeIcon.Times : FontAwesomeIcon.Check,
                        excluded ? UiTheme.ErrorText : UiTheme.StateOn, new Vector2(toggleW, frameH)))
                {
                    if (!createNewDesignExcludedBonusSlots.Remove(slotKey))
                        createNewDesignExcludedBonusSlots.Add(slotKey);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(excluded ? "Excluded - click to include" : "Included - click to exclude");
            }

            ImGui.SameLine();
            var itemName = plugin.GameData.ResolveBonusItemName(slotKey, entry.ItemId);
            DesignDetailView.DrawSlotRow(plugin.GameData, label, labelWidth, itemName,
                0, 0, false, applied: !excluded, EmptyAffectedMap);
        }
    }

    private static readonly Dictionary<string, string> EmptyAffectedMap = new();

    private void DoCreateNewDesignFromScratch(string name, bool includeCustomizations,
        IReadOnlySet<EquipmentSlot>? excludedSlots, IReadOnlySet<string>? excludedBonusSlots = null)
    {
        var result = plugin.GearImport.CreateFreshDesign(name, includeCustomizations, excludedSlots, excludedBonusSlots);
        if (!result.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Create new design failed: {result.Error}");
            return;
        }

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Created \"{name}\" from currently worn equipment.");
        selectedDesign = result.NewId;
        RefreshDesigns();
    }
}
