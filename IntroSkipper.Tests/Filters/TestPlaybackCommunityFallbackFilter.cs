// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Community;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Filters;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// On-demand community fallback during playback for items not yet analyzed by the queue.
/// </summary>
public sealed class TestPlaybackCommunityFallbackFilter : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;
    private readonly TempSegmentDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task UnanalyzedItemGetsOffsetAdjustedIntroAndTailProtectedOutro()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            EnableCommunityFallback = true,
            EnableOnDemandPlaybackCommunityLookup = true,
            ScanIntroduction = true,
            ScanCredits = true,
            IntroStartOffset = 2,
            IntroEndOffset = 5,
            EndSnapThreshold = 2.0,
            IncludeIntroStartOffsetWhenSnapping = false,
            CreditsStartOffset = 3,
            CreditsEndOffset = 2,
            IncludeCreditsEndOffsetWhenSnapping = false,
            OutroTailBufferSeconds = -1,
        });

        var (library, itemId) = CreateEpisodeLibrary();
        var recap = PlaybackFilterTestHelpers.Segment(itemId, 0, 15 * Second, MediaSegmentType.Recap);
        var source = new FakeSource((_, _) => Task.FromResult(new CommunityAnswer(
            CommunityAnswerKind.Found,
            [new CommunityRange(10, 90)],
            [new CommunityRange(1330, 1420)],
            1420)));
        var filter = CreateFilter(library, source);

        var result1 = new OkObjectResult(new QueryResult<MediaSegmentDto>([recap]) { TotalRecordCount = 1 });
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result1), filter);

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result1.Value);
        Assert.Equal(3, output.TotalRecordCount);
        Assert.Equal(3, output.Items.Count);
        Assert.Same(recap, output.Items[0]);
        Assert.Equal(MediaSegmentType.Intro, output.Items[1].Type);
        Assert.Equal(12 * Second, output.Items[1].StartTicks);
        Assert.Equal(85 * Second, output.Items[1].EndTicks);
        Assert.Equal(MediaSegmentType.Outro, output.Items[2].Type);
        Assert.Equal(1333 * Second, output.Items[2].StartTicks);
        Assert.Equal(1415 * Second, output.Items[2].EndTicks);

        // Repeat requests hit the filter's in-memory answer cache without calling the source again.
        var result2 = new OkObjectResult(new QueryResult<MediaSegmentDto>([]));
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result2), filter);
        Assert.Equal(1, source.Calls);
        Assert.Equal(2, Assert.IsType<QueryResult<MediaSegmentDto>>(result2.Value).Items.Count);
    }

    [Fact]
    public async Task ComposesWithOffsetAndTailFiltersWithoutDoubleAdjusting()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            EnableCommunityFallback = true,
            EnableOnDemandPlaybackCommunityLookup = true,
            ScanIntroduction = true,
            ScanCredits = true,
            IntroStartOffset = 2,
            IntroEndOffset = 5,
            CreditsStartOffset = 3,
            OutroTailBufferSeconds = 5,
        });

        var (library, itemId) = CreateEpisodeLibrary();
        var source = new FakeSource((_, _) => Task.FromResult(new CommunityAnswer(
            CommunityAnswerKind.Found,
            [new CommunityRange(10, 90)],
            [new CommunityRange(1330, 1420)],
            1420)));
        var fallback = CreateFilter(library, source);
        var store = new FakeJellyfinSegmentStore();
        var offsets = new PlaybackIntroOffsetFilter(library, store, NullLogger<PlaybackIntroOffsetFilter>.Instance);
        var tail = new PlaybackOutroTailFilter(library);

        var result = new OkObjectResult(new QueryResult<MediaSegmentDto>([]));
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), fallback, offsets, tail);

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        Assert.Equal(12 * Second, output.Items[0].StartTicks);
        Assert.Equal(85 * Second, output.Items[0].EndTicks);
        Assert.Equal(1333 * Second, output.Items[1].StartTicks);
        Assert.Equal(1415 * Second, output.Items[1].EndTicks);
    }

    [Theory]
    [InlineData(AnalysisMode.Introduction)]
    [InlineData(AnalysisMode.Credits)]
    public async Task SettledNoSegmentsItemIsSkippedWithoutQueryingSources(AnalysisMode mode)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(EnabledConfig());
        var (library, itemId) = CreateEpisodeLibrary();
        await _db.Database.MarkItemsAnalyzedAsync(mode, [itemId], "hash");

        var source = new FakeSource((_, _) => Task.FromResult(CommunityAnswer.NotFound));
        var input = new QueryResult<MediaSegmentDto>([]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), CreateFilter(library, source));

        Assert.Same(input, result.Value);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task TombstonedItemIsSkippedWithoutQueryingSources()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(EnabledConfig());
        var (library, itemId) = CreateEpisodeLibrary();
        await _db.Database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Introduction, [new Segment(itemId, new TimeRange(10, 90))], SegmentSource.Chapter);
        var stored = Assert.Single(await _db.Database.GetSegmentsAsync(itemId));
        Assert.NotNull(await _db.Database.DeleteSegmentAsync(itemId, stored.Id));

        var source = new FakeSource((_, _) => Task.FromResult(CommunityAnswer.NotFound));
        var input = new QueryResult<MediaSegmentDto>([]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), CreateFilter(library, source));

        Assert.Same(input, result.Value);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task DisabledItemIsSkippedWithoutQueryingSources()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(EnabledConfig());
        var (library, itemId) = CreateEpisodeLibrary();
        await _db.Database.SetItemDisabledAsync(itemId, disabled: true);

        var source = new FakeSource((_, _) => Task.FromResult(CommunityAnswer.NotFound));
        var input = new QueryResult<MediaSegmentDto>([]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), CreateFilter(library, source));

        Assert.Same(input, result.Value);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(MediaSegmentType.Intro)]
    [InlineData(MediaSegmentType.Outro)]
    public async Task ResponseWithExistingIntroOrOutroIsSkippedWithoutQueryingSources(MediaSegmentType existingType)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(EnabledConfig());
        var (library, itemId) = CreateEpisodeLibrary();
        var source = new FakeSource((_, _) => Task.FromResult(CommunityAnswer.NotFound));
        var input = new QueryResult<MediaSegmentDto>([PlaybackFilterTestHelpers.Segment(itemId, 10 * Second, 90 * Second, existingType)]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), CreateFilter(library, source));

        Assert.Same(input, result.Value);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task SlowSourceExceedingTimeoutPassesThroughOriginalResponse()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(EnabledConfig());
        var (library, itemId) = CreateEpisodeLibrary();
        var source = new FakeSource(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return CommunityAnswer.NotFound;
        });
        var input = new QueryResult<MediaSegmentDto>([]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(
            PlaybackFilterTestHelpers.CreateContext(itemId, result),
            CreateFilter(library, source, TimeSpan.FromMilliseconds(40)));

        Assert.Same(input, result.Value);
        Assert.Equal(1, source.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledFlagsSkipLookup(bool fallbackEnabled, bool onDemandEnabled)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            EnableCommunityFallback = fallbackEnabled,
            EnableOnDemandPlaybackCommunityLookup = onDemandEnabled,
        });
        var (library, itemId) = CreateEpisodeLibrary();
        var source = new FakeSource((_, _) => Task.FromResult(CommunityAnswer.NotFound));
        var input = new QueryResult<MediaSegmentDto>([]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), CreateFilter(library, source));

        Assert.Same(input, result.Value);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task UnanalyzedSeasonPremiereHonorsFirstEpisodeIntroFilterWhileKeepingTailProtectedOutro()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            EnableCommunityFallback = true,
            EnableOnDemandPlaybackCommunityLookup = true,
            FirstEpisodeIntroMode = PluginConfiguration.FirstEpisodeIntroModeIgnore,
            ScanIntroduction = true,
            ScanCredits = true,
            IntroEndOffset = 5,
            OutroTailBufferSeconds = 5,
        });

        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var series = JellyfinItems.Series(seriesId);
        series.SetProviderId(MetadataProvider.Imdb, "tt33258199");
        var season = JellyfinItems.Season(seasonId, seriesId, 1);
        var ep1 = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId, seasonNumber: 1, episodeNumber: 1);
        ep1.RunTimeTicks = 1420 * Second;
        ep1.ParentId = seasonId;
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([], [series, season, ep1]);

        var source = new FakeSource((_, _) => Task.FromResult(new CommunityAnswer(
            CommunityAnswerKind.Found,
            [new CommunityRange(10, 90)],
            [new CommunityRange(1330, 1420)],
            1420)));
        var fallback = CreateFilter(library, source);
        var firstEpisode = new MediaSegmentsFirstEpisodeFilter(library, NullLogger<MediaSegmentsFirstEpisodeFilter>.Instance);
        var offsets = new PlaybackIntroOffsetFilter(library, new FakeJellyfinSegmentStore(), NullLogger<PlaybackIntroOffsetFilter>.Instance);
        var tail = new PlaybackOutroTailFilter(library);

        var result = new OkObjectResult(new QueryResult<MediaSegmentDto>([]));
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(ep1.Id, result), fallback, firstEpisode, offsets, tail);

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        var outro = Assert.Single(output.Items);
        Assert.Equal(MediaSegmentType.Outro, outro.Type);
        Assert.Equal(1415 * Second, outro.EndTicks);
    }

    [Fact]
    public async Task NotFoundCacheExpiresAfterTwentyMinutesWhileFoundStaysCached()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(EnabledConfig());
        var (library, itemId) = CreateEpisodeLibrary();
        var clock = new AdjustableClock(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
        var answer = CommunityAnswer.NotFound;
        var source = new FakeSource((_, _) => Task.FromResult(answer));
        var filter = new PlaybackCommunityFallbackFilter(library, _db.Database, [source], NullLogger<PlaybackCommunityFallbackFilter>.Instance, timeProvider: clock);

        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, new OkObjectResult(new QueryResult<MediaSegmentDto>([]))), filter);
        Assert.Equal(1, source.Calls);

        // Within 20 minutes the cached miss is reused.
        clock.Advance(TimeSpan.FromMinutes(15));
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, new OkObjectResult(new QueryResult<MediaSegmentDto>([]))), filter);
        Assert.Equal(1, source.Calls);

        // Past 20 minutes the miss expires and picks up a newly submitted community timestamp.
        answer = new CommunityAnswer(CommunityAnswerKind.Found, [new CommunityRange(10, 90)], [], 1420);
        clock.Advance(TimeSpan.FromMinutes(6));
        var hitResult = new OkObjectResult(new QueryResult<MediaSegmentDto>([]));
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, hitResult), filter);
        Assert.Equal(2, source.Calls);
        Assert.Single(Assert.IsType<QueryResult<MediaSegmentDto>>(hitResult.Value).Items);

        // Found stays cached well past 20 minutes (up to 6 hours).
        clock.Advance(TimeSpan.FromHours(2));
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, new OkObjectResult(new QueryResult<MediaSegmentDto>([]))), filter);
        Assert.Equal(2, source.Calls);
    }

    private static PluginConfiguration EnabledConfig() => new()
    {
        EnableCommunityFallback = true,
        EnableOnDemandPlaybackCommunityLookup = true,
    };

    private static (ILibraryManager Library, Guid ItemId) CreateEpisodeLibrary()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series" };
        series.SetProviderId(MetadataProvider.Imdb, "tt33258199");
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = series.Id,
            ParentIndexNumber = 1,
            IndexNumber = 16,
            RunTimeTicks = 1420 * Second,
        };
        return (EntrypointTestHelpers.CreateLibraryManager(series, episode), episode.Id);
    }

    private PlaybackCommunityFallbackFilter CreateFilter(
        ILibraryManager library,
        ICommunitySource source,
        TimeSpan? timeout = null)
        => new(library, _db.Database, [source], NullLogger<PlaybackCommunityFallbackFilter>.Instance, timeout);

    private sealed class FakeSource(Func<CommunityLookup, CancellationToken, Task<CommunityAnswer>> lookup) : ICommunitySource
    {
        public int Calls { get; private set; }

        public string Name => "fake";

        public bool IsEnabled(PluginConfiguration config) => true;

        public Task<CommunityAnswer> LookupAsync(CommunityLookup query, CancellationToken cancellationToken)
        {
            Calls++;
            return lookup(query, cancellationToken);
        }
    }

    private sealed class AdjustableClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
