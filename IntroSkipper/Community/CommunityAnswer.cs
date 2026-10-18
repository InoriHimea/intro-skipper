// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Community;

/// <summary>
/// One source's answer for an item.
/// </summary>
/// <param name="Kind">Answer kind.</param>
/// <param name="Intro">Intro ranges.</param>
/// <param name="Credits">Credits ranges.</param>
/// <param name="ReferenceDuration">Duration in seconds of the release the ranges were measured on, when the source reports it.</param>
public sealed record CommunityAnswer(
    CommunityAnswerKind Kind,
    IReadOnlyList<CommunityRange> Intro,
    IReadOnlyList<CommunityRange> Credits,
    double? ReferenceDuration)
{
    /// <summary>
    /// Gets the definitive miss.
    /// </summary>
    public static CommunityAnswer NotFound { get; } = new(CommunityAnswerKind.NotFound, [], [], null);

    /// <summary>
    /// Gets the "could not ask" answer.
    /// </summary>
    public static CommunityAnswer Unavailable { get; } = new(CommunityAnswerKind.Unavailable, [], [], null);
}
