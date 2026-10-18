// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;

namespace IntroSkipper.Community;

/// <summary>
/// Keeps local results when the community fallback is switched on or off. The switch is part of
/// the intro and credits analysis hash, so without this every analyzed item of those modes would
/// be re-analyzed. Instead, a completed result recorded under the opposite switch state is adopted
/// under the current hash when it holds a segment the switch cannot change: any segment when the
/// fallback is turned on, and a segment that did not come from a community source when it is
/// turned off. Results without a segment stay mismatched, so exactly those items are re-analyzed
/// and, when the fallback is on, looked up.
/// </summary>
internal static class CommunityHashAdoption
{
    /// <summary>
    /// Adopts the season's eligible completed results under the current hash.
    /// </summary>
    /// <param name="database">Segment database facade.</param>
    /// <param name="snapshot">The season's stored analysis state.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when any record was updated.</returns>
    internal static async Task<bool> UpgradeAsync(
        IIntroSkipperDatabase database,
        SeasonQueueSnapshot snapshot,
        PluginConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var updated = false;
        foreach (var mode in new[] { AnalysisMode.Introduction, AnalysisMode.Credits })
        {
            var action = snapshot.AnalyzerActionByMode.GetValueOrDefault(mode, AnalyzerAction.Default);
            foreach (var ffmpegValid in new[] { false, true })
            {
                var currentHash = ConfigHasher.Analysis(config, mode, action, ffmpegValid);
                var previousHash = ConfigHasher.Analysis(config, mode, action, ffmpegValid, !config.EnableCommunityFallback);
                var candidates = snapshot.AnalyzedConfigHashes
                    .Where(pair => pair.Key.Mode == mode
                        && string.Equals(pair.Value, previousHash, StringComparison.Ordinal)
                        && snapshot.SegmentModesByEpisodeId.TryGetValue(pair.Key.ItemId, out var modes)
                        && modes.Contains(mode))
                    .Select(pair => pair.Key.ItemId)
                    .ToList();
                if (candidates.Count == 0)
                {
                    continue;
                }

                if (!config.EnableCommunityFallback)
                {
                    // Turned off: a community segment must not outlive the switch.
                    var kept = new List<Guid>(candidates.Count);
                    foreach (var itemId in candidates)
                    {
                        var segments = await database.GetSegmentsAsync(itemId, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (!segments.Any(s => s.Type == mode && s.Source == SegmentSource.Community))
                        {
                            kept.Add(itemId);
                        }
                    }

                    candidates = kept;
                }

                updated |= await database.UpgradeAnalysisHashAsync(mode, candidates, previousHash, currentHash, cancellationToken).ConfigureAwait(false) > 0;
            }
        }

        return updated;
    }
}
