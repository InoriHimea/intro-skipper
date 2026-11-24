// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;

namespace IntroSkipper.Filters;

/// <summary>
/// The playback offsets and start-snap rule <see cref="PlaybackIntroOffsetFilter"/> applies
/// to a foreign Intro or Outro row, flattened from the plugin configuration so the response path
/// reads it once per request.
/// </summary>
/// <param name="StartSeconds">Seconds of intro to keep at the start.</param>
/// <param name="EndSeconds">Seconds of intro to keep at the end.</param>
/// <param name="EndSnapThresholdSeconds">Distance from the episode start that counts as snapped.</param>
/// <param name="IncludeStartOffsetWhenSnapping">Whether a snapped start still takes the start offset.</param>
/// <param name="CreditsStartSeconds">Seconds of credits to keep at the start of a foreign Outro row.</param>
/// <param name="CreditsEndSeconds">Seconds of credits to keep at the end of a foreign Outro row.</param>
/// <param name="IncludeCreditsEndOffsetWhenSnapping">Whether an Outro end snapped to the episode end still takes the end offset.</param>
/// <param name="ApplyToForeignOutro">Whether foreign Outro rows take the credits offsets at all.</param>
internal readonly record struct PlaybackIntroOffsets(
    int StartSeconds,
    int EndSeconds,
    double EndSnapThresholdSeconds,
    bool IncludeStartOffsetWhenSnapping,
    double CreditsStartSeconds = 0,
    double CreditsEndSeconds = 0,
    bool IncludeCreditsEndOffsetWhenSnapping = false,
    bool ApplyToForeignOutro = false)
{
    /// <summary>
    /// Gets a value indicating whether this configuration changes nothing, so the response
    /// path can pass through before reading attribution.
    /// </summary>
    public bool IsNoOp => !HasIntroOffsets && !HasOutroOffsets;

    /// <summary>
    /// Gets a value indicating whether a foreign Intro row would change.
    /// </summary>
    public bool HasIntroOffsets => StartSeconds != 0 || EndSeconds != 0;

    /// <summary>
    /// Gets a value indicating whether a foreign Outro row would change.
    /// </summary>
    public bool HasOutroOffsets => ApplyToForeignOutro && (CreditsStartSeconds != 0 || CreditsEndSeconds != 0);

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
            configuration.IncludeIntroStartOffsetWhenSnapping,
            Math.Max(0, configuration.CreditsStartOffset),
            Math.Max(0, configuration.CreditsEndOffset),
            configuration.IncludeCreditsEndOffsetWhenSnapping,
            configuration.ApplyOffsetsToForeignOutro);
}
