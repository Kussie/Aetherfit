using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Game;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Newtonsoft.Json;

namespace Aetherfit.Windows;

public sealed class AutomationsWindow : Window, IDisposable
{
    private const string RuleDragType = "AF_AUTOMATION_RULE";
    private const int MaxVisibleRows = 15;

    private readonly Plugin plugin;
    private Guid? expandedRuleId;
    private string designPickerFilter = string.Empty;
    private string tagPoolPickerFilter = string.Empty;
    private readonly HashSet<string> tagPoolPickerSelected = new(StringComparer.OrdinalIgnoreCase);
    private int draggedRuleIndex = -1;

    public AutomationsWindow(Plugin plugin)
        : base("Automations##AetherfitAutomations")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 400),
            MaximumSize = new Vector2(760, 900),
        };
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public void OpenRule(Guid ruleId)
    {
        expandedRuleId = ruleId;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (!Plugin.PlayerState.IsLoaded)
        {
            ImGui.TextDisabled("Log in to a character to configure Automations.");
            return;
        }

        var settings = plugin.Configuration.GetOrCreateLoginSettings(Plugin.PlayerState.ContentId);

        DrawHeader(settings);
        ImGui.Separator();
        ImGui.Spacing();

        DrawRuleList(settings);

        ImGui.Spacing();
        if (ImGui.Button("Add Rule"))
        {
            var rule = new AutomationRule();
            settings.AutomationRules.Add(rule);
            plugin.Configuration.Save();
            expandedRuleId = rule.Id;
        }
        ImGui.SameLine();
        DrawImportRuleButton(settings);
    }

    private string importRuleCode = string.Empty;
    private string? importRuleError;

    private void DrawImportRuleButton(CharacterLoginSettings settings)
    {
        if (ImGui.Button("Import Rule from Code..."))
        {
            importRuleCode = string.Empty;
            importRuleError = null;
            ImGui.OpenPopup("##importRule");
        }

        using var popup = ImRaii.Popup("##importRule");
        if (!popup.Success)
            return;

        ImGui.TextDisabled("Paste a rule code below. Only conditions come across - you'll assign");
        ImGui.TextDisabled("your own designs to it afterwards.");
        ImGui.Spacing();

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(350 * ImGuiHelpers.GlobalScale);
        var submitted = ImGui.InputTextWithHint("##importRuleCode", "Paste code here...", ref importRuleCode, 8192,
            ImGuiInputTextFlags.EnterReturnsTrue);

        if (importRuleError != null)
            ImGui.TextColored(UiTheme.ErrorText, importRuleError);

        if ((submitted || ImGui.Button("Import")) && !string.IsNullOrWhiteSpace(importRuleCode))
        {
            if (AutomationRuleCode.TryDecode(importRuleCode, out var rule, out var error))
            {
                settings.AutomationRules.Add(rule!);
                plugin.Configuration.Save();
                expandedRuleId = rule!.Id;
                ImGui.CloseCurrentPopup();
            }
            else
            {
                importRuleError = error;
            }
        }
    }

    private void DrawHeader(CharacterLoginSettings settings)
    {
        var enabled = settings.AutomationsEnabled;
        if (ImGui.Checkbox("Enable Automations for this character", ref enabled))
        {
            settings.AutomationsEnabled = enabled;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Automatically applies designs based on the rules below (job, zone, mount, weather, "
                + "time, swimming). While on, this replaces the Settings → Login & Zoning login/zone actions -\n"
                + "they're skipped for this character.");

        ImGui.Indent();
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled)))
            ImGui.TextWrapped("Turn off Glamourer's own Automations for this character - Aetherfit's Automations "
                + "can't detect whether Glamourer's is still active, and the two will fight over what to apply.");

        if (settings.AutomationsEnabled)
        {
            var disableInDuties = settings.AutomationsDisableInDuties;
            if (ImGui.Checkbox("Don't change appearance while in a duty", ref disableInDuties))
            {
                settings.AutomationsDisableInDuties = disableInDuties;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Dungeons, trials, raids, alliance raids, etc. Automations never changes your "
                    + "appearance in combat regardless of this setting.");
        }
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.SectionHeader, "Status:");
        ImGui.SameLine();
        if (!settings.AutomationsEnabled)
            ImGui.TextColored(UiTheme.StateOff, "Off");
        else if (plugin.Automation.CurrentRuleName is { } activeName)
            ImGui.TextColored(UiTheme.StateOn, $"On — currently applying \"{activeName}\"");
        else
            ImGui.TextColored(UiTheme.GoldAccent, "On — no rule currently matches");
    }

    private void DrawRuleList(CharacterLoginSettings settings)
    {
        var rules = settings.AutomationRules;
        if (rules.Count == 0)
        {
            ImGui.TextDisabled("No rules yet. Click Add Rule to create one.");
            return;
        }

        // Rules apply top to bottom, first match wins - so a reorder or delete is deferred until after
        // the loop finishes, same as MainWindow.AdditionalLayers.cs's pendingLayerEdit.
        Action? pendingEdit = null;

        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            using var rowId = ImRaii.PushId(rule.Id.ToString());
            ImGui.Spacing();

            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextDisabled(FontAwesomeIcon.GripVertical.ToIconString());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Drag to reorder");
            ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);

            // The grip icon is plain text with no widget id of its own, so the drag source has to be
            // told explicitly not to require one (SourceAllowNullId) - otherwise BeginDragDropSource
            // silently refuses to start a drag from it at all.
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
            {
                draggedRuleIndex = i;
                ImGui.SetDragDropPayload(RuleDragType, ReadOnlySpan<byte>.Empty);
                ImGui.TextUnformatted(rule.Name);
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                if (!ImGui.AcceptDragDropPayload(RuleDragType).IsNull && draggedRuleIndex >= 0 && draggedRuleIndex != i)
                {
                    var from = draggedRuleIndex;
                    var to = i;
                    pendingEdit = () =>
                    {
                        var moved = rules[from];
                        rules.RemoveAt(from);
                        rules.Insert(to, moved);
                    };
                }
                ImGui.EndDragDropTarget();
            }
            ImGui.SameLine();

            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                rule.Enabled = enabled;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Enabled");
            ImGui.SameLine();

            var issues = plugin.Automation.GetRuleIssues(rule);
            var style = ImGui.GetStyle();
            float warningIconW, copyIconW, shareIconW, trashIconW;
            using (Plugin.PluginInterface.UiBuilder.IconFontFixedWidthHandle.Push())
            {
                warningIconW = ImGui.CalcTextSize(FontAwesomeIcon.ExclamationTriangle.ToIconString()).X;
                copyIconW = ImGui.CalcTextSize(FontAwesomeIcon.Copy.ToIconString()).X;
                shareIconW = ImGui.CalcTextSize(FontAwesomeIcon.Share.ToIconString()).X;
                trashIconW = ImGui.CalcTextSize(FontAwesomeIcon.Trash.ToIconString()).X;
            }
            // IconButton doesn't always come out to exactly glyph width + frame padding, so pad the
            // reservation a bit rather than clip the buttons if it's off by a couple pixels.
            var framePad2 = style.FramePadding.X * 2;
            var buttonsWidth = (copyIconW + framePad2 + style.ItemSpacing.X)
                + (shareIconW + framePad2 + style.ItemSpacing.X)
                + (trashIconW + framePad2 + style.ItemSpacing.X)
                + (8f * ImGuiHelpers.GlobalScale);
            var warningWidth = issues.Count > 0 ? warningIconW + style.ItemSpacing.X : 0f;
            var labelWidth = Math.Max(0f, ImGui.GetContentRegionAvail().X - buttonsWidth - warningWidth);
            var expanded = expandedRuleId == rule.Id;
            var chevron = expanded ? "▼" : "▶";
            // The condition/design summary is only useful while collapsed - once expanded, the full
            // editor below already shows exactly that, so repeating it here would just be noise.
            var poolCount = rule.DesignIds.Count + rule.TagPools.Count;
            var label = expanded
                ? $"{chevron} {rule.Name}##ruleLabel"
                : $"{chevron} {rule.Name}  —  {SummarizeConditions(rule)}, {poolCount} design(s)##ruleLabel";
            if (ImGui.Selectable(label, expanded, ImGuiSelectableFlags.None, new Vector2(labelWidth, 0)))
                expandedRuleId = expanded ? null : rule.Id;

            if (issues.Count > 0)
            {
                ImGui.SameLine();
                DesignDetailView.DrawFontAwesome(FontAwesomeIcon.ExclamationTriangle, UiTheme.ErrorText);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.Join("\n", issues));
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy))
            {
                var clone = JsonConvert.DeserializeObject<AutomationRule>(JsonConvert.SerializeObject(rule))!;
                clone.Id = Guid.NewGuid();
                clone.Name += " (Copy)";
                var insertAt = rules.IndexOf(rule) + 1;
                pendingEdit = () => rules.Insert(insertAt, clone);
                expandedRuleId = clone.Id;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Duplicate this rule");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Share))
            {
                ImGui.SetClipboardText(AutomationRuleCode.Encode(rule));
                Plugin.ChatGui.Print($"{Plugin.ChatPrefix}Copied a shareable code for \"{rule.Name}\" to your clipboard.");
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copy a shareable code for this rule's conditions - assigned designs "
                    + "aren't included, since they wouldn't mean anything on someone else's install.");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash) && ImGui.GetIO().KeyShift)
            {
                var toRemove = rule;
                pendingEdit = () =>
                {
                    rules.Remove(toRemove);
                    if (expandedRuleId == toRemove.Id)
                        expandedRuleId = null;
                };
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Shift and click to delete this rule.");

            if (expanded)
            {
                ImGui.Indent();
                ImGui.Spacing();
                DrawRuleEditor(rule);
                ImGui.Spacing();
                ImGui.Unindent();
            }

            ImGui.Spacing();
            ImGui.Separator();
        }

        if (pendingEdit != null)
        {
            pendingEdit();
            plugin.Configuration.Save();
        }
    }

    private static string SummarizeConditions(AutomationRule rule)
        => rule.Conditions.Count == 0 ? "no conditions" : string.Join(", ", rule.Conditions.Select(c => c.Type.ToString()));

    private string? DesignThumbnailPath(Guid id)
        => plugin.Configuration.ShowThumbnailOnHover ? plugin.ImageStorage.GetCoverPath(id) : null;

    private void DrawRuleEditor(AutomationRule rule)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Name:");
        ImGui.SameLine();
        var name = rule.Name;
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputTextWithHint("##ruleName", "Rule name...", ref name, 64))
        {
            rule.Name = name;
            plugin.Configuration.Save();
        }

        // Live dry-run against the current game state - independent of rule order/other rules/Enabled,
        // so this still works to sanity-check a rule before turning it on.
        var preview = plugin.Automation.PreviewRule(rule);

        ImGui.Spacing();
        ImGui.TextColored(UiTheme.SectionHeader, "Preview:");
        ImGui.SameLine();
        if (preview.WouldApply)
            ImGui.TextColored(UiTheme.StateOn, "Would apply right now");
        else
            ImGui.TextColored(UiTheme.PlaceholderText, "Would not apply right now");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(UiTheme.SectionHeader, "Conditions (all must match)");
        ImGui.Spacing();

        AutomationCondition? toRemoveCondition = null;
        AutomationCondition? toDuplicateCondition = null;
        foreach (var (condition, matched) in preview.ConditionResults)
        {
            using var condId = ImRaii.PushId(condition.GetHashCode());

            // A framed header bar per condition, same visual language as the panel headers elsewhere
            // (Pills.DrawCollapsibleSubheader) - makes it obvious at a glance where one condition ends
            // and the next begins, instead of the type name just blending into the row above/below it.
            var rowStart = ImGui.GetCursorScreenPos();
            var rowWidth = ImGui.GetContentRegionAvail().X;
            var rowHeight = ImGui.GetFrameHeight();
            ImGui.GetWindowDrawList().AddRectFilled(rowStart, rowStart + new Vector2(rowWidth, rowHeight),
                ImGui.GetColorU32(ImGuiCol.Header), ImGui.GetStyle().FrameRounding);
            ImGui.SetCursorScreenPos(rowStart + new Vector2(ImGui.GetStyle().FramePadding.X, 0));

            ImGui.AlignTextToFramePadding();
            DesignDetailView.DrawFontAwesome(matched ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, matched ? UiTheme.StateOn : UiTheme.StateOff);
            ImGui.SameLine();
            ImGui.TextColored(UiTheme.SectionHeader, condition.Type.ToString());
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy))
                toDuplicateCondition = condition;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Duplicate this condition");
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash) && ImGui.GetIO().KeyShift)
                toRemoveCondition = condition;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Shift and click to delete this condition.");

            ImGui.Spacing();
            ImGui.Indent();
            DrawConditionEditor(condition);
            ImGui.Unindent();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        if (toDuplicateCondition != null)
        {
            var clone = JsonConvert.DeserializeObject<AutomationCondition>(JsonConvert.SerializeObject(toDuplicateCondition))!;
            rule.Conditions.Insert(rule.Conditions.IndexOf(toDuplicateCondition) + 1, clone);
            plugin.Configuration.Save();
        }

        if (toRemoveCondition != null)
        {
            rule.Conditions.Remove(toRemoveCondition);
            plugin.Configuration.Save();
        }

        if (rule.Conditions.Count == 0)
        {
            ImGui.TextDisabled("No conditions yet - this rule will never match.");
            ImGui.Spacing();
        }

        if (ImGui.Button("Add Condition##addCondition"))
            ImGui.OpenPopup("##addConditionPopup");

        using (var popup = ImRaii.Popup("##addConditionPopup"))
        {
            if (popup.Success)
            {
                foreach (var type in Enum.GetValues<AutomationConditionType>())
                {
                    if (ImGui.Selectable(type.ToString()))
                    {
                        rule.Conditions.Add(new AutomationCondition { Type = type });
                        plugin.Configuration.Save();
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(UiTheme.SectionHeader, "Designs (one is picked at random when several match)");
        ImGui.Spacing();
        if (rule.DesignIds.Count > 0)
        {
            Pills.DrawRemovableRow(rule.DesignIds, plugin.Configuration.ResolveDesignName, id =>
            {
                rule.DesignIds.Remove(id);
                plugin.Configuration.Save();
            }, thumbnailPath: DesignThumbnailPath);
            ImGui.Spacing();
        }
        if (rule.TagPools.Count > 0)
        {
            Pills.DrawRemovableRow(rule.TagPools, DescribeTagPool, pool =>
            {
                rule.TagPools.Remove(pool);
                plugin.Configuration.Save();
            });
            ImGui.Spacing();
        }
        DrawAddDesignButton(rule);
        ImGui.SameLine();
        DrawAddTagPoolButton(rule);
    }

    private static string DescribeTagPool(List<string> tags) => $"Tag: {string.Join(" + ", tags)}";

    private void DrawConditionEditor(AutomationCondition condition)
    {
        switch (condition.Type)
        {
            case AutomationConditionType.Job: DrawJobConditionEditor(condition); break;
            case AutomationConditionType.Territory: DrawTerritoryConditionEditor(condition); break;
            case AutomationConditionType.Mounted: DrawMountedConditionEditor(condition); break;
            case AutomationConditionType.Weather: DrawWeatherConditionEditor(condition); break;
            case AutomationConditionType.Time: DrawTimeConditionEditor(condition); break;
            case AutomationConditionType.Swimming: DrawSwimmingConditionEditor(condition); break;
            case AutomationConditionType.Housing: DrawHousingConditionEditor(condition); break;
        }
    }

    private void DrawJobConditionEditor(AutomationCondition condition)
    {
        var preview = condition.JobIds.Count == 0
            ? "Select job(s)..."
            : string.Join(", ", condition.JobIds.Select(plugin.GameData.ResolveJobName));

        using var combo = ImRaii.Combo("##jobs", preview);
        if (!combo.Success)
            return;

        JobRole? lastRole = null;
        foreach (var job in plugin.GameData.GetSelectableJobs())
        {
            if (lastRole != job.Role)
            {
                ImGui.TextDisabled(GameDataService.RoleLabel(job.Role));
                lastRole = job.Role;
            }

            var on = condition.JobIds.Contains(job.RowId);
            if (ImGui.Checkbox($"{job.Name}##job{job.RowId}", ref on))
            {
                if (on) condition.JobIds.Add(job.RowId); else condition.JobIds.Remove(job.RowId);
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawTerritoryConditionEditor(AutomationCondition condition)
    {
        Pills.DrawRemovableRow(condition.TerritoryIds, plugin.GameData.ResolveTerritoryName, id =>
        {
            condition.TerritoryIds.Remove(id);
            plugin.Configuration.Save();
        });

        if (ImGui.Button("Add Current Zone"))
        {
            var current = Plugin.ClientState.TerritoryType;
            if (current != 0 && !condition.TerritoryIds.Contains(current))
            {
                condition.TerritoryIds.Add(current);
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawMountedConditionEditor(AutomationCondition condition)
    {
        if (ImGui.RadioButton("Not Mounted", !condition.MountedValue))
        {
            condition.MountedValue = false;
            plugin.Configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Mounted", condition.MountedValue))
        {
            condition.MountedValue = true;
            plugin.Configuration.Save();
        }

        if (!condition.MountedValue)
            return;

        ImGui.TextDisabled("Leave empty to match any mount.");
        Pills.DrawRemovableRow(condition.MountIds, plugin.GameData.ResolveMountName, id =>
        {
            condition.MountIds.Remove(id);
            plugin.Configuration.Save();
        });

        if (ImGui.Button("Add Current Mount"))
        {
            var current = plugin.GameData.GetCurrentMountId();
            if (current != 0 && !condition.MountIds.Contains(current))
            {
                condition.MountIds.Add(current);
                plugin.Configuration.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only works while you're actually mounted.");
    }

    private void DrawWeatherConditionEditor(AutomationCondition condition)
    {
        var weathers = plugin.GameData.GetAllWeathers();
        var listHeight = Math.Min(weathers.Count, 10) * ImGui.GetTextLineHeightWithSpacing();
        using var scroll = ImRaii.Child("##weatherList", new Vector2(-1, listHeight), false);
        foreach (var (ids, name) in weathers)
        {
            // Several row ids can share a name (see GetAllWeathers) - the checkbox tracks the whole group.
            var on = ids.Any(condition.WeatherIds.Contains);
            if (ImGui.Checkbox($"{name}##weather{ids[0]}", ref on))
            {
                if (on) condition.WeatherIds.AddRange(ids.Where(id => !condition.WeatherIds.Contains(id)));
                else condition.WeatherIds.RemoveAll(ids.Contains);
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawTimeConditionEditor(AutomationCondition condition)
    {
        var start = condition.StartHour;
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("From ET##startHour", ref start, 0, 23))
        {
            condition.StartHour = start;
            plugin.Configuration.Save();
        }

        ImGui.SameLine();
        var end = condition.EndHour;
        ImGui.SetNextItemWidth(140 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt("To ET##endHour", ref end, 0, 23))
        {
            condition.EndHour = end;
            plugin.Configuration.Save();
        }

        ImGui.TextDisabled("Wraps past midnight when \"To\" is earlier than or equal to \"From\".");
    }

    private void DrawSwimmingConditionEditor(AutomationCondition condition)
    {
        var swimming = condition.SwimStates.Contains(SwimState.Swimming);
        if (ImGui.Checkbox("Swimming (surface)##swimSurface", ref swimming))
        {
            if (swimming) condition.SwimStates.Add(SwimState.Swimming); else condition.SwimStates.Remove(SwimState.Swimming);
            plugin.Configuration.Save();
        }

        ImGui.SameLine();
        var diving = condition.SwimStates.Contains(SwimState.Diving);
        if (ImGui.Checkbox("Diving (underwater)##swimDiving", ref diving))
        {
            if (diving) condition.SwimStates.Add(SwimState.Diving); else condition.SwimStates.Remove(SwimState.Diving);
            plugin.Configuration.Save();
        }
    }

    private void DrawHousingConditionEditor(AutomationCondition condition)
    {
        Pills.DrawRemovableRow(condition.HousingTargets, DescribeHousingTarget, target =>
        {
            condition.HousingTargets.Remove(target);
            plugin.Configuration.Save();
        });
        if (condition.HousingTargets.Count > 0)
            ImGui.Spacing();

        if (ImGui.Button("Add Any House"))
            AddHousingTarget(condition, new HousingTarget { Type = HousingTargetType.AnyHouse });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Matches any personal or Free Company house/plot - not apartments.");
        ImGui.SameLine();

        if (ImGui.Button("Add Any Apartment"))
            AddHousingTarget(condition, new HousingTarget { Type = HousingTargetType.AnyApartment });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Matches any apartment room, in any building.");

        var housing = plugin.GameData.GetCurrentHousingState();

        using (ImRaii.Disabled(!housing.InHousing || housing.IsApartment))
        {
            if (ImGui.Button("Add Current Plot"))
                AddHousingTarget(condition, new HousingTarget
                {
                    Type = HousingTargetType.SpecificPlot,
                    TerritoryTypeId = housing.TerritoryTypeId,
                    Ward = housing.Ward,
                    Plot = housing.Plot,
                });
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(housing.InHousing && !housing.IsApartment
                ? "Match this exact plot, whether standing outside it or inside the house."
                : "Stand on or inside a personal house plot to capture it.");
        ImGui.SameLine();

        using (ImRaii.Disabled(!housing.InHousing || !housing.IsApartment))
        {
            if (ImGui.Button("Add Current Apartment"))
                AddHousingTarget(condition, new HousingTarget
                {
                    Type = HousingTargetType.SpecificApartmentRoom,
                    TerritoryTypeId = housing.TerritoryTypeId,
                    Ward = housing.Ward,
                    Room = housing.Room,
                });
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(housing.InHousing && housing.IsApartment
                ? "Match this exact apartment room."
                : "Be inside an apartment room to capture it.");
    }

    private void AddHousingTarget(AutomationCondition condition, HousingTarget target)
    {
        var exists = condition.HousingTargets.Any(t => t.Type == target.Type && t.TerritoryTypeId == target.TerritoryTypeId
            && t.Ward == target.Ward && t.Plot == target.Plot && t.Room == target.Room);
        if (exists)
            return;

        condition.HousingTargets.Add(target);
        plugin.Configuration.Save();
    }

    private string DescribeHousingTarget(HousingTarget target) => target.Type switch
    {
        HousingTargetType.AnyHouse => "Any house",
        HousingTargetType.AnyApartment => "Any apartment",
        HousingTargetType.SpecificPlot =>
            $"{plugin.GameData.ResolveTerritoryName(target.TerritoryTypeId)} — Ward {target.Ward + 1}, Plot {target.Plot + 1}",
        HousingTargetType.SpecificApartmentRoom =>
            $"{plugin.GameData.ResolveTerritoryName(target.TerritoryTypeId)} — Ward {target.Ward + 1}, Room {target.Room}",
        _ => "Unknown",
    };

    private void DrawAddDesignButton(AutomationRule rule)
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
        {
            designPickerFilter = string.Empty;
            ImGui.OpenPopup("##addDesignToRule");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add a design to this rule's pool.");

        using var popup = ImRaii.Popup("##addDesignToRule");
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##designFilter", "Filter by name...", ref designPickerFilter, 64);
        ImGui.Separator();

        var matches = plugin.Configuration.CachedOutfits
            .Where(kv => !rule.DesignIds.Contains(kv.Key)
                        && (designPickerFilter.Length == 0 || kv.Value.Name.Contains(designPickerFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => (kv.Key, kv.Value.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            ImGui.TextDisabled("No matching designs.");
            return;
        }

        var listHeight = Math.Min(matches.Count, MaxVisibleRows) * ImGui.GetTextLineHeightWithSpacing();
        using var scroll = ImRaii.Child("##addDesignList", new Vector2(250 * ImGuiHelpers.GlobalScale, listHeight), false);
        foreach (var (id, name) in matches)
        {
            if (ImGui.Selectable($"{name}##designAdd{id}"))
            {
                rule.DesignIds.Add(id);
                plugin.Configuration.Save();
                ImGui.CloseCurrentPopup();
            }
            if (ImGui.IsItemHovered() && DesignThumbnailPath(id) is { } imagePath)
                DrawDesignThumbnailTooltip(name, imagePath);
        }
    }

    private void DrawAddTagPoolButton(AutomationRule rule)
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Tags))
        {
            tagPoolPickerFilter = string.Empty;
            tagPoolPickerSelected.Clear();
            ImGui.OpenPopup("##addTagPoolToRule");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add a tag-based pool: whenever this rule applies, a random design matching every\n"
                + "checked tag is picked - the pool follows your tags automatically as they change.");

        using var popup = ImRaii.Popup("##addTagPoolToRule");
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##tagPoolFilter", "Search tags...", ref tagPoolPickerFilter, 64);
        ImGui.Separator();

        var allTags = plugin.Configuration.DistinctSortedTags()
            .Where(t => tagPoolPickerFilter.Length == 0 || t.Contains(tagPoolPickerFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (allTags.Count == 0)
        {
            ImGui.TextDisabled("No matching tags.");
        }
        else
        {
            var listHeight = Math.Min(allTags.Count, MaxVisibleRows) * ImGui.GetTextLineHeightWithSpacing();
            using var scroll = ImRaii.Child("##tagPoolList", new Vector2(250 * ImGuiHelpers.GlobalScale, listHeight), false);
            foreach (var tag in allTags)
            {
                var on = tagPoolPickerSelected.Contains(tag);
                if (ImGui.Checkbox($"{tag}##tagPool{tag}", ref on))
                {
                    if (on) tagPoolPickerSelected.Add(tag); else tagPoolPickerSelected.Remove(tag);
                }
            }
        }

        ImGui.Separator();
        using (ImRaii.Disabled(tagPoolPickerSelected.Count == 0))
        {
            if (ImGui.Button("Add##confirmTagPool", new Vector2(-1, 0)))
            {
                rule.TagPools.Add(tagPoolPickerSelected.ToList());
                plugin.Configuration.Save();
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private static void DrawDesignThumbnailTooltip(string name, string imagePath)
    {
        var tex = Plugin.TextureProvider.GetFromFile(imagePath).GetWrapOrEmpty();
        if (tex.Width <= 0 || tex.Height <= 0)
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(name);
        var (size, _) = GalleryDraw.ComputeFitSize(new Vector2(160f, 160f) * ImGuiHelpers.GlobalScale, tex.Width, tex.Height);
        ImGui.Image(tex.Handle, size);
        ImGui.EndTooltip();
    }
}
