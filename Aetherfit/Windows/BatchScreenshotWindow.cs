using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Aetherfit.Services.Game;
using Aetherfit.Services.Integrations;
using Aetherfit.Ui;
using Aetherfit.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SixLabors.ImageSharp;

namespace Aetherfit.Windows;

// Applies each of a chosen set of designs in turn, waits a fixed (user-adjustable) delay for mods
// to finish loading in, captures a screenshot, and saves it as that design's cover - unattended.
// The delay itself is timed off a Framework.Update subscription (the same proven-reliable pattern
// Plugin.cs uses for keybind polling), added only while a job is actively waiting and removed the
// moment it fires or the job is cancelled - everything else is a direct, synchronous call chain.
public sealed class BatchScreenshotWindow : Window, IDisposable
{
    private enum Phase
    {
        Applying,
        Waiting,
        Capturing,
        Cropping,
        Saving,
    }

    private sealed class Job
    {
        public required List<Guid> Ids { get; init; }
        public int Index;
        public Phase Phase;
        public int Succeeded;
        public int Failed;
    }

    private const string StartConfirmPopupId = "Start Batch Screenshot?##batchScreenshotConfirm";
    private const string FilterPopupId = "BatchScreenshotFilterPopup";
    private const int MaxPreviewRows = 15;

    private readonly Plugin plugin;

    // Setup-phase filter/selection state.
    private readonly Dictionary<string, bool> filterTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, bool> filterJobs = new();
    private readonly HashSet<DesignSource> filterSources = new();
    private bool skipAlreadyCovered = true;
    private readonly HashSet<Guid> selectedIds = new();
    private string filterName = string.Empty;
    private string tagSearchText = string.Empty;
    private bool showFramingGuide;
    // How long to wait after applying a design before capturing it - adjustable since how long mods
    // take to finish loading in varies a lot per system/library, and isn't reliably signalled back to us.
    private float captureDelaySeconds = 3f;

    // Execution-phase state.
    private Job? job;
    private int generation;
    private bool windowWasOpenBeforeCapture;
    private bool hidingForCapture;
    private DateTime waitUntilUtc;
    private bool waitingOnFrameworkUpdate;

    public BatchScreenshotWindow(Plugin plugin)
        : base("Batch Screenshot##AetherfitBatchScreenshot")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 380),
            MaximumSize = new Vector2(700, 900),
        };
        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() => CancelJob();

    // Setting IsOpen = false to hide the window for a capture (see RunCapture) triggers this same
    // callback, so it can't unconditionally cancel - hidingForCapture distinguishes "we did this to
    // ourselves for a screenshot" from the user actually closing the window mid-batch.
    public override void OnClose()
    {
        if (hidingForCapture)
            return;
        CancelJob();
    }

    public override void Draw()
    {
        if (job is { } activeJob)
            DrawProgress(activeJob);
        else
            DrawSetup();
    }

    private void DrawSetup()
    {
        var enabledProviders = plugin.DesignProviders.Where(p => plugin.Configuration.IsProviderEnabled(p.Source)).ToList();
        if (filterSources.Count == 0)
            foreach (var provider in enabledProviders)
                filterSources.Add(provider.Source);

        ImGui.TextWrapped("Pick a set of designs below, then start the batch. Aetherfit will apply "
            + "each one, wait, capture it, and save the result as that design's cover image - "
            + "unattended. For consistent framing, consider entering GPose first.");
        ImGui.Spacing();

        DrawSubheader("Filter");
        ImGui.Indent();

        var filterCount = filterTags.Count + filterJobs.Count;
        if (ImGui.Button(Pills.TagJobFilterLabel(filterCount)))
        {
            tagSearchText = string.Empty;
            ImGui.OpenPopup(FilterPopupId);
        }
        DrawFilterPopup();

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##batchNameFilter", "Filter by name...", ref filterName, 64);

        ImGui.Spacing();
        ImGui.TextDisabled("Sources:");
        foreach (var provider in enabledProviders)
        {
            var on = filterSources.Contains(provider.Source);
            if (ImGui.Checkbox($"{provider.DisplayName}##batchSource{provider.Source}", ref on))
            {
                if (on) filterSources.Add(provider.Source);
                else filterSources.Remove(provider.Source);
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.Checkbox("Skip designs that already have a cover", ref skipAlreadyCovered);
        ImGui.Unindent();
        ImGui.Spacing();

        var candidates = plugin.Configuration.CachedOutfits
            .Where(kv => plugin.Configuration.IsProviderEnabled(kv.Value.Source)
                      && filterSources.Contains(kv.Value.Source)
                      && (filterName.Length == 0 || kv.Value.Name.Contains(filterName, StringComparison.OrdinalIgnoreCase))
                      && filterTags.MatchesFilter((IReadOnlyCollection<string>?)kv.Value.Tags ?? Array.Empty<string>())
                      && filterJobs.MatchesFilter(plugin.Configuration.GetJobAssociations(kv.Key))
                      && (!skipAlreadyCovered || !plugin.ImageStorage.HasCover(kv.Key)))
            .Select(kv => (Id: kv.Key, kv.Value.Name))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Selection persists across filter changes - only drop a selected id once the design itself
        // is actually gone, not just filtered out of view.
        selectedIds.RemoveWhere(id => !plugin.Configuration.CachedOutfits.ContainsKey(id));

        DrawSubheader($"Matches ({candidates.Count})");
        ImGui.Indent();

        if (ImGui.Button("Select All Visible"))
            foreach (var c in candidates) selectedIds.Add(c.Id);
        ImGui.SameLine();
        if (ImGui.Button("Deselect All Visible"))
            foreach (var c in candidates) selectedIds.Remove(c.Id);

        if (candidates.Count == 0)
        {
            ImGui.TextDisabled("No designs match this filter.");
        }
        else
        {
            var style = ImGui.GetStyle();
            var rowH = ImGui.GetTextLineHeightWithSpacing();
            var reserved = (2 * ImGui.GetFrameHeightWithSpacing()) + ImGui.GetFrameHeightWithSpacing() + (style.ItemSpacing.Y * 3);
            var maxListHeight = Math.Min(candidates.Count, MaxPreviewRows) * rowH;
            var listHeight = Math.Max(rowH * 3, Math.Min(maxListHeight, ImGui.GetContentRegionAvail().Y - reserved));

            using (ImRaii.Child("##batchScreenshotPreview", new Vector2(-1, listHeight), true))
            {
                foreach (var (id, name) in candidates)
                {
                    var isSelected = selectedIds.Contains(id);
                    if (ImGui.Checkbox($"{name}##batchDesign{id}", ref isSelected))
                    {
                        if (isSelected) selectedIds.Add(id);
                        else selectedIds.Remove(id);
                    }
                }
            }
        }
        ImGui.Unindent();
        ImGui.Spacing();

        ImGui.Checkbox("Show framing guide", ref showFramingGuide);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draws a box over the game window showing exactly what the fixed crop "
                + "will capture, so you can line up your camera and character before starting.");
        if (showFramingGuide)
            DrawFramingGuideOverlay();

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.SliderFloat("Delay before capture (seconds)", ref captureDelaySeconds, 0.5f, 15f, "%.1f");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How long to wait after applying a design before taking the screenshot - "
                + "give mods enough time to finish loading in.");

        ImGui.Spacing();
        ImGui.Separator();

        var selectedCount = selectedIds.Count;
        using (ImRaii.Disabled(selectedCount == 0))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, $"Start Batch ({selectedCount})", Vector2.Zero))
                ConfirmDialog.Open(StartConfirmPopupId);
        }

        if (ConfirmDialog.Draw(StartConfirmPopupId,
                $"This will capture a new cover image for {selectedCount} selected design(s). If a design "
                + "already has a cover, the existing one is kept as an additional image rather than deleted. "
                + "Aetherfit will apply each one in turn, wait, then screenshot and save it - this can take "
                + "a while and will repeatedly hide the main window.",
                "Start Batch", holdToConfirm: true))
        {
            StartJob(selectedIds.Where(id => plugin.Configuration.CachedOutfits.ContainsKey(id)).ToList());
        }
    }

    private void DrawFilterPopup()
    {
        using var popup = ImRaii.Popup(FilterPopupId);
        if (!popup.Success)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();

        var scale = ImGuiHelpers.GlobalScale;
        var allTags = plugin.Configuration.DistinctSortedTags();
        var allJobs = plugin.GameData.GetSelectableJobs();

        ImGui.SetNextItemWidth(260 * scale);
        ImGui.InputTextWithHint("##batchScreenshotFilterSearch", "Search tags or jobs...", ref tagSearchText, 64);
        ImGui.Separator();

        var matchingTags = allTags
            .Where(t => tagSearchText.Length == 0 || t.Contains(tagSearchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var matchingJobs = allJobs
            .Where(j => tagSearchText.Length == 0 || j.Name.Contains(tagSearchText, StringComparison.OrdinalIgnoreCase))
            .Select(j => (j.RowId, j.Name, (JobRole?)j.Role))
            .ToList();

        var emptyMessage = allTags.Count == 0 && allJobs.Count == 0
            ? "No tags or jobs to filter by yet."
            : "No matching tags or jobs.";
        Pills.DrawTagJobFilterList(matchingTags, matchingJobs, Array.Empty<(string, string)>(),
            filterTags, filterJobs, new Dictionary<string, bool>(),
            plugin.GameData.GetJobIcon, "batchScreenshot", 260 * scale, emptyMessage);
    }

    private void DrawProgress(Job activeJob)
    {
        var total = activeJob.Ids.Count;
        var currentName = activeJob.Index < total ? plugin.Configuration.ResolveDesignName(activeJob.Ids[activeJob.Index]) : string.Empty;

        ImGui.TextUnformatted($"Design {Math.Min(activeJob.Index + 1, total)} of {total}");
        ImGui.TextColored(UiTheme.GoldAccent, currentName);
        ImGui.TextDisabled(activeJob.Phase switch
        {
            Phase.Applying => "Applying design...",
            Phase.Waiting => "Waiting before capture...",
            Phase.Capturing => "Capturing...",
            Phase.Cropping => "Cropping...",
            Phase.Saving => "Saving...",
            _ => string.Empty,
        });

        ImGui.Spacing();
        ImGui.ProgressBar(total == 0 ? 0f : (float)activeJob.Index / total, new Vector2(-1, 0));
        ImGui.Spacing();

        if (ImGui.Button("Cancel"))
            CancelJob();
    }

    private static (double X, double Y, double W, double H) ComputeCenteredCrop(double frameWidth, double frameHeight)
    {
        var cropHeight = frameHeight;
        var cropWidth = cropHeight / GalleryDraw.CoverAspectRatio;
        if (cropWidth > frameWidth)
        {
            cropWidth = frameWidth;
            cropHeight = cropWidth * GalleryDraw.CoverAspectRatio;
        }
        var x = (frameWidth - cropWidth) / 2;
        var y = (frameHeight - cropHeight) / 2;
        return (x, y, cropWidth, cropHeight);
    }

    private static void DrawFramingGuideOverlay()
    {
        var display = ImGui.GetIO().DisplaySize;
        var (x, y, w, h) = ComputeCenteredCrop(display.X, display.Y);
        var dl = ImGui.GetForegroundDrawList();
        dl.AddRect(new Vector2((float)x, (float)y), new Vector2((float)(x + w), (float)(y + h)),
            ImGui.ColorConvertFloat4ToU32(UiTheme.GoldAccent), 0f, ImDrawFlags.None, 2f * ImGuiHelpers.GlobalScale);
    }

    private static void DrawSubheader(string label)
    {
        var style = ImGui.GetStyle();
        var draw = ImGui.GetWindowDrawList();

        var avail = ImGui.GetContentRegionAvail().X;
        var lineH = ImGui.GetTextLineHeight();
        var rectH = lineH + style.FramePadding.Y * 2f;

        var rectMin = ImGui.GetCursorScreenPos();
        var rectMax = new Vector2(rectMin.X + avail, rectMin.Y + rectH);

        ImGui.Dummy(new Vector2(avail, rectH));
        draw.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(ImGuiCol.Header), style.FrameRounding);

        Pills.DrawSubheaderChrome(rectMin, rectMax, label, null);
    }

    private void StartJob(List<Guid> ids)
    {
        if (ids.Count == 0)
            return;

        job = new Job { Ids = ids, Index = 0, Phase = Phase.Applying };
        RunApply(++generation);
    }

    private void CancelJob()
    {
        if (job == null)
            return;

        generation++;
        StopWaitTimer();
        plugin.SetMainWindowHiddenForCapture(false);
        if (windowWasOpenBeforeCapture)
            IsOpen = true;
        job = null;
    }

    private void RunApply(int gen)
    {
        if (gen != generation || job == null)
            return;

        try
        {
            // Capturing a preview isn't the user actively choosing to wear this - keep it out of the
            // recently/last-worn history and the per-design last-applied timestamp.
            plugin.DesignApply.ApplyDesignById(job.Ids[job.Index], recordLastApplied: false);
        }
        catch (Exception ex)
        {
            // A failure here must still advance the batch - an unhandled exception would otherwise
            // strand the job on this design forever with no visible error.
            Plugin.Log.Warning(ex, "Batch screenshot: apply failed for {Id}", job.Ids[job.Index]);
            job.Failed++;
            AdvanceToNext(gen);
            return;
        }

        job.Phase = Phase.Waiting;
        StartWaitTimer(gen);
    }

    // A plain Framework.Update subscription rather than a chained RunOnTick(Action, TimeSpan) delay -
    // this is the same polling shape Plugin.cs already relies on for keybinds, so it's proven to fire
    // every frame without needing to trust a one-shot scheduled callback to land correctly.
    private void StartWaitTimer(int gen)
    {
        waitUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, captureDelaySeconds));
        if (!waitingOnFrameworkUpdate)
        {
            waitingOnFrameworkUpdate = true;
            Plugin.Framework.Update += OnWaitTick;
        }

        void OnWaitTick(IFramework framework)
        {
            // A stale handler from a cancelled/superseded generation unsubscribes itself here too -
            // not just on the success path - so cancelling never leaks a permanent no-op subscriber.
            if (gen != generation || job == null)
            {
                Plugin.Framework.Update -= OnWaitTick;
                waitingOnFrameworkUpdate = false;
                return;
            }

            if (DateTime.UtcNow < waitUntilUtc)
                return;

            Plugin.Framework.Update -= OnWaitTick;
            waitingOnFrameworkUpdate = false;
            RunCapture(gen);
        }
    }

    private void StopWaitTimer()
    {
        // The handler is a local function captured per-call, so it can't be unsubscribed by
        // reference from here - the gen/job guard inside it is what actually neutralises it; this
        // just clears the bookkeeping flag so a fresh StartWaitTimer doesn't think one's still live.
        waitingOnFrameworkUpdate = false;
    }

    private void RunCapture(int gen)
    {
        if (gen != generation || job == null)
            return;

        job.Phase = Phase.Capturing;
        plugin.Screenshot.CaptureGameWindowDelayed(
            onBeforeCapture: () =>
            {
                windowWasOpenBeforeCapture = IsOpen;
                hidingForCapture = true;
                IsOpen = false;
                plugin.SetMainWindowHiddenForCapture(true);
            },
            onAfterCapture: () =>
            {
                plugin.SetMainWindowHiddenForCapture(false);
                if (windowWasOpenBeforeCapture)
                    IsOpen = true;
                hidingForCapture = false;
            },
            onTempReady: path => RunCrop(gen, path),
            onError: ex =>
            {
                if (gen != generation || job == null)
                    return;
                Plugin.Log.Warning(ex, "Batch screenshot: capture failed for {Id}", job.Ids[job.Index]);
                job.Failed++;
                AdvanceToNext(gen);
            });
    }

    private void RunCrop(int gen, string tempPath)
    {
        if (gen != generation || job == null)
        {
            plugin.Screenshot.CleanupTemp(tempPath);
            return;
        }

        job.Phase = Phase.Cropping;
        try
        {
            var info = Image.Identify(tempPath);
            var (x, y, w, h) = ComputeCenteredCrop(info.Width, info.Height);
            var croppedPath = plugin.Screenshot.CropTempToOutput(tempPath, (int)x, (int)y, (int)w, (int)h);
            RunSave(gen, tempPath, croppedPath);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Batch screenshot: crop failed for {Id}", job.Ids[job.Index]);
            plugin.Screenshot.CleanupTemp(tempPath);
            job.Failed++;
            AdvanceToNext(gen);
        }
    }

    private void RunSave(int gen, string tempPath, string croppedPath)
    {
        if (gen != generation || job == null)
        {
            plugin.Screenshot.CleanupTemp(tempPath);
            plugin.Screenshot.CleanupTemp(croppedPath);
            return;
        }

        job.Phase = Phase.Saving;
        try
        {
            var id = job.Ids[job.Index];
            if (plugin.ImageStorage.HasCover(id))
                plugin.ImageStorage.DemoteCoverToAdditional(id);
            plugin.ImageStorage.SetCover(id, croppedPath);
            job.Succeeded++;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Batch screenshot: failed to save cover for {Id}", job.Ids[job.Index]);
            job.Failed++;
        }
        finally
        {
            plugin.Screenshot.CleanupTemp(tempPath);
            plugin.Screenshot.CleanupTemp(croppedPath);
        }

        AdvanceToNext(gen);
    }

    private void AdvanceToNext(int gen)
    {
        if (gen != generation || job == null)
            return;

        job.Index++;
        if (job.Index >= job.Ids.Count)
        {
            FinishJob();
            return;
        }

        job.Phase = Phase.Applying;
        RunApply(gen);
    }

    private void FinishJob()
    {
        if (job == null)
            return;

        var succeeded = job.Succeeded;
        var failed = job.Failed;
        job = null;

        var summary = failed > 0
            ? $"Batch screenshot finished: {succeeded} captured, {failed} failed."
            : $"Batch screenshot finished: {succeeded} captured.";
        Plugin.ChatGui.Print($"{Plugin.ChatPrefix}{summary}");
    }
}
