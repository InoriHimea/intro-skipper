// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Configuration;
using IntroSkipper.Data;

namespace IntroSkipper.Community;

/// <summary>
/// Decides when an episode that community lookups settled "without a segment" is asked again.
/// A new release is often missing from the community databases on its first day, and a settled
/// "no segment" would otherwise stand until a settings change or a manual rescan. For
/// <see cref="PluginConfiguration.CommunityRetryNoSegmentsDays"/> days after the item was added,
/// such an episode reopens at most once per <see cref="MinimumGap"/>.
/// </summary>
/// <remarks>
/// The last attempt per item and mode lives in memory only, which costs at most one extra
/// lookup per item after a restart and needs no stored timestamp: the analysis records carry
/// none. An attempt is recorded when a source was actually asked
/// (<see cref="CommunityFallback"/>) and when an item is reopened here.
/// </remarks>
internal static class CommunityRetry
{
    /// <summary>
    /// The shortest time between two retries of one item and mode.
    /// </summary>
    internal static readonly TimeSpan MinimumGap = TimeSpan.FromHours(12);

    private static readonly ConcurrentDictionary<(Guid ItemId, AnalysisMode Mode), DateTimeOffset> LastAttempt = new();

    /// <summary>
    /// Records that the community databases were asked about the item for the mode.
    /// </summary>
    /// <param name="episodeId">Item id.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="now">Current time.</param>
    internal static void RecordAttempt(Guid episodeId, AnalysisMode mode, DateTimeOffset now)
        => LastAttempt[(episodeId, mode)] = now;

    /// <summary>
    /// Returns whether a settled "no segment" episode should be looked up again, and records the
    /// retry when it should.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="episode">The queued episode.</param>
    /// <param name="mode">Analysis mode.</param>
    /// <param name="now">Current time.</param>
    /// <returns><see langword="true"/> when the episode is due.</returns>
    internal static bool ShouldReopen(PluginConfiguration config, QueuedEpisode episode, AnalysisMode mode, DateTimeOffset now)
    {
        if (!config.EnableCommunityFallback
            || config.CommunityRetryNoSegmentsDays <= 0
            || !CommunityFallback.Supports(mode)
            || episode.DateAdded == default)
        {
            return false;
        }

        var added = new DateTimeOffset(DateTime.SpecifyKind(episode.DateAdded, DateTimeKind.Utc));
        if (now - added > TimeSpan.FromDays(config.CommunityRetryNoSegmentsDays))
        {
            return false;
        }

        var key = (episode.EpisodeId, mode);
        if (LastAttempt.TryGetValue(key, out var last) && now - last < MinimumGap)
        {
            return false;
        }

        LastAttempt[key] = now;
        return true;
    }
}
