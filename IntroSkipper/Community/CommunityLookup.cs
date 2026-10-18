// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Community;

/// <summary>
/// Identity of one episode or movie as the community databases address it. Numbering is the
/// local library's; <see cref="DisplayOrder"/> carries the series order so a source that can
/// convert coordinates is told which order they are in.
/// </summary>
/// <param name="IsMovie">Whether the item is a movie.</param>
/// <param name="ImdbId">IMDb id of the movie, or of the series for an episode.</param>
/// <param name="TmdbId">TMDB id of the movie, or of the series for an episode.</param>
/// <param name="TvdbId">TVDB id of the movie, or of the series for an episode.</param>
/// <param name="Season">Season number; <see langword="null"/> for movies.</param>
/// <param name="Episode">Episode number; <see langword="null"/> for movies.</param>
/// <param name="DisplayOrder">The series display order; empty for the default (aired) order.</param>
/// <param name="Duration">Local file duration in seconds.</param>
public sealed record CommunityLookup(
    bool IsMovie,
    string? ImdbId,
    string? TmdbId,
    string? TvdbId,
    int? Season,
    int? Episode,
    string DisplayOrder,
    double Duration);
