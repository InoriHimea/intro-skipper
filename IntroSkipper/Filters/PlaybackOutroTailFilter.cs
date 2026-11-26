// SPDX-License-Identifier: GPL-3.0-only

using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IntroSkipper.Filters;

/// <summary>
/// Keeps the configured tail outside Outro (and optionally tail-reaching Preview) segments
/// returned by the native playback API. Stored segments and canonical editor responses retain
/// their original boundaries.
/// </summary>
/// <param name="libraryManager">Library manager.</param>
public sealed class PlaybackOutroTailFilter(ILibraryManager libraryManager) : IAsyncResultFilter
{
    private readonly ILibraryManager _libraryManager = libraryManager;

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var configuration = Plugin.Instance?.Configuration;
        var keepSeconds = configuration?.EffectiveOutroTailBufferSeconds ?? 0;
        var clipPreview = configuration?.ApplyOutroTailBufferToPreview ?? false;
        if (keepSeconds <= 0 ||
            !HttpMethods.IsGet(context.HttpContext.Request.Method) ||
            context.ActionDescriptor is not ControllerActionDescriptor action ||
            !PlaybackSegmentsAction.IsNativePlayback(action.ControllerTypeInfo, action.ActionName))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var (value, statusCode) = context.Result switch
        {
            ObjectResult result => (result.Value, result.StatusCode),
            JsonResult result => (result.Value, result.StatusCode),
            _ => (null, (int?)null)
        };
        if ((statusCode ?? context.HttpContext.Response.StatusCode) != StatusCodes.Status200OK ||
            value is not QueryResult<MediaSegmentDto> segments ||
            segments.Items is null ||
            !segments.Items.Any(segment => IsProtectedType(segment.Type, clipPreview)) ||
            !context.RouteData.Values.TryGetValue("itemId", out var routeItemId) ||
            !Guid.TryParse(routeItemId?.ToString(), out var itemId) ||
            itemId == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var durationTicks = _libraryManager.GetItemById(itemId)?.RunTimeTicks;
        if (durationTicks is > 0)
        {
            var clipped = ClipSegments(segments, durationTicks.Value, keepSeconds, clipPreview);
            if (context.Result is ObjectResult objectResult)
            {
                objectResult.Value = clipped;
            }
            else if (context.Result is JsonResult jsonResult)
            {
                jsonResult.Value = clipped;
            }
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Clips Outro (and optionally Preview) boundaries without mutating the response, its collection or its DTOs.
    /// </summary>
    /// <param name="segments">Original response.</param>
    /// <param name="durationTicks">Media duration.</param>
    /// <param name="keepSeconds">Tail length to keep.</param>
    /// <param name="clipPreview">Whether Preview segments reaching into the protected tail are also capped.</param>
    /// <returns>The original response when unchanged, otherwise a clipped copy.</returns>
    internal static QueryResult<MediaSegmentDto> ClipSegments(QueryResult<MediaSegmentDto> segments, long durationTicks, int keepSeconds, bool clipPreview = false)
    {
        var items = new List<MediaSegmentDto>();
        var changed = false;
        var removed = 0;
        foreach (var segment in segments.Items)
        {
            var clipped = ClipOutro(segment, durationTicks, keepSeconds, clipPreview);
            changed |= !ReferenceEquals(segment, clipped);
            if (clipped is null)
            {
                removed++;
            }
            else
            {
                items.Add(clipped);
            }
        }

        return changed
            ? new QueryResult<MediaSegmentDto>
            {
                Items = [.. items],
                StartIndex = segments.StartIndex,
                TotalRecordCount = Math.Max(0, segments.TotalRecordCount - removed)
            }
            : segments;
    }

    /// <summary>
    /// Caps one Outro (or Preview when <paramref name="clipPreview"/> is enabled) at the protected
    /// tail, without extending an earlier end.
    /// </summary>
    /// <param name="segment">Original segment.</param>
    /// <param name="durationTicks">Media duration.</param>
    /// <param name="keepSeconds">Tail length to keep.</param>
    /// <param name="clipPreview">Whether Preview segments reaching into the protected tail are also capped.</param>
    /// <returns>The original or clipped segment, or null when no valid skip range remains.</returns>
    internal static MediaSegmentDto? ClipOutro(MediaSegmentDto segment, long durationTicks, int keepSeconds, bool clipPreview = false)
    {
        if (!IsProtectedType(segment.Type, clipPreview) || durationTicks <= 0 || keepSeconds <= 0)
        {
            return segment;
        }

        var cap = Math.Max(0, durationTicks - ((long)keepSeconds * TimeSpan.TicksPerSecond));
        var endTicks = Math.Min(segment.EndTicks, cap);
        if (segment.StartTicks < 0 || endTicks <= segment.StartTicks)
        {
            return null;
        }

        return endTicks == segment.EndTicks
            ? segment
            : new MediaSegmentDto
            {
                Id = segment.Id,
                ItemId = segment.ItemId,
                Type = segment.Type,
                StartTicks = segment.StartTicks,
                EndTicks = endTicks
            };
    }

    private static bool IsProtectedType(MediaSegmentType type, bool clipPreview)
        => type == MediaSegmentType.Outro || (clipPreview && type == MediaSegmentType.Preview);
}
