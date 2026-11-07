// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Filters;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Xunit;

/// <summary>
/// The playback path makes another provider's Intro row obey the configured offsets while
/// leaving Intro Skipper's own rows alone: its rows carry the offsets already. The
/// fixture numbers are the real-world case that motivated the filter — a fixed 90-second
/// SkipMe.db range on an episode whose Opening chapter ends at 90.007 s, next to the
/// plugin's own 5.000–85.007 s row.
/// </summary>
public class TestPlaybackIntroOffsetFilter
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    private static readonly PlaybackIntroOffsets FiveSeconds = new(5, 5, 2.0, false);

    [Theory]
    [InlineData(2496, 92496, 7496, 87496)] // the Bokurano row: 2.496-92.496 s becomes 7.496-87.496 s
    [InlineData(3000, 30000, 8000, 25000)]
    [InlineData(0, 92496, 0, 87496)] // a row pinned to the episode start keeps it: analysis reads that as snapped
    [InlineData(2000, 30000, 2000, 25000)] // the snap threshold itself also counts as snapped
    public void ForeignIntroTakesBothOffsets(int startMs, int endMs, int expectedStartMs, int expectedEndMs)
    {
        var input = Segment(MediaSegmentType.Intro, startMs * TicksPerMillisecond, endMs * TicksPerMillisecond);

        var output = PlaybackIntroOffsetFilter.AdjustIntro(input, 120 * TicksPerSecond, FiveSeconds);

        Assert.NotNull(output);
        Assert.Equal(expectedStartMs * TicksPerMillisecond, output!.StartTicks);
        Assert.Equal(expectedEndMs * TicksPerMillisecond, output.EndTicks);
        Assert.Equal(input.Id, output.Id);
        Assert.Equal(input.ItemId, output.ItemId);
        Assert.Equal(input.Type, output.Type);
        Assert.Equal(startMs * TicksPerMillisecond, input.StartTicks);
        Assert.Equal(endMs * TicksPerMillisecond, input.EndTicks);
    }

    [Fact]
    public void OptedInStartOffsetMovesASnappedStartButNeverBeforeZero()
    {
        var optIn = new PlaybackIntroOffsets(5, 5, 2.0, IncludeStartOffsetWhenSnapping: true);
        var fromStart = Segment(MediaSegmentType.Intro, 0, 30000 * TicksPerMillisecond);

        var output = PlaybackIntroOffsetFilter.AdjustIntro(fromStart, 120 * TicksPerSecond, optIn);

        Assert.Equal(5 * TicksPerSecond, output!.StartTicks);
        Assert.Equal(25 * TicksPerSecond, output.EndTicks);
    }

    [Fact]
    public void OffsetWholeRowLeavesNothingToSkip()
    {
        var input = Segment(MediaSegmentType.Intro, 10 * TicksPerSecond, 12 * TicksPerSecond);
        Assert.Null(PlaybackIntroOffsetFilter.AdjustIntro(input, 100 * TicksPerSecond, FiveSeconds));

        var zeroLength = Segment(MediaSegmentType.Intro, 95 * TicksPerSecond, 96 * TicksPerSecond);
        Assert.Null(PlaybackIntroOffsetFilter.AdjustIntro(zeroLength, 100 * TicksPerSecond, FiveSeconds));
    }

    [Fact]
    public void BoundariesAreClampedToTheMediaDuration()
    {
        var input = Segment(MediaSegmentType.Intro, 94 * TicksPerSecond, 300 * TicksPerSecond);

        var output = PlaybackIntroOffsetFilter.AdjustIntro(input, 100 * TicksPerSecond, FiveSeconds);

        Assert.Equal(99 * TicksPerSecond, output!.StartTicks);
        Assert.Equal(100 * TicksPerSecond, output.EndTicks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void UnknownDurationOnlyAppliesTheOffsets(long? durationTicks)
    {
        var input = Segment(MediaSegmentType.Intro, 3 * TicksPerSecond, 200 * TicksPerSecond);

        var output = PlaybackIntroOffsetFilter.AdjustIntro(input, durationTicks, FiveSeconds);

        Assert.Equal(8 * TicksPerSecond, output!.StartTicks);
        Assert.Equal(195 * TicksPerSecond, output.EndTicks);
    }

    [Fact]
    public void ZeroOffsetsLeaveTheRowAsTheServerSentIt()
    {
        var input = Segment(MediaSegmentType.Intro, 2 * TicksPerSecond, 92 * TicksPerSecond);
        var none = new PlaybackIntroOffsets(0, 0, 2.0, false);

        Assert.True(none.IsNoOp);
        Assert.Same(input, PlaybackIntroOffsetFilter.AdjustIntro(input, 100 * TicksPerSecond, none));
    }

    [Fact]
    public void NegativeOffsetsCountAsZeroInsteadOfWideningTheSkip()
    {
        var offsets = PlaybackIntroOffsets.From(new PluginConfiguration { IntroStartOffset = -5, IntroEndOffset = -5 });

        Assert.True(offsets.IsNoOp);
        Assert.Equal(0, offsets.StartSeconds);
        Assert.Equal(0, offsets.EndSeconds);
    }

    [Fact]
    public void ResponseClonesOnlyForeignIntroRowsAndPreservesPageMetadata()
    {
        var itemId = Guid.NewGuid();
        var ownIntro = Segment(MediaSegmentType.Intro, 5 * TicksPerSecond, 85 * TicksPerSecond, itemId);
        var foreignIntro = Segment(MediaSegmentType.Intro, 3 * TicksPerSecond, 92 * TicksPerSecond, itemId);
        var foreignOutro = Segment(MediaSegmentType.Outro, 1300 * TicksPerSecond, 1420 * TicksPerSecond, itemId);
        var foreignRecap = Segment(MediaSegmentType.Recap, 0, 30 * TicksPerSecond, itemId);
        var items = new[] { ownIntro, foreignIntro, foreignOutro, foreignRecap };
        var input = new QueryResult<MediaSegmentDto> { Items = items, StartIndex = 11, TotalRecordCount = 40 };

        var output = PlaybackIntroOffsetFilter.AdjustSegments(
            input, new HashSet<Guid> { foreignIntro.Id, foreignOutro.Id, foreignRecap.Id }, 1420 * TicksPerSecond, FiveSeconds);

        Assert.NotSame(input, output);
        Assert.Equal(11, output.StartIndex);
        Assert.Equal(40, output.TotalRecordCount);
        Assert.Same(ownIntro, output.Items[0]);
        Assert.Equal(8 * TicksPerSecond, output.Items[1].StartTicks);
        Assert.Equal(87 * TicksPerSecond, output.Items[1].EndTicks);
        Assert.Same(foreignOutro, output.Items[2]);
        Assert.Same(foreignRecap, output.Items[3]);
        Assert.Same(items, input.Items);
        Assert.Equal(92 * TicksPerSecond, foreignIntro.EndTicks);
    }

    [Fact]
    public void RemovedForeignRowLeavesTheResponseAndTheCount()
    {
        var itemId = Guid.NewGuid();
        var consumed = Segment(MediaSegmentType.Intro, 9 * TicksPerSecond, 12 * TicksPerSecond, itemId);
        var input = new QueryResult<MediaSegmentDto> { Items = [consumed], StartIndex = 3, TotalRecordCount = 2 };

        var output = PlaybackIntroOffsetFilter.AdjustSegments(input, new HashSet<Guid> { consumed.Id }, 100 * TicksPerSecond, FiveSeconds);

        Assert.Empty(output.Items);
        Assert.Equal(3, output.StartIndex);
        Assert.Equal(1, output.TotalRecordCount);
        Assert.Single(input.Items);
    }

    [Fact]
    public void UnattributedResponseIsReturnedUnchanged()
    {
        var itemId = Guid.NewGuid();
        var input = new QueryResult<MediaSegmentDto>([Segment(MediaSegmentType.Intro, 2 * TicksPerSecond, 92 * TicksPerSecond, itemId)]);

        Assert.Same(input, PlaybackIntroOffsetFilter.AdjustSegments(input, new HashSet<Guid>(), 100 * TicksPerSecond, FiveSeconds));
    }

    [Fact]
    public async Task ForeignRowIsAdjustedWhileThePluginRowIsLeftAlone()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroStartOffset = 5, IntroEndOffset = 5 });
        var itemId = Guid.NewGuid();
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = itemId, RunTimeTicks = 1420 * TicksPerSecond });
        var own = Segment(MediaSegmentType.Intro, 5 * TicksPerSecond, 85 * TicksPerSecond, itemId);
        var foreign = Segment(MediaSegmentType.Intro, 2496 * TicksPerMillisecond, 92496 * TicksPerMillisecond, itemId);
        var store = new FakeJellyfinSegmentStore();
        store.ForeignSegments[foreign.Id] = foreign;
        var input = new QueryResult<MediaSegmentDto>([own, foreign]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(
            PlaybackFilterTestHelpers.CreateContext(itemId, result),
            new PlaybackIntroOffsetFilter(library, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaybackIntroOffsetFilter>.Instance));

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        Assert.Same(own, output.Items[0]);
        Assert.Equal(7496 * TicksPerMillisecond, output.Items[1].StartTicks);
        Assert.Equal(87496 * TicksPerMillisecond, output.Items[1].EndTicks);
        Assert.Equal(92496 * TicksPerMillisecond, foreign.EndTicks);
        Assert.Equal(1, store.ForeignSegmentIdReadCount);
    }

    [Fact]
    public async Task PluginOwnRowPersistedInJellyfinsTableKeepsItsBoundaries()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroStartOffset = 5, IntroEndOffset = 5 });
        var itemId = Guid.NewGuid();
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = itemId, RunTimeTicks = 1420 * TicksPerSecond });
        var own = Segment(MediaSegmentType.Intro, 5 * TicksPerSecond, 85 * TicksPerSecond, itemId);
        // Own rows are seeded as own rows of the store and are absent from ForeignSegments,
        // exactly like a mirrored row whose provider id Jellyfin records as Intro Skipper's.
        var store = new FakeJellyfinSegmentStore { ExistingSegments = [own] };
        var input = new QueryResult<MediaSegmentDto>([own]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(
            PlaybackFilterTestHelpers.CreateContext(itemId, result),
            new PlaybackIntroOffsetFilter(library, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaybackIntroOffsetFilter>.Instance));

        Assert.Same(input, result.Value);
    }

    [Fact]
    public async Task JsonResultIsAdjustedWithoutReplacingResultMetadata()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroStartOffset = 5, IntroEndOffset = 5 });
        var itemId = Guid.NewGuid();
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = itemId, RunTimeTicks = 1420 * TicksPerSecond });
        var foreign = Segment(MediaSegmentType.Intro, 2496 * TicksPerMillisecond, 92496 * TicksPerMillisecond, itemId);
        var store = new FakeJellyfinSegmentStore();
        store.ForeignSegments[foreign.Id] = foreign;
        var result = new JsonResult(new QueryResult<MediaSegmentDto>([foreign])) { StatusCode = 200, ContentType = "application/json" };
        var context = PlaybackFilterTestHelpers.CreateContext(itemId, result);

        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackIntroOffsetFilter(library, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaybackIntroOffsetFilter>.Instance));

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        Assert.Equal(87496 * TicksPerMillisecond, Assert.Single(output.Items).EndTicks);
        Assert.Same(result, context.Result);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
    }

    [Fact]
    public async Task UnreadableAttributionLeavesTheResponseUntouched()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroStartOffset = 5, IntroEndOffset = 5 });
        var itemId = Guid.NewGuid();
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = itemId, RunTimeTicks = 1420 * TicksPerSecond });
        var intro = Segment(MediaSegmentType.Intro, 2496 * TicksPerMillisecond, 92496 * TicksPerMillisecond, itemId);
        var store = new FakeJellyfinSegmentStore { ForeignSegmentIdException = new InvalidOperationException("database locked") };
        var input = new QueryResult<MediaSegmentDto>([intro]);
        var result = new OkObjectResult(input);

        await PlaybackFilterTestHelpers.RunAsync(
            PlaybackFilterTestHelpers.CreateContext(itemId, result),
            new PlaybackIntroOffsetFilter(library, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaybackIntroOffsetFilter>.Instance));

        Assert.Same(input, result.Value);
        Assert.Equal(1, store.ForeignSegmentIdReadCount);
    }

    [Fact]
    public async Task PassThroughCasesNeverReadAttribution()
    {
        var store = new FakeJellyfinSegmentStore { ForeignSegmentIdException = new InvalidOperationException("must not be read") };
        var filter = new PlaybackIntroOffsetFilter(null!, store, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaybackIntroOffsetFilter>.Instance);
        var itemId = Guid.NewGuid();
        var intro = Segment(MediaSegmentType.Intro, 3 * TicksPerSecond, 92 * TicksPerSecond, itemId);

        using (EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroStartOffset = 0, IntroEndOffset = 0 }))
        {
            var input = new QueryResult<MediaSegmentDto>([intro]);
            var result = new OkObjectResult(input);
            await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), filter);
            Assert.Same(input, result.Value);
        }

        using (EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroStartOffset = 5, IntroEndOffset = 5 }))
        {
            // No Intro row to attribute.
            var outroOnly = new QueryResult<MediaSegmentDto>([Segment(MediaSegmentType.Outro, 2 * TicksPerSecond, 92 * TicksPerSecond, itemId)]);
            var result = new OkObjectResult(outroOnly);
            await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), filter);
            Assert.Same(outroOnly, result.Value);

            // Not a GET, not a success, unknown payload, other controller, missing route id.
            var introResult = new OkObjectResult(new QueryResult<MediaSegmentDto>([intro]));
            foreach (var method in new[] { "POST", "PUT", "DELETE", "HEAD", "OPTIONS" })
            {
                await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, introResult, method), filter);
            }

            var created = new ObjectResult(new QueryResult<MediaSegmentDto>([intro])) { StatusCode = 201 };
            await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, created), filter);
            Assert.Equal(201, created.StatusCode);

            var array = new[] { intro };
            var arrayResult = new OkObjectResult(array);
            await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, arrayResult), filter);
            Assert.Same(array, arrayResult.Value);

            var editorPayload = new QueryResult<MediaSegmentDto>([intro]);
            var editorResult = new OkObjectResult(editorPayload);
            await PlaybackFilterTestHelpers.RunAsync(
                PlaybackFilterTestHelpers.CreateContext(itemId, editorResult, controllerType: typeof(PlaybackFixtures.SegmentEditorController)),
                filter);
            Assert.Same(editorPayload, editorResult.Value);

            var noRoutePayload = new QueryResult<MediaSegmentDto>([intro]);
            var noRoute = new OkObjectResult(noRoutePayload);
            await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(itemId, noRoute, includeItemId: false), filter);
            Assert.Same(noRoutePayload, noRoute.Value);
        }

        Assert.Equal(0, store.ForeignSegmentIdReadCount);
    }

    [Fact]
    public async Task RegisteredPlaybackFiltersComposeForOneResponse()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(
            new PluginConfiguration { IntroStartOffset = 5, IntroEndOffset = 5, FirstEpisodeIntroMode = PluginConfiguration.FirstEpisodeIntroModeAnalyze });
        var itemId = Guid.NewGuid();
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = itemId, RunTimeTicks = 1420 * TicksPerSecond });
        var foreignIntro = Segment(MediaSegmentType.Intro, 2496 * TicksPerMillisecond, 92496 * TicksPerMillisecond, itemId);
        var outro = Segment(MediaSegmentType.Outro, 1300 * TicksPerSecond, 1420 * TicksPerSecond, itemId);
        var store = new FakeJellyfinSegmentStore();
        store.ForeignSegments[foreignIntro.Id] = foreignIntro;
        var result = new OkObjectResult(new QueryResult<MediaSegmentDto>([foreignIntro, outro]));

        await PlaybackFilterTestHelpers.RunRegisteredFiltersAsync(PlaybackFilterTestHelpers.CreateContext(itemId, result), library, store);

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        Assert.Equal(7496 * TicksPerMillisecond, output.Items[0].StartTicks);
        Assert.Equal(87496 * TicksPerMillisecond, output.Items[0].EndTicks);
        Assert.Equal(1415 * TicksPerSecond, output.Items[1].EndTicks);
        Assert.Equal(92496 * TicksPerMillisecond, foreignIntro.EndTicks);
        Assert.Equal(1420 * TicksPerSecond, outro.EndTicks);
    }

    private static MediaSegmentDto Segment(MediaSegmentType type, long startTicks, long endTicks, Guid? itemId = null)
        => PlaybackFilterTestHelpers.Segment(itemId ?? Guid.NewGuid(), startTicks, endTicks, type);
}
