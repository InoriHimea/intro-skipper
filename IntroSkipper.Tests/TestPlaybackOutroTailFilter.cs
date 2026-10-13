// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Filters;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

public class TestPlaybackOutroTailFilter
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    [Theory]
    [InlineData(100, 10, 100, 5, 95)]
    [InlineData(100, 10, 98, 5, 95)]
    [InlineData(100, 10, 103, 5, 95)]
    [InlineData(100, 10, 95, 5, 95)]
    [InlineData(100, 10, 90, 5, 90)]
    [InlineData(100, 95, 100, 5, -1)]
    [InlineData(100, 98, 100, 5, -1)]
    [InlineData(3, 0, 3, 5, -1)]
    [InlineData(100, -1, 100, 5, -1)]
    [InlineData(100, 10, 10, 5, -1)]
    [InlineData(100, 10, 9, 5, -1)]
    [InlineData(100, 10, 100, 0, 100)]
    [InlineData(100, 10, 100, -5, 100)]
    [InlineData(0, 10, 100, 5, 100)]
    [InlineData(-1, 10, 100, 5, 100)]
    public void OutroClipping(long duration, long start, long end, int buffer, long expectedEnd)
    {
        var input = PlaybackFilterTestHelpers.Segment(Guid.NewGuid(), start * TicksPerSecond, end * TicksPerSecond);
        var output = PlaybackOutroTailFilter.ClipOutro(input, duration * TicksPerSecond, buffer);
        if (expectedEnd < 0)
        {
            Assert.Null(output);
        }
        else
        {
            Assert.NotNull(output);
            Assert.Equal(expectedEnd * TicksPerSecond, output!.EndTicks);
            Assert.Equal(input.Id, output.Id);
            Assert.Equal(input.ItemId, output.ItemId);
            Assert.Equal(input.Type, output.Type);
            Assert.Equal(input.StartTicks, output.StartTicks);
            if (expectedEnd == end)
            {
                Assert.Same(input, output);
            }
            else
            {
                Assert.NotSame(input, output);
            }
        }

        Assert.Equal(end * TicksPerSecond, input.EndTicks);
    }

    [Fact]
    public void KeepsTickPrecisionAndDoesNotOverflowBufferMultiplication()
    {
        var input = PlaybackFilterTestHelpers.Segment(Guid.NewGuid(), 0, (95 * TicksPerSecond) + 1);
        Assert.Equal(95 * TicksPerSecond, PlaybackOutroTailFilter.ClipOutro(input, 100 * TicksPerSecond, 5)!.EndTicks);
        input.EndTicks = (95 * TicksPerSecond) - 1;
        Assert.Same(input, PlaybackOutroTailFilter.ClipOutro(input, 100 * TicksPerSecond, 5));
        input.EndTicks = long.MaxValue;
        var result = PlaybackOutroTailFilter.ClipOutro(input, long.MaxValue, int.MaxValue)!;
        Assert.Equal(long.MaxValue - ((long)int.MaxValue * TicksPerSecond), result.EndTicks);
    }

    [Fact]
    public void EveryNonOutroTypeIsUnchanged()
    {
        foreach (var type in Enum.GetValues<MediaSegmentType>().Append((MediaSegmentType)123).Where(type => type != MediaSegmentType.Outro))
        {
            var input = PlaybackFilterTestHelpers.Segment(Guid.NewGuid(), 0, 100 * TicksPerSecond, type);
            Assert.Same(input, PlaybackOutroTailFilter.ClipOutro(input, 100 * TicksPerSecond, 5));
            Assert.Equal(100 * TicksPerSecond, input.EndTicks);
        }
    }

    [Fact]
    public void MixedResponseClonesOnlyChangedDtosAndPreservesPageMetadata()
    {
        var id = Guid.NewGuid();
        var first = PlaybackFilterTestHelpers.Segment(id, 10 * TicksPerSecond, 100 * TicksPerSecond);
        var second = PlaybackFilterTestHelpers.Segment(id, 20 * TicksPerSecond, 102 * TicksPerSecond);
        var early = PlaybackFilterTestHelpers.Segment(id, 10 * TicksPerSecond, 90 * TicksPerSecond);
        var intro = PlaybackFilterTestHelpers.Segment(id, 0, 100 * TicksPerSecond, MediaSegmentType.Intro);
        var originalItems = new[] { first, intro, second, early };
        var input = new QueryResult<MediaSegmentDto> { Items = originalItems, StartIndex = 37, TotalRecordCount = 100 };
        var alias = new QueryResult<MediaSegmentDto>(originalItems);

        var output = PlaybackOutroTailFilter.ClipSegments(input, 100 * TicksPerSecond, 5);

        Assert.NotSame(input, output);
        Assert.NotSame(originalItems, output.Items);
        Assert.Equal(37, output.StartIndex);
        Assert.Equal(100, output.TotalRecordCount);
        Assert.Equal(originalItems.Select(segment => segment.Id), output.Items.Select(segment => segment.Id));
        Assert.Equal(new[] { 95L, 100L, 95L, 90L }.Select(seconds => seconds * TicksPerSecond), output.Items.Select(segment => segment.EndTicks));
        Assert.NotSame(first, output.Items[0]);
        Assert.Same(intro, output.Items[1]);
        Assert.Same(early, output.Items[3]);
        Assert.Same(originalItems, input.Items);
        Assert.Equal(100 * TicksPerSecond, alias.Items[0].EndTicks);
        Assert.Equal(102 * TicksPerSecond, alias.Items[2].EndTicks);
        Assert.Same(output, PlaybackOutroTailFilter.ClipSegments(output, 100 * TicksPerSecond, 5));
    }

    [Theory]
    [InlineData(100, 99)]
    [InlineData(0, 0)]
    public void EmptyClippedRangesAreRemovedOnlyFromResponse(int originalTotal, int expectedTotal)
    {
        var id = Guid.NewGuid();
        var consumed = PlaybackFilterTestHelpers.Segment(id, 95 * TicksPerSecond, 100 * TicksPerSecond);
        var shortButValid = PlaybackFilterTestHelpers.Segment(id, 94 * TicksPerSecond, 100 * TicksPerSecond);
        var input = new QueryResult<MediaSegmentDto> { Items = [consumed, shortButValid], StartIndex = 9, TotalRecordCount = originalTotal };
        var output = PlaybackOutroTailFilter.ClipSegments(input, 100 * TicksPerSecond, 5);
        Assert.Equal(expectedTotal, output.TotalRecordCount);
        Assert.Equal(9, output.StartIndex);
        Assert.Equal(95 * TicksPerSecond, Assert.Single(output.Items).EndTicks);
        Assert.Equal(shortButValid.Id, output.Items[0].Id);
        Assert.Equal(2, input.Items.Count());
        Assert.Equal(100 * TicksPerSecond, consumed.EndTicks);
    }

    [Fact]
    public async Task SameSingletonReadsReplacedConfigurationObjects()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, RunTimeTicks = 100 * TicksPerSecond });
        var filter = new PlaybackOutroTailFilter(library);
        await Run(5, 95);
        await Run(0, 100);
        await Run(8, 92);

        async Task Run(int buffer, long expectedEnd)
        {
            EntrypointTestHelpers.SetPropertyOrField(Plugin.Instance!, "Configuration", new PluginConfiguration { IntroEndOffset = buffer });
            var input = new QueryResult<MediaSegmentDto>([PlaybackFilterTestHelpers.Segment(id, 10 * TicksPerSecond, 100 * TicksPerSecond)]);
            var result = new OkObjectResult(input);
            await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result), filter);
            Assert.Equal(expectedEnd * TicksPerSecond, Assert.Single(Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value).Items).EndTicks);
            Assert.Equal(100 * TicksPerSecond, input.Items[0].EndTicks);
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task NonGetRequestsNeverQueryLibrary(string method)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var input = Response(Guid.NewGuid());
        var result = new OkObjectResult(input);
        var filter = new PlaybackOutroTailFilter(null!);
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(input.Items[0].ItemId, result, method), filter);
        Assert.Same(input, result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task DisabledBufferNeverQueriesLibrary(int buffer)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = buffer });
        var input = Response(Guid.NewGuid());
        var result = new OkObjectResult(input);
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(input.Items[0].ItemId, result), new PlaybackOutroTailFilter(null!));
        Assert.Same(input, result.Value);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(304)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task NonSuccessResponseNeverQueriesLibrary(int statusCode)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var input = Response(Guid.NewGuid());
        var result = new ObjectResult(input) { StatusCode = statusCode };
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(input.Items[0].ItemId, result), new PlaybackOutroTailFilter(null!));
        Assert.Same(input, result.Value);
        Assert.Equal(statusCode, result.StatusCode);
    }

    [Fact]
    public async Task ResponseStatusIsUsedOnlyWhenResultStatusIsUnspecified()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var input = Response(id);
        var result = new ObjectResult(input);
        var context = PlaybackFilterTestHelpers.CreateContext(id, result);
        context.HttpContext.Response.StatusCode = 404;
        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackOutroTailFilter(null!));
        Assert.Same(input, result.Value);

        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, RunTimeTicks = 100 * TicksPerSecond });
        result.StatusCode = 200;
        result.ContentTypes.Add("application/vnd.tail-test+json");
        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackOutroTailFilter(library));
        Assert.Equal(95 * TicksPerSecond, Assert.Single(Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value).Items).EndTicks);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("application/vnd.tail-test+json", result.ContentTypes);
        Assert.Same(result, context.Result);
    }

    [Fact]
    public async Task JsonResultIsClippedWithoutReplacingResultMetadata()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var input = Response(id);
        var result = new JsonResult(input) { StatusCode = 200, ContentType = "application/json" };
        var context = PlaybackFilterTestHelpers.CreateContext(id, result);
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, RunTimeTicks = 100 * TicksPerSecond });
        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackOutroTailFilter(library));
        Assert.Equal(95 * TicksPerSecond, Assert.Single(Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value).Items).EndTicks);
        Assert.Same(result, context.Result);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(100 * TicksPerSecond, input.Items[0].EndTicks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task UnknownDurationPreservesResponse(long? duration)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var input = Response(id);
        var result = new OkObjectResult(input);
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, RunTimeTicks = duration });
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result), new PlaybackOutroTailFilter(library));
        Assert.Same(input, result.Value);
    }

    [Fact]
    public async Task MissingItemAndMissingRouteIdPreserveResponse()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var input = Response(id);
        var result = new OkObjectResult(input);
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result), new PlaybackOutroTailFilter(EntrypointTestHelpers.CreateLibraryManager()));
        Assert.Same(input, result.Value);
        var context = PlaybackFilterTestHelpers.CreateContext(id, result, includeItemId: false);
        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackOutroTailFilter(null!));
        context.RouteData.Values["itemId"] = "invalid";
        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackOutroTailFilter(null!));
        context.RouteData.Values["itemId"] = Guid.Empty;
        await PlaybackFilterTestHelpers.RunAsync(context, new PlaybackOutroTailFilter(null!));
        Assert.Same(input, result.Value);
    }

    [Fact]
    public async Task EditorControllerUnknownPayloadAndIntroOnlyResponseDoNotQueryLibrary()
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var input = Response(id);
        var result = new OkObjectResult(input);
        var filter = new PlaybackOutroTailFilter(null!);
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result, controllerType: typeof(PlaybackFixtures.SegmentEditorController)), filter);
        Assert.Same(input, result.Value);
        var array = input.Items;
        result.Value = array;
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result), filter);
        Assert.Same(array, result.Value);
        result.Value = new { version = "12.0.4.0" };
        var unknown = result.Value;
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result), filter);
        Assert.Same(unknown, result.Value);
        input.Items[0].Type = MediaSegmentType.Intro;
        result.Value = input;
        await PlaybackFilterTestHelpers.RunAsync(PlaybackFilterTestHelpers.CreateContext(id, result), filter);
        Assert.Same(input, result.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextIsCalledOnceAndItsExceptionIsNotSwallowed(bool fail)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5 });
        var id = Guid.NewGuid();
        var result = new OkObjectResult(Response(id));
        var context = PlaybackFilterTestHelpers.CreateContext(id, result);
        var filter = new PlaybackOutroTailFilter(EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, RunTimeTicks = 100 * TicksPerSecond }));
        var calls = 0;
        var expected = new InvalidOperationException("next failed");
        Task<ResultExecutedContext> Next()
        {
            calls++;
            return fail ? Task.FromException<ResultExecutedContext>(expected) : Task.FromResult(new ResultExecutedContext(context, [], result, context.Controller));
        }

        if (fail)
        {
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => filter.OnResultExecutionAsync(context, Next)));
        }
        else
        {
            await filter.OnResultExecutionAsync(context, Next);
        }

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TailProtectionComposesWithFirstEpisodeFilterInEitherOrder(bool skipFirst, bool tailFirst)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration { IntroEndOffset = 5, SkipFirstEpisode = skipFirst, SkipFirstEpisodeAnime = false });
        var id = Guid.NewGuid();
        var episode = new Episode { Id = id, SeasonId = Guid.NewGuid(), IndexNumber = 1, RunTimeTicks = 100 * TicksPerSecond };
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([], _ => [episode], itemId => itemId == id ? episode : null);
        var intro = PlaybackFilterTestHelpers.Segment(id, 0, 10 * TicksPerSecond, MediaSegmentType.Intro);
        var outro = PlaybackFilterTestHelpers.Segment(id, 20 * TicksPerSecond, 100 * TicksPerSecond);
        var result = new OkObjectResult(new QueryResult<MediaSegmentDto>([intro, outro]));
        var context = PlaybackFilterTestHelpers.CreateContext(id, result);
        var first = new MediaSegmentsFirstEpisodeFilter(library, NullLogger<MediaSegmentsFirstEpisodeFilter>.Instance);
        var tail = new PlaybackOutroTailFilter(library);
        await PlaybackFilterTestHelpers.RunAsync(context, tailFirst ? [tail, first] : [first, tail]);
        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        Assert.Equal(95 * TicksPerSecond, Assert.Single(output.Items, segment => segment.Type == MediaSegmentType.Outro).EndTicks);
        Assert.Equal(skipFirst ? 0 : 1, output.Items.Count(segment => segment.Type == MediaSegmentType.Intro));
        Assert.Equal(skipFirst ? 1 : 2, output.TotalRecordCount);
        Assert.Equal(100 * TicksPerSecond, outro.EndTicks);
    }

    private static QueryResult<MediaSegmentDto> Response(Guid itemId)
        => new([PlaybackFilterTestHelpers.Segment(itemId, 10 * TicksPerSecond, 100 * TicksPerSecond)]);
}
