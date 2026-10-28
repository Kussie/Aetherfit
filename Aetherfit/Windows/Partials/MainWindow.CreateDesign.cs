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

// The "Create new design" flow, reached from the toolbar's own Create dropdown (also triggered by
// QuickCreateWindow): From Worn (this file's own popup), From Code/Eorzea Collection
// (GalleryLiveShare.cs's). That same dropdown also offers Create Persona (MainWindow.Personas.cs's own
// popup) and Quick Create, neither of which QuickCreateWindow's own button needs to re-offer.
public partial class MainWindow
{
    private const string CreateNewDesignPopupId = "Create new design?##createNewDesignConfirm";
    private const string CreateNewDesignMenuPopupId = "##createNewDesignMenu";
    private string createNewDesignName = string.Empty;
    private bool createNewDesignReclaimFocus;
    private bool createNewDesignIncludeCustomizations;
    private bool createNewDesignAdvancedImport;
    private readonly HashSet<EquipmentSlot> createNewDesignExcludedSlots = new();
    private readonly HashSet<string> createNewDesignExcludedBonusSlots = new();
    private Guid? createNewDesignBaseId;
    private string createNewDesignBaseFilter = string.Empty;
    private List<CachedEquipmentSlot>? createNewDesignLiveEquipment;
    private List<CachedBonusItem>? createNewDesignLiveBonusItems;
    private bool createNewDesignPopupRequested;
    private bool createNewDesignMenuRequested;

    private bool createNewDesignMenuShowExtras;

    // Opened by the toolbar's Create button and QuickCreateWindow's own. Only ever sets a flag -
    // OpenPopup itself happens inside DrawCreateNewDesignMenu, since OpenPopup/BeginPopup have to share
    // the same ID-stack context (window/child) to match, and this is called from several different ones.
    // showExtras is false from QuickCreateWindow's own button - Create Persona and Quick Create itself
    // don't belong on a "quickly create a design" bar.
    internal void OpenCreateNewDesignMenu(bool showExtras = true)
    {
        createNewDesignMenuRequested = true;
        createNewDesignMenuShowExtras = showExtras;
    }

    private void DrawCreateNewDesignMenu()
    {
        if (createNewDesignMenuRequested)
        {
            createNewDesignMenuRequested = false;
            ImGui.OpenPopup(CreateNewDesignMenuPopupId);
        }

        using var popup = ImRaii.Popup(CreateNewDesignMenuPopupId);
        if (!popup.Success)
            return;

        ImGui.TextDisabled("Designs");
        if (ImGui.Selectable("From Worn"))
            RequestCreateNewDesignFromWorn();
        if (ImGui.Selectable("From Code / Eorzea Collection"))
            OpenImportDesignDialog();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Paste a design code (gear only) shared by another Aetherfit user, or an Eorzea Collection glamour link.");

        if (createNewDesignMenuShowExtras)
        {
            if (ImGui.Selectable("Quick Create..."))
            {
                plugin.OpenQuickCreateUi();
                IsOpen = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Create designs without the full Aetherfit window open.");

            if (Plugin.PlayerState.IsLoaded)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Personas");
                if (ImGui.Selectable("Create Persona"))
                    OpenCreatePersonaPopup();
            }
        }
    }

    // Popups need an active window's Begin/End, so both MainWindow and QuickCreateWindow call this from
    // their own Draw() - the frame guard stops it rendering (and ID-colliding) twice if both are open.
    private int createOverlaysFrame = -1;

    internal void DrawCreateOverlaysOnce()
    {
        var frame = ImGui.GetFrameCount();
        if (createOverlaysFrame == frame)
            return;
        createOverlaysFrame = frame;

        DrawCreateNewDesignMenu();
        DrawCreateNewDesignPopup();
        DrawImportDesignPopup();
        DrawCreatePersonaPopupOverlay();
    }

    // Shared by the tree leaf's own menu and the toolbar's Import -> Create Design submenu.
    private void RequestCreateNewDesignFromWorn()
    {
        createNewDesignName = string.Empty;
        createNewDesignReclaimFocus = true;
        createNewDesignIncludeCustomizations = false;
        createNewDesignAdvancedImport = false;
        createNewDesignExcludedSlots.Clear();
        createNewDesignExcludedBonusSlots.Clear();
        createNewDesignLiveEquipment = null;
        createNewDesignLiveBonusItems = null;
        // Falls back to "current appearance" if the configured default was since deleted.
        createNewDesignBaseId = plugin.Configuration.DefaultBaseDesignTemplate is { } defaultBaseId
                                 && plugin.Configuration.CachedOutfits.ContainsKey(defaultBaseId)
            ? defaultBaseId
            : null;
        createNewDesignBaseFilter = string.Empty;
        createNewDesignPopupRequested = true;
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
        DrawBaseDesignPicker("createNewDesignBase", ref createNewDesignBaseId, ref createNewDesignBaseFilter);

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
            DrawAdvancedImportSlotPicker("createNewDesign", createNewDesignLiveEquipment!,
                createNewDesignLiveBonusItems!, createNewDesignExcludedSlots, createNewDesignExcludedBonusSlots);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(!canConfirm))
        {
            if (ImGui.Button("Create") || (submitted && canConfirm))
            {
                ImGui.CloseCurrentPopup();
                DoCreateNewDesignFromScratch(trimmed, createNewDesignIncludeCustomizations,
                    createNewDesignAdvancedImport ? createNewDesignExcludedSlots : null,
                    createNewDesignAdvancedImport ? createNewDesignExcludedBonusSlots : null,
                    createNewDesignBaseId);
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

    // Lets the user exclude specific equipment slots (and the one bonus/facewear slot) from a capture
    // that would otherwise happen unconditionally - unworn slots aren't shown, since there's nothing
    // there to include or exclude either way. Shared by "Create new design" and "Import current gear"
    // onto an existing one - idSuffix keeps their scrollable list/widget ids independent.
    private void DrawAdvancedImportSlotPicker(string idSuffix, IReadOnlyList<CachedEquipmentSlot> liveEquipment,
        IReadOnlyList<CachedBonusItem> liveBonusItems, HashSet<EquipmentSlot> excludedSlots, HashSet<string> excludedBonusSlots)
    {
        var equipmentBySlot = liveEquipment.ToDictionary(e => e.Slot);
        var worn = DesignDetailView.SlotDisplay
            .Where(sd => equipmentBySlot.TryGetValue(sd.Slot, out var e)
                         && plugin.GameData.ResolveItemName(e.ItemId) != GameDataService.NothingItemName)
            .ToList();

        var bonusBySlot = liveBonusItems.ToDictionary(b => b.Slot);
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
        using var scroll = ImRaii.Child($"##advancedImportSlots{idSuffix}", new Vector2(-1, listHeight), true);
        foreach (var (slot, label) in worn)
        {
            var entry = equipmentBySlot[slot];
            var excluded = excludedSlots.Contains(slot);

            using (ImRaii.PushId((int)slot))
            {
                if (HeaderIconButton("advToggle", excluded ? FontAwesomeIcon.Times : FontAwesomeIcon.Check,
                        excluded ? UiTheme.ErrorText : UiTheme.StateOn, new Vector2(toggleW, frameH)))
                {
                    if (!excludedSlots.Remove(slot))
                        excludedSlots.Add(slot);
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
            var excluded = excludedBonusSlots.Contains(slotKey);

            using (ImRaii.PushId(slotKey))
            {
                if (HeaderIconButton("advToggle", excluded ? FontAwesomeIcon.Times : FontAwesomeIcon.Check,
                        excluded ? UiTheme.ErrorText : UiTheme.StateOn, new Vector2(toggleW, frameH)))
                {
                    if (!excludedBonusSlots.Remove(slotKey))
                        excludedBonusSlots.Add(slotKey);
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
        IReadOnlySet<EquipmentSlot>? excludedSlots, IReadOnlySet<string>? excludedBonusSlots = null, Guid? baseDesignId = null)
    {
        var result = plugin.GearImport.CreateFreshDesign(name, includeCustomizations, excludedSlots, excludedBonusSlots, baseDesignId);
        if (!result.Success)
        {
            Plugin.ChatGui.PrintError($"{Plugin.ChatPrefix}Create new design failed: {result.Error}");
            return;
        }

        if (baseDesignId is { } baseId)
            plugin.DesignApply.DuplicateDesignMetadata(baseId, result.NewId);

        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Created \"{name}\" from currently worn equipment.");
        selectedDesign = result.NewId;
        RefreshDesigns();
        DesignCreated?.Invoke(result.NewId);
    }

    // QuickCreateWindow's Snap/Open Aetherfit buttons key off this rather than selectedDesign, which
    // also changes for unrelated reasons (e.g. browsing the tree) while that window stays open.
    internal event Action<Guid>? DesignCreated;
}
