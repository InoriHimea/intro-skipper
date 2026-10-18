// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Community;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Creates the per-run analysis objects. <see cref="QueueManager"/> and
/// <see cref="BaseItemAnalyzerTask"/> hold per-run state (queue contents, enumeration
/// failures, memoized ffmpeg validity, captured configuration), so they cannot be DI
/// singletons; this factory owns their shared dependency set instead, so adding a
/// dependency means changing this one class rather than every construction site.
/// </summary>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Library manager.</param>
/// <param name="providerManager">Provider manager.</param>
/// <param name="fileSystem">File system.</param>
/// <param name="ffmpegService">FFmpeg service.</param>
/// <param name="cacheService">Detection cache service.</param>
/// <param name="database">Segment database facade.</param>
/// <param name="communitySources">Community timestamp sources.</param>
public class AnalyzerTaskFactory(
    ILoggerFactory loggerFactory,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IFileSystem fileSystem,
    IFFmpegService ffmpegService,
    DetectionCacheService cacheService,
    IIntroSkipperDatabase database,
    IEnumerable<ICommunitySource>? communitySources = null)
{
    private readonly IReadOnlyList<ICommunitySource> _communitySources = communitySources?.ToList() ?? [];

    /// <summary>
    /// Creates a fresh queue manager for one enumeration run.
    /// </summary>
    /// <returns>The queue manager.</returns>
    internal QueueManager CreateQueueManager()
        => new(
            loggerFactory.CreateLogger<QueueManager>(),
            libraryManager,
            providerManager,
            fileSystem,
            ffmpegService,
            database);

    /// <summary>
    /// Creates a fresh analyzer task for one analysis run.
    /// </summary>
    /// <returns>The analyzer task.</returns>
    internal BaseItemAnalyzerTask CreateAnalyzerTask()
        => new(
            loggerFactory.CreateLogger<BaseItemAnalyzerTask>(),
            loggerFactory,
            this,
            ffmpegService,
            cacheService,
            database)
        {
            // One fallback per run: its answer cache spans the run's intro and credits passes.
            CommunityFallback = _communitySources.Count == 0
                ? null
                : new CommunityFallback(loggerFactory.CreateLogger<CommunityFallback>(), _communitySources, ffmpegService, database),
        };
}
