// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Community;

/// <summary>
/// How a community source answered a lookup.
/// </summary>
public enum CommunityAnswerKind
{
    /// <summary>
    /// The source knows the item; its ranges may still be empty for a mode.
    /// </summary>
    Found,

    /// <summary>
    /// The source definitively has nothing for the item.
    /// </summary>
    NotFound,

    /// <summary>
    /// The source could not be asked (network failure, server error or rate limit); the
    /// item must stay eligible for a later scan.
    /// </summary>
    Unavailable,
}
