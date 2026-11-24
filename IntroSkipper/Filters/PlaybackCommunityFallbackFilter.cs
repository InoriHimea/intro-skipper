// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using IntroSkipper.Community;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Filters;

/// <summary>
/// Queries the enabled community timestamp databases on demand during a native playback
/// segment GET when the response has neither an Intro nor an Outro and the item has not
/// been analyzed yet. Responses are bounded by a short timeout so playback startup is
/// never blocked, and any matches receive the configured playback offsets and outro tail
/// buffer without touching the plugin database.
/// </summary>
internal sealed partial class PlaybackCommunityFallbackFilter : IAsyncResultFilter
{
    /// <summary>
    /// Maximum wait before aborting an on-demand playback community lookup.
    /// </summary>
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackCommunityFallbackFilter> _logger;
    private readonly IServiceProvider? _serviceProvider;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<(Guid ItemId, string Source), CommunityAnswer> _answers = new();
    private IntroSkipperDatabase? _database;
    private IReadOnlyList<ICommunitySource>? _sources;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCommunityFallbackFilter"/> class
    /// with explicit dependencies, primarily for tests.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="database">Segment database facade.</param>
    /// <param name="sources">Community sources in priority order.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeout">Optional lookup timeout; defaults to <see cref="LookupTimeout"/>.</param>
    public PlaybackCommunityFallbackFilter(
        ILibraryManager libraryManager,
        IntroSkipperDatabase database,
        IEnumerable<ICommunitySource> sources,
        ILogger<PlaybackCommunityFallbackFilter> logger,
        TimeSpan? timeout = null)
    {
        _libraryManager = libraryManager;
        _database = database;
        _sources = [.. sources];
        _logger = logger;
        _timeout = timeout ?? LookupTimeout;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackCommunityFallbackFilter"/> class
    /// resolving the database facade and community sources lazily when needed.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="serviceProvider">Service provider.</param>
    /// <param name="logger">Logger.</param>
    public PlaybackCommunityFallbackFilter(
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider,
        ILogger<PlaybackCommunityFallbackFilter> logger)
    {
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _timeout = LookupTimeout;
    }

    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (Plugin.Instance?.Configuration is not { EnableCommunityFallback: true, EnableOnDemandPlaybackCommunityLookup: true } config ||
            (!config.ScanIntroduction && !config.ScanCredits) ||
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
            segments.Items.Any(segment => segment.Type is MediaSegmentType.Intro or MediaSegmentType.Outro) ||
            !context.RouteData.Values.TryGetValue("itemId", out var routeItemId) ||
            !Guid.TryParse(routeItemId?.ToString(), out var itemId) ||
            itemId == Guid.Empty)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item?.RunTimeTicks is not > 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var durationTicks = item.RunTimeTicks.Value;
        var lookup = CommunityFallback.CreateLookup(item, _libraryManager.GetItemById);
        if (lookup is null || lookup.Duration <= 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var enabled = GetSources().Where(s => s.IsEnabled(config)).ToList();
        if (enabled.Count == 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var requestAborted = context.HttpContext.RequestAborted;
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, timeoutCts.Token);
        List<MediaSegmentDto> added;
        try
        {
            if (!await GetDatabase().IsEligibleForOnDemandCommunityLookupAsync(itemId, linked.Token).ConfigureAwait(false))
            {
                await next().ConfigureAwait(false);
                return;
            }

            added = await LookupSegmentsAsync(itemId, durationTicks, lookup, enabled, config, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
        {
            LogLookupTimedOut(_logger, itemId);
            await next().ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogLookupFailed(_logger, itemId, ex);
            await next().ConfigureAwait(false);
            return;
        }

        if (added.Count > 0)
        {
            var updated = new QueryResult<MediaSegmentDto>
            {
                Items = [.. segments.Items, .. added],
                StartIndex = segments.StartIndex,
                TotalRecordCount = Math.Max(0, segments.TotalRecordCount + added.Count)
            };

            if (context.Result is ObjectResult objectResult)
            {
                objectResult.Value = updated;
            }
            else if (context.Result is JsonResult jsonResult)
            {
                jsonResult.Value = updated;
            }
        }

        await next().ConfigureAwait(false);
    }

    private static MediaSegmentDto? AdjustSegment(
        MediaSegmentDto segment,
        long durationTicks,
        PlaybackIntroOffsets offsets,
        int outroTailBufferSeconds)
    {
        if (segment.Type == MediaSegmentType.Intro)
        {
            return PlaybackIntroOffsetFilter.AdjustIntro(segment, durationTicks, offsets);
        }

        var adjusted = PlaybackIntroOffsetFilter.AdjustOutro(segment, durationTicks, offsets);
        return adjusted is null ? null : PlaybackOutroTailFilter.ClipOutro(adjusted, durationTicks, outroTailBufferSeconds);
    }

    private async Task<List<MediaSegmentDto>> LookupSegmentsAsync(
        Guid itemId,
        long durationTicks,
        CommunityLookup lookup,
        IReadOnlyList<ICommunitySource> enabled,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var added = new List<MediaSegmentDto>();
        var offsets = PlaybackIntroOffsets.From(config) with { ApplyToForeignOutro = true };
        var tailBuffer = config.EffectiveOutroTailBufferSeconds;

        if (config.ScanIntroduction)
        {
            await AddModeSegmentsAsync(AnalysisMode.Introduction, MediaSegmentType.Intro).ConfigureAwait(false);
        }

        if (config.ScanCredits)
        {
            await AddModeSegmentsAsync(AnalysisMode.Credits, MediaSegmentType.Outro).ConfigureAwait(false);
        }

        return added;

        async Task AddModeSegmentsAsync(AnalysisMode mode, MediaSegmentType type)
        {
            foreach (var source in enabled)
            {
                var key = (itemId, source.Name);
                if (!_answers.TryGetValue(key, out var answer))
                {
                    answer = await source.LookupAsync(lookup, cancellationToken).ConfigureAwait(false);
                    if (answer.Kind != CommunityAnswerKind.Unavailable)
                    {
                        _answers[key] = answer;
                    }
                }

                var segments = CommunityFallback.ToSegments(
                    answer,
                    mode,
                    itemId,
                    lookup.Duration,
                    config.CommunityDurationToleranceSeconds);
                if (segments.Count == 0)
                {
                    continue;
                }

                foreach (var segment in segments)
                {
                    var dto = new MediaSegmentDto
                    {
                        Id = Guid.NewGuid(),
                        ItemId = itemId,
                        Type = type,
                        StartTicks = TickConversions.FromSeconds(segment.Start),
                        EndTicks = TickConversions.FromSeconds(segment.End)
                    };
                    var adjusted = AdjustSegment(dto, durationTicks, offsets, tailBuffer);
                    if (adjusted is not null)
                    {
                        added.Add(adjusted);
                    }
                }

                return;
            }
        }
    }

    private IntroSkipperDatabase GetDatabase()
        => _database ??= _serviceProvider!.GetRequiredService<IntroSkipperDatabase>();

    private IReadOnlyList<ICommunitySource> GetSources()
        => _sources ??= [.. _serviceProvider!.GetServices<ICommunitySource>()];

    [LoggerMessage(Level = LogLevel.Debug, Message = "On-demand playback community lookup timed out for item {ItemId}; passing response through unchanged.")]
    private static partial void LogLookupTimedOut(ILogger logger, Guid itemId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "On-demand playback community lookup failed for item {ItemId}; passing response through unchanged.")]
    private static partial void LogLookupFailed(ILogger logger, Guid itemId, Exception exception);
}
