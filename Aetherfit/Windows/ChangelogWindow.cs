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
            "Added a robust automation system to automatically equip designs based on specific conditions",
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
