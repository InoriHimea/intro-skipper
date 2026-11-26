// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace IntroSkipper.Filters;

/// <summary>
/// Applies the fork's playback filters — <see cref="PlaybackCommunityFallbackFilter"/>,
/// <see cref="PlaybackIntroOffsetFilter"/> and <see cref="PlaybackOutroTailFilter"/> — to
/// Jellyfin's native playback segment GET. <see cref="PlaybackCommunityFallbackFilter"/> runs
/// ahead of <see cref="MediaSegmentsFirstEpisodeFilter"/> so an unanalyzed season premiere still
/// respects the configured first-episode intro suppression.
/// </summary>
internal sealed class PlaybackFiltersConvention : IApplicationModelConvention
{
    /// <summary>
    /// Filter order placing on-demand community fallback ahead of default (<c>0</c>) action filters.
    /// </summary>
    internal const int FallbackFilterOrder = -10;

    /// <inheritdoc />
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                if (!PlaybackSegmentsAction.Applies(action))
                {
                    continue;
                }

                action.Filters.Insert(0, new ServiceFilterAttribute(typeof(PlaybackCommunityFallbackFilter)) { Order = FallbackFilterOrder });
                action.Filters.Add(new ServiceFilterAttribute(typeof(PlaybackIntroOffsetFilter)));
                action.Filters.Add(new ServiceFilterAttribute(typeof(PlaybackOutroTailFilter)));
            }
        }
    }
}
