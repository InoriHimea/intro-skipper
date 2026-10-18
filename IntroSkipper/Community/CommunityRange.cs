// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Community;

/// <summary>
/// One community range in seconds. An <see cref="End"/> of <see langword="null"/> means the end of the media.
/// </summary>
/// <param name="Start">Start in seconds.</param>
/// <param name="End">End in seconds, or <see langword="null"/> for the end of the media.</param>
public readonly record struct CommunityRange(double Start, double? End);
