// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace IntroSkipper.Tests;

public class TestPlaybackOutroTailContract
{
    [Theory]
    [InlineData(100, 95)]
    [InlineData(98, 95)]
    [InlineData(103, 95)]
    [InlineData(95, 95)]
    [InlineData(90, 90)]
    public async Task RegisteredPlaybackFiltersPreserveAtLeastFiveSeconds(long originalEndSeconds, long expectedEndSeconds)
    {
        using var scope = EntrypointTestHelpers.CreatePluginScope(new PluginConfiguration
        {
            IntroEndOffset = 5,
            SkipFirstEpisode = false
        });
        var itemId = Guid.NewGuid();
        var item = new Episode { Id = itemId, RunTimeTicks = 100 * TimeSpan.TicksPerSecond };
        var input = PlaybackFilterTestHelpers.Segment(itemId, 10 * TimeSpan.TicksPerSecond, originalEndSeconds * TimeSpan.TicksPerSecond);
        var original = new QueryResult<MediaSegmentDto>([input]);
        var result = new OkObjectResult(original);
        var context = PlaybackFilterTestHelpers.CreateContext(itemId, result);

        await PlaybackFilterTestHelpers.RunRegisteredFiltersAsync(context, EntrypointTestHelpers.CreateLibraryManager(item));

        var output = Assert.IsType<QueryResult<MediaSegmentDto>>(result.Value);
        Assert.Equal(expectedEndSeconds * TimeSpan.TicksPerSecond, Assert.Single(output.Items).EndTicks);
        Assert.Equal(originalEndSeconds * TimeSpan.TicksPerSecond, input.EndTicks);
        Assert.Single(original.Items);
    }
}
