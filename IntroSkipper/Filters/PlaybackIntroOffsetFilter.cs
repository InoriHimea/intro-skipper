// SPDX-License-Identifier: GPL-3.0-only

using IntroSkipper.Configuration;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Filters;

/// <summary>
/// Applies the configured playback offsets to <c>Intro</c> rows another provider wrote.
/// Intro Skipper's own rows already carry <see cref="PluginConfiguration.IntroStartOffset"/>
/// and <see cref="PluginConfiguration.IntroEndOffset"/>, because
/// <see cref="Analyzers.TimeAdjustmentHelper"/> applies them at analysis time. A foreign
/// row carries raw detection times, so a provider aiming slightly past the real intro end
/// sends the skip into the episode. Rewriting only the identified foreign rows gives them
/// the same treatment without ever double-applying an offset to a row that has one.
/// Stored segments and every other response stay untouched.
/// </summary>
/// <param name="libraryManager">Library manager; supplies the media duration.</param>
/// <param name="segmentStore">Reads provider attribution of the item's Jellyfin rows.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class PlaybackIntroOffsetFilter(
    ILibraryManager libraryManager,
    IJellyfinSegmentStore segmentStore,
    ILogger<PlaybackIntroOffsetFilter> logger) : IAsyncResultFilter
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IJellyfinSegmentStore _segmentStore = segmentStore;
    private readonly ILogger<PlaybackIntroOffsetFilter> _logger = logger;

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var configuration = Plugin.Instance?.Configuration;
        var offsets = configuration is null ? default : PlaybackIntroOffsets.From(configuration);
        if (offsets.IsNoOp ||
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
            !segments.Items.Any(segment => segment.Type == MediaSegmentType.Intro) ||
            !context.RouteData.Values.TryGetValue("itemId", out var routeItemId) ||
            !Guid.TryParse(routeItemId?.ToString(), out var itemId) ||
            itemId == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        IReadOnlySet<Guid> foreignIds;
        try
        {
            foreignIds = await _segmentStore.GetForeignSegmentIdsAsync(itemId, MediaSegmentType.Intro, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A playback response is a skip hint, never worth failing the request over; an
            // unreadable attribution leaves every row's boundaries as the server sent them.
            LogAttributionReadFailed(_logger, itemId, ex);
            await next().ConfigureAwait(false);
            return;
        }

        if (foreignIds.Count == 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var adjusted = AdjustSegments(segments, foreignIds, _libraryManager.GetItemById(itemId)?.RunTimeTicks, offsets);
        if (!ReferenceEquals(adjusted, segments))
        {
            if (context.Result is ObjectResult objectResult)
            {
                objectResult.Value = adjusted;
            }
            else if (context.Result is JsonResult jsonResult)
            {
                jsonResult.Value = adjusted;
            }
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the offsets to the rows in <paramref name="foreignIds"/> without mutating the
    /// response, its collection or its DTOs.
    /// </summary>
    /// <param name="segments">Original response.</param>
    /// <param name="foreignIds">Ids of Intro rows another provider wrote.</param>
    /// <param name="durationTicks">Media duration; non-positive when unknown.</param>
    /// <param name="offsets">Configured playback offsets.</param>
    /// <returns>The original response when nothing changed, otherwise an adjusted copy.</returns>
    internal static QueryResult<MediaSegmentDto> AdjustSegments(
        QueryResult<MediaSegmentDto> segments,
        IReadOnlySet<Guid> foreignIds,
        long? durationTicks,
        PlaybackIntroOffsets offsets)
    {
        var items = new List<MediaSegmentDto>();
        var changed = false;
        var removed = 0;
        foreach (var segment in segments.Items)
        {
            var adjusted = segment.Type == MediaSegmentType.Intro && foreignIds.Contains(segment.Id)
                ? AdjustIntro(segment, durationTicks, offsets)
                : segment;
            changed |= !ReferenceEquals(segment, adjusted);
            if (adjusted is null)
            {
                removed++;
            }
            else
            {
                items.Add(adjusted);
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
    /// Applies one foreign Intro row's offsets the way analysis applies them to a detected
    /// range: the start takes the start offset unless it sat on the episode start (then
    /// only when explicitly opted in), the end loses the end offset. Both are clamped to
    /// the media duration when it is known. Unlike analysis, a snapped start keeps the
    /// provider's own value instead of moving to zero, so a foreign row's skip never
    /// grows backwards.
    /// </summary>
    /// <param name="segment">Original foreign Intro row.</param>
    /// <param name="durationTicks">Media duration; non-positive when unknown.</param>
    /// <param name="offsets">Configured playback offsets.</param>
    /// <returns>The original row when unchanged, an adjusted copy, or null when no skip range remains.</returns>
    internal static MediaSegmentDto? AdjustIntro(MediaSegmentDto segment, long? durationTicks, PlaybackIntroOffsets offsets)
    {
        var maximum = durationTicks is > 0 ? durationTicks.Value : long.MaxValue;
        var startOffsetTicks = (long)offsets.StartSeconds * TimeSpan.TicksPerSecond;

        // A row the detector already placed at the episode start keeps it: analysis
        // treats such a start as snapped and leaves it at zero unless the admin opted
        // in, and pulling a foreign start backwards would widen that provider's skip.
        var snappedToStart = segment.StartTicks <= (long)(offsets.EndSnapThresholdSeconds * TimeSpan.TicksPerSecond);
        var startTicks = snappedToStart && !offsets.IncludeStartOffsetWhenSnapping
            ? segment.StartTicks
            : AddClamped(segment.StartTicks, startOffsetTicks);
        startTicks = Math.Clamp(startTicks, 0, maximum);
        var endTicks = Math.Clamp(segment.EndTicks - ((long)offsets.EndSeconds * TimeSpan.TicksPerSecond), 0, maximum);

        if (endTicks <= startTicks)
        {
            return null;
        }

        return startTicks == segment.StartTicks && endTicks == segment.EndTicks
            ? segment
            : new MediaSegmentDto
            {
                Id = segment.Id,
                ItemId = segment.ItemId,
                Type = segment.Type,
                StartTicks = startTicks,
                EndTicks = endTicks
            };
    }

    private static long AddClamped(long ticks, long delta) => ticks > long.MaxValue - delta ? long.MaxValue : ticks + delta;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read media segment providers for item {ItemId}; leaving playback intro boundaries unchanged.")]
    private static partial void LogAttributionReadFailed(ILogger logger, Guid itemId, Exception exception);
}
