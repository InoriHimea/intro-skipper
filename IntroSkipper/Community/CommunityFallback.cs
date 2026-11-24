// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Analyzers;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.FFmpeg;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Community;

/// <summary>
/// Fills intro and credits segments the local analyzers did not find from the community
/// databases. Runs after the mode's local analysis and only for episodes that analysis settled
/// without a segment, so a local result is never replaced. Community ranges go through the same
/// <see cref="TimeAdjustmentHelper"/> as local results, so the configured offsets apply.
/// One instance serves one season pass: answers are reused between its intro and credits modes,
/// so each item costs at most one request per source per pass.
/// </summary>
/// <param name="logger">Logger.</param>
/// <param name="sources">Community sources in priority order.</param>
/// <param name="ffmpegService">FFmpeg service for boundary adjustment.</param>
/// <param name="database">Segment database facade.</param>
internal sealed partial class CommunityFallback(
    ILogger<CommunityFallback> logger,
    IReadOnlyList<ICommunitySource> sources,
    IFFmpegService ffmpegService,
    IntroSkipperDatabase database)
{
    private readonly ConcurrentDictionary<(Guid ItemId, string Source), CommunityAnswer> _answers = new();

    /// <summary>
    /// Returns whether the mode can be filled from the community databases.
    /// </summary>
    /// <param name="mode">Analysis mode.</param>
    /// <returns><see langword="true"/> for intro and credits.</returns>
    public static bool Supports(AnalysisMode mode) => mode is AnalysisMode.Introduction or AnalysisMode.Credits;

    /// <summary>
    /// Returns whether a series display order is the aired order the databases store.
    /// </summary>
    /// <param name="displayOrder">Jellyfin series display order.</param>
    /// <returns><see langword="true"/> for the default order.</returns>
    public static bool IsDefaultOrder(string? displayOrder)
        => string.IsNullOrWhiteSpace(displayOrder) || string.Equals(displayOrder, "aired", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a <see cref="CommunityLookup"/> from a library item (<see cref="Episode"/> or movie).
    /// </summary>
    /// <param name="item">Episode or movie item.</param>
    /// <param name="getItem">Lookup function resolving parent items by id.</param>
    /// <param name="duration">Optional duration override in seconds; non-positive uses <see cref="BaseItem.RunTimeTicks"/>.</param>
    /// <returns>The community lookup, or <see langword="null"/> when an episode's series cannot be resolved.</returns>
    internal static CommunityLookup? CreateLookup(BaseItem item, Func<Guid, BaseItem?> getItem, double duration = 0)
    {
        var effectiveDuration = duration > 0 ? duration : TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
        if (item is Episode episode)
        {
            return getItem(episode.SeriesId) is Series series
                ? CreateEpisodeLookup(series, episode.ParentIndexNumber, episode.IndexNumber, effectiveDuration)
                : null;
        }

        return new CommunityLookup(
            true,
            Id(item, MetadataProvider.Imdb),
            Id(item, MetadataProvider.Tmdb),
            Id(item, MetadataProvider.Tvdb),
            null,
            null,
            string.Empty,
            effectiveDuration,
            MalId(item));
    }

    /// <summary>
    /// Fills the mode for every item the local analysis left without a segment. An item no
    /// source could be asked about is marked <see cref="EpisodeState.AnalysisFailed"/> so the
    /// next scan retries it instead of recording a settled "no segment".
    /// </summary>
    /// <param name="items">The season's items, already through local analysis.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when every item was considered.</returns>
    public async Task FillAsync(IReadOnlyList<QueuedEpisode> items, AnalysisMode mode, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!config.EnableCommunityFallback || !Supports(mode))
        {
            return;
        }

        var enabled = sources.Where(s => s.IsEnabled(config)).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        var helper = new TimeAdjustmentHelper(logger, config, mode, ffmpegService);
        foreach (var episode in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Analyzed and UserProvided carry a segment; AnalysisFailed is left to the local
            // analyzers' retry so local analysis keeps priority.
            if (episode.GetAnalyzed(mode) is not (EpisodeState.NotAnalyzed or EpisodeState.NoSegments) || episode.Duration <= 0)
            {
                continue;
            }

            var lookup = CreateLookup(episode);
            if (lookup is null)
            {
                continue;
            }

            CommunityRetry.RecordAttempt(episode.EpisodeId, mode, DateTimeOffset.UtcNow);
            var (ranges, sourceName, unavailable) = await FindRangesAsync(episode, lookup, mode, enabled, config, cancellationToken).ConfigureAwait(false);
            if (ranges.Count == 0)
            {
                if (unavailable)
                {
                    episode.SetAnalyzed(mode, EpisodeState.AnalysisFailed);
                }

                continue;
            }

            var adjusted = new List<Segment>(ranges.Count);
            foreach (var range in ranges)
            {
                var segment = await helper.AdjustIntroTimesAsync(episode, range, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (segment.Valid)
                {
                    adjusted.Add(segment);
                }
            }

            if (adjusted.Count == 0)
            {
                continue;
            }

            var written = await database.ReplaceAutoSegmentsAsync(episode.EpisodeId, mode, adjusted, SegmentSource.Community, episode.AnalysisConfigHash, cancellationToken).ConfigureAwait(false);
            if (written > 0)
            {
                episode.SetAnalyzed(mode, EpisodeState.Analyzed);
                LogFilled(logger, mode, episode.Name, sourceName, adjusted[0].Start, adjusted[0].End);
            }
        }
    }

    /// <summary>
    /// Converts one source answer into ranges for the mode, rejecting timestamps measured on a
    /// different release: a reference duration that disagrees with the local file, or a range
    /// reaching past its end by more than the tolerance.
    /// </summary>
    /// <param name="answer">Source answer.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="episodeId">Item id.</param>
    /// <param name="duration">Local duration in seconds.</param>
    /// <param name="tolerance">Allowed disagreement in seconds.</param>
    /// <returns>The usable ranges; empty when the answer has none or does not fit the file.</returns>
    internal static IReadOnlyList<Segment> ToSegments(CommunityAnswer answer, AnalysisMode mode, Guid episodeId, double duration, double tolerance)
    {
        if (answer.Kind != CommunityAnswerKind.Found
            || (answer.ReferenceDuration is { } reference && Math.Abs(reference - duration) > tolerance))
        {
            return [];
        }

        var ranges = mode == AnalysisMode.Introduction ? answer.Intro : answer.Credits;
        var segments = new List<Segment>(ranges.Count);
        foreach (var range in ranges)
        {
            var end = range.End ?? duration;
            if (range.Start < 0 || range.Start >= duration || end > duration + tolerance)
            {
                return [];
            }

            var segment = new Segment(episodeId, new TimeRange(range.Start, Math.Min(end, duration)));
            if (segment.Valid)
            {
                segments.Add(segment);
            }
        }

        return segments;
    }

    private async Task<(IReadOnlyList<Segment> Ranges, string Source, bool Unavailable)> FindRangesAsync(
        QueuedEpisode episode,
        CommunityLookup lookup,
        AnalysisMode mode,
        IReadOnlyList<ICommunitySource> enabled,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var unavailable = false;
        foreach (var source in enabled)
        {
            var key = (episode.EpisodeId, source.Name);
            if (!_answers.TryGetValue(key, out var answer))
            {
                answer = await source.LookupAsync(lookup, cancellationToken).ConfigureAwait(false);
                if (answer.Kind == CommunityAnswerKind.Unavailable)
                {
                    unavailable = true;
                    continue;
                }

                _answers[key] = answer;
            }

            var ranges = ToSegments(answer, mode, episode.EpisodeId, episode.Duration, config.CommunityDurationToleranceSeconds);
            if (ranges.Count > 0)
            {
                return (ranges, source.Name, false);
            }

            if (answer.Kind == CommunityAnswerKind.Found)
            {
                LogRejected(logger, source.Name, mode, episode.Name);
            }
        }

        return ([], string.Empty, unavailable);
    }

    private static CommunityLookup? CreateLookup(QueuedEpisode episode)
    {
        var plugin = Plugin.Instance;
        if (episode.Category == QueuedMediaCategory.Movie)
        {
            return plugin?.GetItem(episode.EpisodeId) is { } movie
                ? CreateLookup(movie, id => plugin.GetItem(id), episode.Duration)
                : null;
        }

        // The series carries the ids the databases address episodes by.
        return plugin?.GetItem(episode.SeriesId) is Series series
            ? CreateEpisodeLookup(series, episode.SeasonNumber, episode.EpisodeNumber, episode.Duration)
            : null;
    }

    private static CommunityLookup CreateEpisodeLookup(Series series, int? season, int? episode, double duration)
        => new(
            false,
            Id(series, MetadataProvider.Imdb),
            Id(series, MetadataProvider.Tmdb),
            Id(series, MetadataProvider.Tvdb),
            season,
            episode,
            series.DisplayOrder ?? string.Empty,
            duration,
            MalId(series));

    // Metadata plugins for anime store the MyAnimeList id under one of these names.
    private static string? MalId(BaseItem item)
        => item.ProviderIds.TryGetValue("MyAnimeList", out var id) && !string.IsNullOrWhiteSpace(id) ? id
            : item.ProviderIds.TryGetValue("Mal", out id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    private static string? Id(BaseItem item, MetadataProvider provider)
        => item.TryGetProviderId(provider, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "[Mode: {Mode}] {Name}: no local segment, using {Source} community timestamps {Start:F1}-{End:F1}")]
    private static partial void LogFilled(ILogger logger, AnalysisMode mode, string name, string source, double start, double end);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source} timestamps for {Mode} of {Name} do not fit the local file (different release?); ignored")]
    private static partial void LogRejected(ILogger logger, string source, AnalysisMode mode, string name);
}
