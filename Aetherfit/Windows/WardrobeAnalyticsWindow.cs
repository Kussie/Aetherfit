using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Designs;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

public sealed class WardrobeAnalyticsWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const int MaxListRows = 15;
    private const int MaxDistributionRows = 20;

    private bool mostWornOpen = true;
    private bool leastWornOpen = true;
    private bool neverWornOpen = true;
    private bool tagDistributionOpen = true;
    private bool modUsageOpen = true;
    private bool sourceBreakdownOpen = true;

    public WardrobeAnalyticsWindow(Plugin plugin)
        : base("Wardrobe Analytics##AetherfitWardrobeAnalytics")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(900, 900),
        };
        Size = new Vector2(520, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    private WardrobeAnalyticsService.Report cachedReport =
        new(0, Array.Empty<WardrobeAnalyticsService.WornEntry>(), Array.Empty<WardrobeAnalyticsService.WornEntry>(),
            Array.Empty<WardrobeAnalyticsService.NeverWornEntry>(), Array.Empty<WardrobeAnalyticsService.TagCount>(),
            Array.Empty<WardrobeAnalyticsService.ModUsage>(), Array.Empty<WardrobeAnalyticsService.SourceCount>(), null, null);
    private int cachedGeneration = -1;
    private int cachedLastAppliedVersion = -1;

    // Wearing a design bumps LastAppliedVersion, not DesignListGeneration - without keying on both,
    // Most/Least Worn and Never Worn would show stale data until the next full refresh.
    private void RefreshIfStale()
    {
        var generation = plugin.MainWindow.DesignListGeneration;
        var appliedVersion = plugin.Configuration.LastAppliedVersion;
        if (cachedGeneration == generation && cachedLastAppliedVersion == appliedVersion)
            return;

        cachedReport = plugin.WardrobeAnalytics.BuildReport();
        cachedGeneration = generation;
        cachedLastAppliedVersion = appliedVersion;
    }

    public override void Draw()
    {
        RefreshIfStale();
        var report = cachedReport;

        ImGui.TextDisabled($"{report.TotalDesigns} design{(report.TotalDesigns == 1 ? "" : "s")} • {report.NeverWorn.Count} never worn");
        if (report.Oldest != null)
            ImGui.TextDisabled($"Oldest: {report.Oldest.Name} ({report.Oldest.CreatedAt:d})");
        if (report.Newest != null)
            ImGui.TextDisabled($"Newest: {report.Newest.Name} ({report.Newest.CreatedAt:d})");
        ImGui.Spacing();

        DrawWornSection("Most Worn", ref mostWornOpen, report.MostWorn);
        ImGui.Spacing();
        DrawWornSection("Least Worn", ref leastWornOpen, report.LeastWorn);
        ImGui.Spacing();
        DrawNeverWornSection(report.NeverWorn);
        ImGui.Spacing();
        DrawDistributionSection("Tag Distribution", ref tagDistributionOpen, report.TagDistribution, t => t.Tag, t => t.Count);
        ImGui.Spacing();
        DrawDistributionSection("Mod Usage", ref modUsageOpen, report.ModUsage, m => m.DisplayName, m => m.Count);
        ImGui.Spacing();
        DrawDistributionSection("Source Breakdown", ref sourceBreakdownOpen, report.SourceBreakdown, s => s.DisplayName, s => s.Count);
    }

    private void DrawWornSection(string label, ref bool open, IReadOnlyList<WardrobeAnalyticsService.WornEntry> entries)
    {
        if (!Pills.DrawCollapsibleSubheader($"{label} ({entries.Count})", ref open))
            return;

        ImGui.Indent();
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("Nothing worn yet.");
        }
        else
        {
            foreach (var entry in entries)
            {
                using var rowId = ImRaii.PushId(entry.Id.ToString());
                DrawDesignNameCell(entry.Id, entry.Name);
                ImGui.SameLine();
                var lastWorn = entry.LastAppliedAt is { } last ? $" — last {last:d}" : "";
                ImGui.TextDisabled($"— {entry.WornCount}×{lastWorn}");
            }
        }
        ImGui.Unindent();
    }

    private void DrawNeverWornSection(IReadOnlyList<WardrobeAnalyticsService.NeverWornEntry> entries)
    {
        if (!Pills.DrawCollapsibleSubheader($"Never Worn ({entries.Count})", ref neverWornOpen))
            return;

        ImGui.Indent();
        if (entries.Count == 0)
        {
            ImGui.TextDisabled("Everything's been worn at least once.");
        }
        else
        {
            var listHeight = Math.Min(entries.Count, MaxListRows) * ImGui.GetTextLineHeightWithSpacing();
            using var scroll = ImRaii.Child("##neverWornList", new Vector2(-1, listHeight), false);
            foreach (var entry in entries)
            {
                using var rowId = ImRaii.PushId(entry.Id.ToString());
                DrawDesignNameCell(entry.Id, entry.Name);
            }
        }
        ImGui.Unindent();
    }

    private static void DrawDistributionSection<T>(string label, ref bool open, IReadOnlyList<T> items,
        Func<T, string> getLabel, Func<T, int> getCount)
    {
        if (!Pills.DrawCollapsibleSubheader($"{label} ({items.Count})", ref open))
            return;

        ImGui.Indent();
        if (items.Count == 0)
        {
            ImGui.TextDisabled("Nothing to show.");
        }
        else
        {
            var maxCount = items.Max(getCount);
            foreach (var item in items.Take(MaxDistributionRows))
            {
                var count = getCount(item);
                var fraction = maxCount > 0 ? count / (float)maxCount : 0f;
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{getLabel(item)} ({count})");
            }
            if (items.Count > MaxDistributionRows)
                ImGui.TextDisabled($"+{items.Count - MaxDistributionRows} more");
        }
        ImGui.Unindent();
    }

    private void DrawDesignNameCell(Guid id, string name)
    {
        DesignDetailView.TextColoredUnformatted(UiTheme.ModLink, name);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip("Click to open in Aetherfit");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                plugin.MainWindow.OpenDesign(id);
        }
    }
}
