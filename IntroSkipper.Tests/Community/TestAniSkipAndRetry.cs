// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Community;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Helper;
using IntroSkipper.Manager;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// AniSkip answers and id resolution (including multi-season ARM v2 mapping), and the retry of
/// recently added episodes that community lookups left empty.
/// </summary>
public sealed class TestAniSkipAndRetry
{
    private const string Found = """
        {"found":true,"statusCode":200,"results":[
          {"interval":{"startTime":50,"endTime":60},"skipType":"mixed-op","episodeLength":1420.1},
          {"interval":{"startTime":10.5,"endTime":100.25},"skipType":"op","episodeLength":1420.1},
          {"interval":{"startTime":1330,"endTime":1420},"skipType":"ed","episodeLength":1420.1}]}
        """;

    private const string MultiSeasonArmArray = """
        [
          {"myanimelist":38000,"thetvdb":348545,"thetvdb-season":1,"themoviedb":85937,"themoviedb-season":1,"media":"TV"},
          {"myanimelist":49926,"thetvdb":348545,"thetvdb-season":2,"themoviedb":85937,"themoviedb-season":2,"media":"TV"},
          {"myanimelist":40456,"thetvdb":348545,"thetvdb-season":0,"themoviedb":85937,"themoviedb-season":null,"media":"MOVIE"},
          {"myanimelist":50265,"thetvdb":999,"thetvdb-season":4,"themoviedb":120089,"themoviedb-season":4,"media":"TV"},
          {"myanimelist":50602,"thetvdb":999,"thetvdb-season":4,"themoviedb":120089,"themoviedb-season":4,"media":"TV"}
        ]
        """;

    [Fact]
    public void ParsePrefersPlainKindsAndReportsReleaseLength()
    {
        var answer = AniSkipSource.Parse(Json(Found));

        Assert.Equal(CommunityAnswerKind.Found, answer.Kind);
        Assert.Equal(new CommunityRange(10.5, 100.25), Assert.Single(answer.Intro));
        Assert.Equal(new CommunityRange(1330, 1420), Assert.Single(answer.Credits));
        Assert.Equal(1420.1, answer.ReferenceDuration);
    }

    [Fact]
    public void ParseFallsBackToMixedAndTreatsEmptyOrBrokenAsMiss()
    {
        var mixed = AniSkipSource.Parse(Json("""{"found":true,"results":[{"interval":{"startTime":5,"endTime":50},"skipType":"mixed-ed","episodeLength":0}]}"""));
        Assert.Empty(mixed.Intro);
        Assert.Equal(new CommunityRange(5, 50), Assert.Single(mixed.Credits));
        Assert.Null(mixed.ReferenceDuration);

        Assert.Equal(CommunityAnswerKind.NotFound, AniSkipSource.Parse(Json("""{"found":false,"results":[]}""")).Kind);
        Assert.Equal(CommunityAnswerKind.NotFound, AniSkipSource.Parse(Json("""{"results":[{"interval":{"startTime":9,"endTime":3},"skipType":"op"}]}""")).Kind);
        Assert.Equal(CommunityAnswerKind.NotFound, AniSkipSource.Parse(Json("[]")).Kind);
    }

    [Fact]
    public async Task SeasonMalIdWinsOverSeriesMalIdWithoutMapping()
    {
        var handler = new RecordingHandler(_ => Reply(HttpStatusCode.OK, Found));
        var source = Source(handler);

        var answer = await source.LookupAsync(Lookup(season: 2, episode: 5, mal: "38000", seasonMal: "49926"), CancellationToken.None);

        Assert.Equal(CommunityAnswerKind.Found, answer.Kind);
        Assert.Equal([AniSkipSource.BuildUrl(49926, 5)], handler.Urls);
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 0, episode: 1, mal: "5114"), CancellationToken.None)).Kind);
    }

    [Fact]
    public async Task MultiSeasonArmArrayResolvesUnambiguousSeasonsAndMoviesAndSkipsSplitCour()
    {
        var handler = new RecordingHandler(url => url.StartsWith(AniSkipSource.ArmBaseUrl, StringComparison.Ordinal)
            ? Reply(HttpStatusCode.OK, MultiSeasonArmArray)
            : Reply(HttpStatusCode.OK, Found));
        var source = Source(handler);

        // Season 1 and Season 2 both resolve from one cached /api/v2/thetvdb request.
        Assert.Equal(CommunityAnswerKind.Found, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "348545"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.Found, (await source.LookupAsync(Lookup(season: 2, episode: 3, tvdb: "348545"), CancellationToken.None)).Kind);
        Assert.Single(handler.Urls.FindAll(u => u == AniSkipSource.BuildSeriesMappingUrl("thetvdb", "348545")));
        Assert.Contains(AniSkipSource.BuildUrl(38000, 1), handler.Urls);
        Assert.Contains(AniSkipSource.BuildUrl(49926, 3), handler.Urls);

        // Split-cour season 4 maps to two distinct MAL ids (50265 and 50602), so it is refused safely as NotFound.
        handler.Urls.Clear();
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 4, episode: 1, tvdb: "348545"), CancellationToken.None)).Kind);
        Assert.Empty(handler.Urls);

        // Movie entry picks media == "MOVIE".
        var movieLookup = new CommunityLookup(true, null, "85937", null, null, null, string.Empty, 7000);
        Assert.Equal(CommunityAnswerKind.Found, (await source.LookupAsync(movieLookup, CancellationToken.None)).Kind);
        Assert.Contains(AniSkipSource.BuildUrl(40456, 1), handler.Urls);
    }

    [Fact]
    public async Task SingleSeasonAniListIdResolvesViaIdsEndpoint()
    {
        var handler = new RecordingHandler(url => url.StartsWith(AniSkipSource.MappingUrl, StringComparison.Ordinal)
            ? Reply(HttpStatusCode.OK, """{"myanimelist":52991}""")
            : Reply(HttpStatusCode.OK, Found));
        var source = Source(handler);

        var series = new Series { Id = Guid.NewGuid(), Name = "Frieren" };
        var season2 = new Season { Id = Guid.NewGuid(), SeriesId = series.Id, IndexNumber = 2 };
        season2.ProviderIds["AniList"] = "182255";
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = series.Id,
            SeasonId = season2.Id,
            ParentIndexNumber = 2,
            IndexNumber = 4,
            RunTimeTicks = 1420 * TimeSpan.TicksPerSecond,
        };
        var lookup = CommunityFallback.CreateLookup(episode, id => id == series.Id ? series : id == season2.Id ? season2 : null)!;

        Assert.Equal("182255", lookup.AniListId);
        Assert.Equal(CommunityAnswerKind.Found, (await source.LookupAsync(lookup, CancellationToken.None)).Kind);
        Assert.Contains($"{AniSkipSource.MappingUrl}?source=anilist&id=182255", handler.Urls);
        Assert.Contains(AniSkipSource.BuildUrl(52991, 4), handler.Urls);
    }

    [Fact]
    public async Task UnknownOrBadRequestMappingIsAMissAndTransientFailureStaysRetriable()
    {
        var status = HttpStatusCode.BadRequest;
        var handler = new RecordingHandler(_ => Reply(status, """{"statusCode":400}"""));
        var source = Source(handler);

        // 400 and 404 are definitive misses and cached, never marking the item Unavailable.
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "1"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "1"), CancellationToken.None)).Kind);
        Assert.Single(handler.Urls);

        status = HttpStatusCode.InternalServerError;
        Assert.Equal(CommunityAnswerKind.Unavailable, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "2"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.Unavailable, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "2"), CancellationToken.None)).Kind);
        Assert.Equal(3, handler.Urls.Count);
    }

    [Fact]
    public async Task DescribeStatusReportsDisabledReadyAndRateLimitCooldown()
    {
        var now = new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        var source = new AniSkipSource(new Factory(new RecordingHandler(_ => response)), NullLogger<AniSkipSource>.Instance, new FixedTime(now));

        Assert.Equal("disabled", source.DescribeStatus(new PluginConfiguration { CommunityUseAniSkip = false }, now));
        Assert.Equal("ready", source.DescribeStatus(new PluginConfiguration { CommunityUseAniSkip = true }, now));

        _ = await source.LookupAsync(Lookup(season: 1, episode: 1, mal: "38000"), CancellationToken.None);
        Assert.Equal("rate-limited (10 min left)", source.DescribeStatus(new PluginConfiguration { CommunityUseAniSkip = true }, now));
    }

    [Fact]
    public void AniSkipOnlyMarksTheHashWhenItIsTurnedOff()
    {
        var on = new PluginConfiguration { EnableCommunityFallback = true };
        var explicitOn = new PluginConfiguration { EnableCommunityFallback = true, CommunityUseAniSkip = true };
        var off = new PluginConfiguration { EnableCommunityFallback = true, CommunityUseAniSkip = false };

        Assert.Equal(
            ConfigHasher.Analysis(on, AnalysisMode.Introduction, AnalyzerAction.Default, true),
            ConfigHasher.Analysis(explicitOn, AnalysisMode.Introduction, AnalyzerAction.Default, true));
        Assert.NotEqual(
            ConfigHasher.Analysis(on, AnalysisMode.Introduction, AnalyzerAction.Default, true),
            ConfigHasher.Analysis(off, AnalysisMode.Introduction, AnalyzerAction.Default, true));
        Assert.Equal(
            ConfigHasher.Analysis(new PluginConfiguration(), AnalysisMode.Introduction, AnalyzerAction.Default, true),
            ConfigHasher.Analysis(new PluginConfiguration { CommunityUseAniSkip = false }, AnalysisMode.Introduction, AnalyzerAction.Default, true));
    }

    [Theory]
    [InlineData(true, 7, 1, 0, true)] // added a day ago, never retried: due
    [InlineData(true, 7, 9, 0, false)] // past the window
    [InlineData(true, 0, 1, 0, false)] // retry disabled
    [InlineData(false, 7, 1, 0, false)] // fallback off
    [InlineData(true, 7, 1, 11, false)] // retried 11 h ago: too soon
    [InlineData(true, 7, 1, 13, true)] // retried 13 h ago: due again
    public void NoSegmentsEpisodeReopensOnlyInsideTheWindowAndGap(bool fallback, int days, int addedDaysAgo, int hoursSinceAttempt, bool reopens)
    {
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var config = new PluginConfiguration { EnableCommunityFallback = fallback, CommunityRetryNoSegmentsDays = days };
        var episode = new QueuedEpisode { EpisodeId = Guid.NewGuid(), DateAdded = now.UtcDateTime.AddDays(-addedDaysAgo) };
        if (hoursSinceAttempt > 0)
        {
            CommunityRetry.RecordAttempt(episode.EpisodeId, AnalysisMode.Introduction, now.AddHours(-hoursSinceAttempt));
        }

        Assert.Equal(reopens, CommunityRetry.ShouldReopen(config, episode, AnalysisMode.Introduction, now));
        if (reopens)
        {
            // Reopening counts as an attempt, so the same pass cannot reopen it twice.
            Assert.False(CommunityRetry.ShouldReopen(config, episode, AnalysisMode.Introduction, now));
        }
    }

    [Fact]
    public void RetryNeverAppliesToModesTheCommunityCannotFill()
        => Assert.False(CommunityRetry.ShouldReopen(
            new PluginConfiguration { EnableCommunityFallback = true },
            new QueuedEpisode { EpisodeId = Guid.NewGuid(), DateAdded = DateTime.UtcNow },
            AnalysisMode.Recap,
            DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(1, EpisodeState.NotAnalyzed)] // recent: reopened for another community lookup
    [InlineData(30, EpisodeState.NoSegments)] // old: stays settled
    public void VerifierReopensRecentSettledNoSegmentsEpisode(int addedDaysAgo, EpisodeState expected)
    {
        var config = new PluginConfiguration { EnableCommunityFallback = true };
        var id = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var hash = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, true);
        var snapshot = new SeasonQueueSnapshot(
            new Dictionary<(Guid ItemId, AnalysisMode Mode), AnalysisRecord> { [(id, AnalysisMode.Introduction)] = new(hash, null) },
            new Dictionary<AnalysisMode, AnalyzerAction>(),
            new Dictionary<Guid, IReadOnlySet<AnalysisMode>>(),
            new Dictionary<AnalysisMode, IReadOnlySet<Guid>>());
        var episode = new QueuedEpisode { EpisodeId = id, DateAdded = now.UtcDateTime.AddDays(-addedDaysAgo) };

        new QueueVerifier(config, [AnalysisMode.Introduction], snapshot, true, timeProvider: new FixedTime(now)).Classify(episode);

        Assert.Equal(expected, episode.GetAnalyzed(AnalysisMode.Introduction));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static HttpResponseMessage Reply(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static CommunityLookup Lookup(int season, int episode, string? tvdb = null, string? mal = null, string? seasonMal = null)
        => new(false, null, null, tvdb, season, episode, string.Empty, 1420.032, mal, seasonMal);

    private static AniSkipSource Source(RecordingHandler handler)
        => new(new Factory(handler), NullLogger<AniSkipSource>.Instance, TimeProvider.System);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(respond(url));
        }
    }
}
