// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace IntroSkipper.Filters;

/// <summary>
/// Applies the fork's playback filters — <see cref="PlaybackOutroTailFilter"/> and
/// <see cref="PlaybackIntroOffsetFilter"/> — to Jellyfin's native playback segment GET.
/// Both rewrite that one response, so they share one convention and one definition of
/// the action they may touch.
/// </summary>
internal sealed class PlaybackFiltersConvention : IApplicationModelConvention
{
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

                action.Filters.Add(new ServiceFilterAttribute(typeof(PlaybackIntroOffsetFilter)));
                action.Filters.Add(new ServiceFilterAttribute(typeof(PlaybackOutroTailFilter)));
            }
        }
    }
}
