using System;
using System.Linq;
using System.Numerics;
using Aetherfit.Ui;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Aetherfit.Windows;

// Hand-authored, opt-in entries - NOT tied to Plugin.Version, which the release CI bumps on every
// build (including routine ones). Only add an entry (and bump its Revision) for something worth
// interrupting the user to announce.
public readonly record struct ChangelogEntry(int Revision, string Title, string[] Highlights);

internal static class ChangelogData
{
    public static readonly ChangelogEntry[] Entries =
    {
        new(1, "Batch Screenshot Mode", new[]
        {
            "Batch Screenshot mode: pick a set of designs by tag, job or source and Aetherfit applies each one in turn, waits, captures it with a fixed centered crop, and saves it as that design's cover - unattended.",
            "New equipment slot filters in design and gallery views.",
        }),
        new(2, "Automation and Slot Based Applications", new[]
        {
            "Added a robust automation system to automatically equip designs based on specific conditions",
            "Added the ability to select a one or more slots on a design to apply",
            "Added ability to do bulk tagging of designs in the gallery view",
            "Hopefully fixed a race condition that prevented the last known design from being applied on login at times"
        }),
    };

    public static int LatestRevision => Entries.Length == 0 ? 0 : Entries.Max(e => e.Revision);
}

public sealed class ChangelogWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ChangelogWindow(Plugin plugin)
        : base("What's New in Aetherfit##AetherfitChangelog")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(700, 800),
        };
        Size = new Vector2(480, 340);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
    }

    public void Dispose() { }

    // Fires on every IsOpen true->false transition (the X button and the "Got it" button below
    // both count) - either way, the user has seen it, so it shouldn't show again for this revision.
    public override void OnClose()
    {
        plugin.Configuration.LastSeenChangelogRevision = ChangelogData.LatestRevision;
        plugin.Configuration.Save();
    }

    public override void Draw()
    {
        var unseen = ChangelogData.Entries
            .Where(e => e.Revision > plugin.Configuration.LastSeenChangelogRevision)
            .OrderByDescending(e => e.Revision)
            .ToList();

        if (unseen.Count == 0)
        {
            ImGui.TextDisabled("You're all caught up.");
        }
        else
        {
            var first = true;
            foreach (var entry in unseen)
            {
                if (!first)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                }
                first = false;

                ImGui.TextColored(UiTheme.GoldAccent, entry.Title);
                ImGui.Spacing();
                foreach (var highlight in entry.Highlights)
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextWrapped(highlight);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Got it", new Vector2(-1, 0)))
            IsOpen = false;
    }
}
