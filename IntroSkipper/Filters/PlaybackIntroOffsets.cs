// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;

namespace IntroSkipper.Filters;

/// <summary>
/// The playback offsets and start-snap rule <see cref="PlaybackIntroOffsetFilter"/> applies
/// to a foreign Intro row, flattened from the plugin configuration so the response path
/// reads it once per request.
/// </summary>
/// <param name="StartSeconds">Seconds of intro to keep at the start.</param>
/// <param name="EndSeconds">Seconds of intro to keep at the end.</param>
/// <param name="EndSnapThresholdSeconds">Distance from the episode start that counts as snapped.</param>
/// <param name="IncludeStartOffsetWhenSnapping">Whether a snapped start still takes the start offset.</param>
internal readonly record struct PlaybackIntroOffsets(
    int StartSeconds,
    int EndSeconds,
    double EndSnapThresholdSeconds,
    bool IncludeStartOffsetWhenSnapping)
{
    /// <summary>
    /// Gets a value indicating whether this configuration changes nothing, so the response
    /// path can pass through before reading attribution.
    /// </summary>
    public bool IsNoOp => StartSeconds == 0 && EndSeconds == 0;

    /// <summary>
    /// Reads the offsets from the current configuration.
    /// </summary>
    /// <param name="configuration">Plugin configuration.</param>
    /// <returns>The offsets; a negative offset counts as zero, as an offset below zero only widens the skip.</returns>
    public static PlaybackIntroOffsets From(PluginConfiguration configuration)
        => new(
            Math.Max(0, configuration.IntroStartOffset),
            Math.Max(0, configuration.IntroEndOffset),
            Math.Max(0, configuration.EndSnapThreshold),
            configuration.IncludeIntroStartOffsetWhenSnapping);
}
