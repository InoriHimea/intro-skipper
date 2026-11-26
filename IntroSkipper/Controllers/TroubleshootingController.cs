// SPDX-FileCopyrightText: 2022-2023 ConfusedPolarBear
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-License-Identifier: GPL-3.0-only

using System.Net.Mime;
using System.Runtime.InteropServices;
using IntroSkipper.Community;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.FFmpeg;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using IntroSkipper.ScheduledTasks;
using IntroSkipper.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Controllers;

/// <summary>
/// Troubleshooting controller.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("IntroSkipper")]
public partial class TroubleshootingController : ControllerBase
{
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<TroubleshootingController> _logger;
    private readonly IFFmpegService _ffmpegService;
    private readonly ITaskManager _taskManager;
    private readonly AnalysisScheduler _queue;
    private readonly EdlFileWriter? _edlWriter;
    private readonly SeasonResolver? _seasonResolver;
    private readonly SegmentDtoFactory? _segmentDtoFactory;
    private readonly IReadOnlyList<ICommunitySource> _communitySources;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TroubleshootingController"/> class.
    /// </summary>
    /// <param name="applicationHost">Application host.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="ffmpegService">FFmpeg service.</param>
    /// <param name="taskManager">Scheduled task manager, used to report the detection task's last run and progress.</param>
    /// <param name="queue">Analysis queue, used to report what is running and pending.</param>
    /// <param name="edlWriter">Sidecar EDL writer, used to sync or clean managed files and report tracked counts.</param>
    /// <param name="seasonResolver">Season resolver for full-library EDL sync.</param>
    /// <param name="segmentDtoFactory">Servable segment factory for full-library EDL sync.</param>
    /// <param name="communitySources">Registered community timestamp sources.</param>
    /// <param name="timeProvider">Clock for rate-limit cooldown reporting.</param>
    public TroubleshootingController(
        IApplicationHost applicationHost,
        ILogger<TroubleshootingController> logger,
        IFFmpegService ffmpegService,
        ITaskManager taskManager,
        AnalysisScheduler queue,
        EdlFileWriter? edlWriter = null,
        SeasonResolver? seasonResolver = null,
        SegmentDtoFactory? segmentDtoFactory = null,
        IEnumerable<ICommunitySource>? communitySources = null,
        TimeProvider? timeProvider = null)
    {
        _applicationHost = applicationHost;
        _logger = logger;
        _ffmpegService = ffmpegService;
        _taskManager = taskManager;
        _queue = queue;
        _edlWriter = edlWriter;
        _seasonResolver = seasonResolver;
        _segmentDtoFactory = segmentDtoFactory;
        _communitySources = communitySources is null ? [] : [.. communitySources];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Plugin meta endpoint.
    /// </summary>
    /// <returns>The version info.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public JsonResult GetPluginMetadata()
    {
        var json = new
        {
            version = Plugin.Instance!.Version.ToString(3),
        };

        return new JsonResult(json);
    }

    /// <summary>
    /// Gets the support bundle as Markdown, ready to paste into a bug report.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle")]
    [Produces(MediaTypeNames.Text.Plain)]
    public ActionResult<string> GetSupportBundle() => BuildSupportBundle().Markdown;

    /// <summary>
    /// Gets the support bundle as sections for the dashboard, together with its Markdown rendering.
    /// </summary>
    /// <response code="200">Support bundle created.</response>
    /// <returns>Support bundle.</returns>
    [HttpGet("SupportBundle/Json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SupportBundle> GetSupportBundleJson() => BuildSupportBundle();

    /// <summary>
    /// Resets every plugin setting to its default and saves the configuration. The series, movie and
    /// path exclusion lists are user data rather than tuning and are kept. Jellyfin's per-library
    /// provider selection, the injected skip button CSS, and the season and show overrides in the
    /// plugin database are stored outside the plugin configuration and are left as they are.
    /// </summary>
    /// <response code="204">Configuration reset.</response>
    /// <returns>No content.</returns>
    [HttpPost("Configuration/Reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ResetConfiguration()
    {
        var plugin = Plugin.Instance!;
        var current = plugin.Configuration;
        plugin.UpdateConfiguration(new PluginConfiguration
        {
            SeriesExclusions = [.. current.SeriesExclusions],
            MovieExclusions = [.. current.MovieExclusions],
            PathExclusions = [.. current.PathExclusions],

            // Runtime state the plugin detects at startup, not a setting.
            FileTransformationPluginEnabled = current.FileTransformationPluginEnabled,
        });
        return NoContent();
    }

    /// <summary>
    /// Synchronizes sidecar <c>.edl</c> files across the entire library when EDL writing is enabled,
    /// or removes all files tracked in <c>edl-managed.json</c> when it is disabled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">EDL synchronization or cleanup completed.</response>
    /// <returns>Counts of written, unchanged, removed and failed files.</returns>
    [HttpPost("Edl/Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EdlSyncResponse>> SyncEdlFilesAsync(CancellationToken cancellationToken = default)
    {
        if (_edlWriter is null || _seasonResolver is null || _segmentDtoFactory is null)
        {
            return Ok(new EdlSyncResponse(0, 0, 0, 0));
        }

        return Ok(await _edlWriter.SyncLibraryAsync(_seasonResolver, _segmentDtoFactory, cancellationToken).ConfigureAwait(false));
    }

    private SupportBundle BuildSupportBundle()
    {
        var plugin = Plugin.Instance!;
        var ffmpeg = _ffmpegService.GetCheckResult();
        var settings = ConfigurationReport.Enumerate(plugin.Configuration);
        var detectTask = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is DetectSegmentsTask);
        var queue = _queue.Status;

        List<SupportBundleSection> sections =
        [
            new("Overview")
            {
                Entries =
                [
                    new("Jellyfin version", _applicationHost.ApplicationVersionString),
                    new("Plugin version", GetPluginVersion()),
                    new("Runs on", DescribeOperatingSystem()),
                    new("Runtime", FormattableString.Invariant($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.RuntimeIdentifier}, {Environment.ProcessorCount} CPUs")),
                    new("FFmpeg", ffmpeg.Status),
                    new("FFmpeg path", string.IsNullOrEmpty(plugin.FFmpegPath) ? "unknown" : plugin.FFmpegPath),
                    new("Debug logging", _logger.IsEnabled(LogLevel.Debug) ? "on" : "off"),
                    new("Last scan", detectTask is null ? "unknown" : DescribeLastRun(detectTask)),
                    new("Scan running", DescribeScanState(queue, detectTask)),
                    new("Queue", DescribeQueue(queue)),
                    new("Warnings", WarningManager.GetWarnings()),
                    new(
                        "File Transformation plugin",
                        (plugin.Configuration.FileTransformationPluginEnabled ? "installed" : "not installed")
                        + (plugin.Configuration.UseFileTransformationPlugin ? ", enabled in settings" : ", disabled in settings")),
                    new(
                        "Managed EDL files",
                        DescribeEdlStatus(plugin.Configuration)),
                    new(
                        "Community sources",
                        DescribeCommunitySources(plugin.Configuration)),
                ],
            },
            new("Changed settings")
            {
                Entries = [.. settings.Where(s => !s.IsDefault).Select(s => new SupportBundleEntry(s.Name, $"{s.Value} (default {s.Default})"))],
            },
            new("All settings", Collapsed: true)
            {
                Text = string.Join('\n', settings.Select(s => $"{s.Name}: {s.Value}")),
            },
            new("Fingerprint failures since the last full scan", Collapsed: true)
            {
                Text = DescribeFingerprintFailures(),
            },
            .. ffmpeg.Outputs.Select(o => new SupportBundleSection($"FFmpeg {o.Name}", Collapsed: true) { Text = o.Output }),
        ];

        return new SupportBundle(sections);
    }

    // One failure per line, or null so the section renders as "None".
    private static string? DescribeFingerprintFailures()
    {
        var (failures, dropped) = WarningManager.GetFingerprintFailures();
        if (failures.Count == 0)
        {
            return null;
        }

        var text = string.Join('\n', failures);
        return dropped > 0 ? text + FormattableString.Invariant($"\nand {dropped} more") : text;
    }

    // "2026-08-22 03:00 UTC, Completed in 14 min", with the error message appended for failed runs.
    private static string DescribeLastRun(IScheduledTaskWorker task)
    {
        if (task.LastExecutionResult is not { } result)
        {
            return "never";
        }

        var summary = FormattableString.Invariant($"{result.StartTimeUtc:yyyy-MM-dd HH:mm} UTC, {result.Status}");
        if (result.EndTimeUtc >= result.StartTimeUtc)
        {
            summary += " in " + FormatDuration(result.EndTimeUtc - result.StartTimeUtc);
        }

        // Entries are single-line; exception messages occasionally span several.
        return string.IsNullOrWhiteSpace(result.ErrorMessage) ? summary : summary + ": " + result.ErrorMessage.ReplaceLineEndings(" ").Trim();
    }

    // The queue owns the running definition (shared with the dashboard's ScanStatus
    // endpoint); the scheduled task worker only contributes its progress while a library
    // pass runs.
    private static string DescribeScanState(AnalysisSchedulerStatus queue, IScheduledTaskWorker? task)
    {
        if (!queue.PassRunning)
        {
            return queue.IsRunning ? "pending" : "no";
        }

        return task is { State: TaskState.Running, CurrentProgress: { } progress }
            ? FormattableString.Invariant($"yes ({progress:0}%)")
            : "yes";
    }

    // "2 changed items, 1 manual scan, library pass pending", or "empty".
    private static string DescribeQueue(AnalysisSchedulerStatus queue)
    {
        List<string> parts = [];
        if (queue.ChangedItems > 0)
        {
            parts.Add(FormattableString.Invariant($"{queue.ChangedItems} changed item{(queue.ChangedItems == 1 ? string.Empty : "s")}"));
        }

        if (queue.ManualScans > 0)
        {
            parts.Add(FormattableString.Invariant($"{queue.ManualScans} manual scan{(queue.ManualScans == 1 ? string.Empty : "s")}"));
        }

        if (queue.LibraryPending)
        {
            parts.Add("library pass pending");
        }

        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }

    // Names the host operating system, telling the common Docker images apart on Linux.
    private static string DescribeOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (!OperatingSystem.IsLinux())
        {
            return "Unknown";
        }

        if (!System.IO.File.Exists("/.dockerenv") && !System.IO.File.Exists("/run/.containerenv"))
        {
            return RuntimeInformation.OSDescription;
        }

        if (Environment.GetEnvironmentVariable("ATTACHED_DEVICES_PERMS") != null)
        {
            return "LinuxServer.io image (Docker)";
        }

        return Environment.GetEnvironmentVariable("WEBUI_PORTS") != null ? "hotio image (Docker)" : "Linux (Docker)";
    }

    private static string FormatDuration(TimeSpan duration) => duration switch
    {
        { TotalHours: >= 1 } => FormattableString.Invariant($"{(int)duration.TotalHours} h {duration.Minutes} min"),
        { TotalMinutes: >= 1 } => FormattableString.Invariant($"{(int)duration.TotalMinutes} min"),
        _ => FormattableString.Invariant($"{(int)duration.TotalSeconds} s"),
    };

    // CI rewrites Commit.CommitHash to the full 40-character hash; a local build leaves it empty.
    private static string GetPluginVersion()
    {
        var version = Plugin.Instance!.Version.ToString(4);
        var commit = Commit.CommitHash;
        return commit.Length >= 12 ? string.Concat(version, "+", commit.AsSpan(0, 12)) : version;
    }

    private string DescribeEdlStatus(PluginConfiguration config)
    {
        var count = _edlWriter?.GetManagedCount() ?? 0;
        return (config.WriteEdlOnSegmentChange ? "enabled" : "disabled")
            + FormattableString.Invariant($", {count} tracked");
    }

    private string DescribeCommunitySources(PluginConfiguration config)
    {
        if (!config.EnableCommunityFallback)
        {
            return "disabled";
        }

        if (_communitySources.Count == 0)
        {
            return "enabled (no sources registered)";
        }

        var now = _timeProvider.GetUtcNow();
        var details = string.Join(", ", _communitySources.Select(source => $"{source.Name}: {source.DescribeStatus(config, now)}"));
        return $"enabled ({details})";
    }
}
