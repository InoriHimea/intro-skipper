// SPDX-License-Identifier: GPL-3.0-only

using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace IntroSkipper.Filters;

/// <summary>
/// Identifies Jellyfin's native playback segment GET — the single response the fork's
/// playback filters rewrite — both from a controller descriptor and from an ASP.NET model.
/// </summary>
internal static class PlaybackSegmentsAction
{
    /// <summary>
    /// Matches the native action by name and declaring type, so no compile-time
    /// Jellyfin.Api reference is needed and a same-named action of any other controller
    /// stays untouched.
    /// </summary>
    /// <param name="controllerType">Controller type.</param>
    /// <param name="actionName">Action name.</param>
    /// <returns>Whether this is the native playback action.</returns>
    internal static bool IsNativePlayback(TypeInfo controllerType, string actionName)
        => controllerType.FullName == "Jellyfin.Api.Controllers.MediaSegmentsController" && actionName == "GetItemSegments";

    /// <summary>
    /// Matches the native playback action together with the shape the playback filters
    /// depend on: a <see cref="Guid"/> <c>itemId</c> parameter and a GET-only selector.
    /// A host change to either shape stops the filters from loading instead of letting
    /// them rewrite a response they cannot address.
    /// </summary>
    /// <param name="action">Action model from the application model.</param>
    /// <returns>Whether the action is the native playback GET.</returns>
    internal static bool Applies(ActionModel action)
        => IsNativePlayback(action.Controller.ControllerType, action.ActionName)
            && action.Parameters.Any(parameter =>
                parameter.ParameterName == "itemId" && parameter.ParameterInfo.ParameterType == typeof(Guid))
            && action.Selectors.Any(selector => selector.ActionConstraints.OfType<HttpMethodActionConstraint>()
                .Any(constraint => constraint.HttpMethods.Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase)));
}
