// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;

namespace IntroSkipper.Community;

/// <summary>
/// A community timestamp database.
/// </summary>
public interface ICommunitySource
{
    /// <summary>
    /// Gets the source name used in logs.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns whether the configuration enables this source.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns><see langword="true"/> when enabled.</returns>
    bool IsEnabled(PluginConfiguration config);

    /// <summary>
    /// Looks the item up.
    /// </summary>
    /// <param name="lookup">Item identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; never throws for network or server failures.</returns>
    Task<CommunityAnswer> LookupAsync(CommunityLookup lookup, CancellationToken cancellationToken);

    /// <summary>
    /// Describes the current availability of this source for the support bundle.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="now">Current UTC time.</param>
    /// <returns>A short human-readable status such as <c>ready</c>, <c>disabled</c>, or the remaining rate-limit cooldown.</returns>
    string DescribeStatus(PluginConfiguration config, DateTimeOffset now)
        => IsEnabled(config) ? "ready" : "disabled";
}
