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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// AniSkip answers and id resolution, and the retry of recently added episodes that community
/// lookups left empty.
/// </summary>
public sealed class TestAniSkipAndRetry
{
    private const string Found = """
        {"found":true,"statusCode":200,"results":[
          {"interval":{"startTime":50,"endTime":60},"skipType":"mixed-op","episodeLength":1420.1},
          {"interval":{"startTime":10.5,"endTime":100.25},"skipType":"op","episodeLength":1420.1},
          {"interval":{"startTime":1330,"endTime":1420},"skipType":"ed","episodeLength":1420.1}]}
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
    public async Task DirectMalIdIsUsedForAnySeasonWithoutMapping()
    {
        var handler = new RecordingHandler(_ => Reply(HttpStatusCode.OK, Found));
        var source = Source(handler);

        var answer = await source.LookupAsync(Lookup(season: 2, episode: 5, mal: "5114"), CancellationToken.None);

        Assert.Equal(CommunityAnswerKind.Found, answer.Kind);
        Assert.Equal([AniSkipSource.BuildUrl(5114, 5)], handler.Urls);
    }

    [Fact]
    public async Task MalIdIsMappedFromGeneralIdsForSeasonOneOnlyAndRemembered()
    {
        var handler = new RecordingHandler(url => url.StartsWith(AniSkipSource.MappingUrl, StringComparison.Ordinal)
            ? Reply(HttpStatusCode.OK, """{"myanimelist":5114,"thetvdb":81797}""")
            : Reply(HttpStatusCode.OK, Found));
        var source = Source(handler);

        Assert.Equal(CommunityAnswerKind.Found, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "81797"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.Found, (await source.LookupAsync(Lookup(season: 1, episode: 2, tvdb: "81797"), CancellationToken.None)).Kind);
        Assert.Single(handler.Urls.FindAll(u => u.StartsWith(AniSkipSource.MappingUrl, StringComparison.Ordinal)));
        Assert.Contains(AniSkipSource.BuildUrl(5114, 2), handler.Urls);

        handler.Urls.Clear();
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 2, episode: 1, tvdb: "81797"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 0, episode: 1, mal: "5114"), CancellationToken.None)).Kind);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task UnknownMappingIsAMissAndATransientFailureStaysRetriable()
    {
        var status = HttpStatusCode.NotFound;
        var handler = new RecordingHandler(_ => Reply(status, "{}"));
        var source = Source(handler);

        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "1"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.NotFound, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "1"), CancellationToken.None)).Kind);
        Assert.Single(handler.Urls); // the miss was remembered

        status = HttpStatusCode.InternalServerError;
        Assert.Equal(CommunityAnswerKind.Unavailable, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "2"), CancellationToken.None)).Kind);
        Assert.Equal(CommunityAnswerKind.Unavailable, (await source.LookupAsync(Lookup(season: 1, episode: 1, tvdb: "2"), CancellationToken.None)).Kind);
        Assert.Equal(3, handler.Urls.Count); // the failure was not remembered
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

    private static CommunityLookup Lookup(int season, int episode, string? tvdb = null, string? mal = null)
        => new(false, null, null, tvdb, season, episode, string.Empty, 1420.032, mal);

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
