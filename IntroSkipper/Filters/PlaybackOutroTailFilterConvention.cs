// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace IntroSkipper.Filters;

/// <summary>
/// Applies tail protection only to Jellyfin's native playback segment GET action.
/// </summary>
internal sealed class PlaybackOutroTailFilterConvention : IApplicationModelConvention
{
    /// <inheritdoc />
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                if (IsPlaybackAction(controller.ControllerType, action.ActionName) &&
                    action.Parameters.Any(parameter => parameter.ParameterName == "itemId" && parameter.ParameterInfo.ParameterType == typeof(Guid)) &&
                    action.Selectors.Any(selector => selector.ActionConstraints.OfType<HttpMethodActionConstraint>()
                        .Any(constraint => constraint.HttpMethods.Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase))))
                {
                    action.Filters.Add(new ServiceFilterAttribute(typeof(PlaybackOutroTailFilter)));
                }
            }
        }
    }

    /// <summary>
    /// Identifies the native playback action without a compile-time Jellyfin.Api reference.
    /// </summary>
    /// <param name="controllerType">Controller type.</param>
    /// <param name="actionName">Action name.</param>
    /// <returns>Whether this is the native playback action.</returns>
    internal static bool IsPlaybackAction(TypeInfo controllerType, string actionName)
        => controllerType.FullName == "Jellyfin.Api.Controllers.MediaSegmentsController" && actionName == "GetItemSegments";
}
