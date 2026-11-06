// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Community;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Db;
using IntroSkipper.Helper;
using IntroSkipper.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class TestCommunityFallback : IDisposable
{
    private const double Duration = 1420.032;
    private readonly TempSegmentDb _db = new();
    private EntrypointTestHelpers.PluginInstanceScope? _scope;

    public void Dispose()
    {
        _scope?.Dispose();
        _db.Dispose();
    }

    // Real answers captured for the episode that motivated the feature (S01E16, 1420.032 s).
    [Fact]
    public void IntroDb_ParsesSegmentsAndTreatsAllNullAsMiss()
    {
        var found = IntroDbSource.Parse(Json("""{"intro":{"start_sec":0,"end_sec":90.083},"recap":null,"outro":{"start_sec":1330.083,"end_sec":1420.083},"post_credits":null}"""));
        Assert.Equal(CommunityAnswerKind.Found, found.Kind);
        Assert.Equal(new CommunityRange(0, 90.083), Assert.Single(found.Intro));
        Assert.Equal(new CommunityRange(1330.083, 1420.083), Assert.Single(found.Credits));

        var unknown = IntroDbSource.Parse(Json("""{"intro":null,"recap":null,"outro":null,"post_credits":null}"""));
        Assert.Equal(CommunityAnswerKind.NotFound, unknown.Kind);
    }

    [Fact]
    public void TheIntroDb_MapsNullBoundsAndZeroMeansNoSegment()
    {
        var found = TheIntroDbSource.Parse(Json("""{"tvdb_id":453694,"type":"tv","group_duration_ms":1420083,"intro":[{"start_ms":null,"end_ms":90083}],"credits":[{"start_ms":1330083,"end_ms":null}]}"""));
        Assert.Equal(new CommunityRange(0, 90.083), Assert.Single(found.Intro));
        Assert.Equal(new CommunityRange(1330.083, null), Assert.Single(found.Credits));
        Assert.Equal(1420.083, found.ReferenceDuration);

        var none = TheIntroDbSource.Parse(Json("""{"tvdb_id":1,"type":"tv","intro":[{"start_ms":null,"end_ms":0}],"credits":[{"start_ms":0,"end_ms":0}]}"""));
        Assert.Equal(CommunityAnswerKind.NotFound, none.Kind);

        // The episode-list fallback shape carries no timestamps.
        Assert.Equal(CommunityAnswerKind.NotFound, TheIntroDbSource.Parse(Json("""{"episodes":[]}""")).Kind);
    }

    [Fact]
    public void TheIntroDb_TriesEveryKnownIdInServiceOrder()
    {
        var urls = TheIntroDbSource.BuildUrls(Episode(imdb: "tt33258199", tmdb: "280042", tvdb: "453694"));

        Assert.Equal(3, urls.Count);
        Assert.StartsWith(TheIntroDbSource.BaseUrl + "?tvdb_id=453694&season=1&episode=16", urls[0], StringComparison.Ordinal);
        Assert.StartsWith(TheIntroDbSource.BaseUrl + "?tmdb_id=280042&", urls[1], StringComparison.Ordinal);
        Assert.StartsWith(TheIntroDbSource.BaseUrl + "?imdb_id=tt33258199&", urls[2], StringComparison.Ordinal);
        Assert.All(urls, url => Assert.Contains("&duration_ms=1420032&details=true", url, StringComparison.Ordinal));
        Assert.All(urls, url => Assert.DoesNotContain("mapping=", url, StringComparison.Ordinal));
    }

    [Fact]
    public void NonAiredOrder_IsMappedByTheIntroDbAndSkippedByIntroDb()
    {
        var lookup = Episode(imdb: "tt33258199", tmdb: "280042", tvdb: null) with { DisplayOrder = "absolute" };

        Assert.Null(IntroDbSource.BuildUrl(lookup));
        Assert.All(TheIntroDbSource.BuildUrls(lookup), url => Assert.Contains("&mapping=absolute", url, StringComparison.Ordinal));
        Assert.Equal(
            IntroDbSource.BaseUrl + "?imdb_id=tt33258199&season=1&episode=16",
            IntroDbSource.BuildUrl(lookup with { DisplayOrder = "aired" }));
    }

    [Theory]
    [InlineData(1420.083, 1420.032)] // a few ms past the file end is the same release: clamped
    [InlineData(null, 1420.032)]     // open end means end of media
    public void ToSegments_ClampsSameReleaseCreditsToFileEnd(double? end, double expected)
    {
        var answer = new CommunityAnswer(CommunityAnswerKind.Found, [], [new CommunityRange(1330.083, end)], 1420.083);

        var segment = Assert.Single(CommunityFallback.ToSegments(answer, AnalysisMode.Credits, Guid.NewGuid(), Duration, 5));

        Assert.Equal(1330.083, segment.Start, 3);
        Assert.Equal(expected, segment.End, 3);
    }

    [Theory]
    [InlineData(1500.0, null)]   // measured on a longer release
    [InlineData(null, 1440.0)]  // range runs 20 s past the local file
    public void ToSegments_RejectsTimestampsOfAnotherRelease(double? reference, double? end)
    {
        var answer = new CommunityAnswer(CommunityAnswerKind.Found, [], [new CommunityRange(1330, end)], reference);

        Assert.Empty(CommunityFallback.ToSegments(answer, AnalysisMode.Credits, Guid.NewGuid(), Duration, 5));
    }

    [Fact]
    public async Task Fill_WritesOffsetAdjustedCommunityIntroOnlyWhereLocalAnalysisFoundNothing()
    {
        var config = Config();
        var (seriesId, empty) = SetupSeries(config);
        var local = QueuedEpisode(seriesId);
        var database = _db.Database;
        await database.ReplaceAutoSegmentsAsync(local.EpisodeId, AnalysisMode.Introduction, [new Segment(local.EpisodeId, new TimeRange(10, 100))], SegmentSource.Chromaprint);
        local.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.Analyzed);
        empty.SetAnalyzed(AnalysisMode.Introduction, EpisodeState.NoSegments);
        var source = new FakeSource(Found(intro: new CommunityRange(0, 90.083)));

        await Fallback(source).FillAsync([local, empty], AnalysisMode.Introduction, config, CancellationToken.None);

        // Only the empty episode was asked about; the local result is untouched.
        Assert.Single(source.Asked);
        Assert.Equal(SegmentSource.Chromaprint, Assert.Single(await database.GetSegmentsAsync(local.EpisodeId)).Source);

        var row = Assert.Single(await database.GetSegmentsAsync(empty.EpisodeId));
        Assert.Equal(SegmentSource.Community, row.Source);
        Assert.Equal(0, row.ToSegment().Start, 3);
        Assert.Equal(85.083, row.ToSegment().End, 3); // IntroEndOffset = 5
        Assert.Equal(EpisodeState.Analyzed, empty.GetAnalyzed(AnalysisMode.Introduction));
    }

    [Fact]
    public async Task Fill_ReusesOneLookupForIntroAndCredits()
    {
        var config = Config();
        var (_, episode) = SetupSeries(config);
        var source = new FakeSource(Found(intro: new CommunityRange(0, 90), credits: new CommunityRange(1330, null)));
        var fallback = Fallback(source);

        await fallback.FillAsync([episode], AnalysisMode.Introduction, config, CancellationToken.None);
        await fallback.FillAsync([episode], AnalysisMode.Credits, config, CancellationToken.None);

        Assert.Single(source.Asked);
        Assert.Contains(await _db.Database.GetSegmentsAsync(episode.EpisodeId), s => s.Type == AnalysisMode.Credits && s.Source == SegmentSource.Community);
    }

    [Fact]
    public async Task Fill_KeepsItemRetriableWhenNoSourceCouldBeAsked()
    {
        var config = Config();
        var (_, episode) = SetupSeries(config);
        episode.SetAnalyzed(AnalysisMode.Credits, EpisodeState.NoSegments);

        await Fallback(new FakeSource(CommunityAnswer.Unavailable)).FillAsync([episode], AnalysisMode.Credits, config, CancellationToken.None);

        Assert.Equal(EpisodeState.AnalysisFailed, episode.GetAnalyzed(AnalysisMode.Credits));
        Assert.Empty(await _db.Database.GetSegmentsAsync(episode.EpisodeId));
    }

    [Fact]
    public async Task Fill_FallsThroughToNextSourceAndDoesNothingWhenDisabled()
    {
        var config = Config();
        var (_, episode) = SetupSeries(config);
        var first = new FakeSource(CommunityAnswer.NotFound, "first");
        var second = new FakeSource(Found(intro: new CommunityRange(0, 90)), "second");

        await Fallback(first, second).FillAsync([episode], AnalysisMode.Introduction, new PluginConfiguration { EnableCommunityFallback = false }, CancellationToken.None);
        Assert.Empty(first.Asked);

        await Fallback(first, second).FillAsync([episode], AnalysisMode.Introduction, config, CancellationToken.None);
        Assert.Single(first.Asked);
        Assert.Single(second.Asked);
        Assert.Equal(SegmentSource.Community, Assert.Single(await _db.Database.GetSegmentsAsync(episode.EpisodeId)).Source);
    }

    [Fact]
    public void AnalysisHash_IsUnchangedWhileFallbackIsOff()
    {
        // The fallback-off hash follows the current upstream chapter-pattern defaults.
        Assert.Equal("7EF68040EE4E4EC7", ConfigHasher.Analysis(new PluginConfiguration(), AnalysisMode.Credits, AnalyzerAction.Default, true));
        Assert.NotEqual(
            ConfigHasher.Analysis(new PluginConfiguration(), AnalysisMode.Introduction, AnalyzerAction.Default, true),
            ConfigHasher.Analysis(new PluginConfiguration { EnableCommunityFallback = true }, AnalysisMode.Introduction, AnalyzerAction.Default, true));
        Assert.Equal(
            ConfigHasher.Analysis(new PluginConfiguration(), AnalysisMode.Recap, AnalyzerAction.Default, true),
            ConfigHasher.Analysis(new PluginConfiguration { EnableCommunityFallback = true }, AnalysisMode.Recap, AnalyzerAction.Default, true));
    }

    [Theory]
    [InlineData(true, ChapterSource.JellyfinTable, false)]
    [InlineData(false, ChapterSource.JellyfinTable, false)]
    [InlineData(true, ChapterSource.Embedded, true)]
    [InlineData(false, ChapterSource.Embedded, true)]
    public async Task HashAdoption_ReopensOnlyResultsTheSwitchCanChange(bool enabling, ChapterSource source, bool seasonOverrides)
    {
        var config = new PluginConfiguration { EnableCommunityFallback = enabling, UseEmbeddedChaptersForAnalysis = source == ChapterSource.Embedded };
        int? percent = seasonOverrides ? 40 : null;
        int? limit = seasonOverrides ? 12 : null;
        var previous = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, true, percent, limit, communityFallback: !enabling);
        var current = ConfigHasher.Analysis(config, AnalysisMode.Introduction, AnalyzerAction.Default, true, percent, limit);
        var database = _db.Database;
        var withLocal = Guid.NewGuid();
        var withCommunity = Guid.NewGuid();
        var empty = Guid.NewGuid();
        await database.ReplaceAutoSegmentsAsync(withLocal, AnalysisMode.Introduction, [new Segment(withLocal, new TimeRange(10, 60))], SegmentSource.Chapter, previous);
        await database.ReplaceAutoSegmentsAsync(withCommunity, AnalysisMode.Introduction, [new Segment(withCommunity, new TimeRange(0, 85))], SegmentSource.Community, previous);
        await database.MarkItemsAnalyzedAsync(AnalysisMode.Introduction, [(withLocal, null), (withCommunity, null), (empty, null)], previous);
        var seasonId = Guid.NewGuid();
        var snapshot = await database.GetSeasonQueueSnapshotAsync(seasonId, [withLocal, withCommunity, empty]);

        Assert.True(await CommunityHashAdoption.UpgradeAsync(database, snapshot, config, analysisPercentOverride: percent, analysisLengthLimitOverride: limit));

        var records = (await database.GetSeasonQueueSnapshotAsync(seasonId, [withLocal, withCommunity, empty])).AnalysisRecords;
        Assert.Equal(current, records[(withLocal, AnalysisMode.Introduction)].ConfigHash);
        Assert.Equal(enabling ? current : previous, records[(withCommunity, AnalysisMode.Introduction)].ConfigHash);
        Assert.Equal(previous, records[(empty, AnalysisMode.Introduction)].ConfigHash);
    }

    [Fact]
    public async Task AnalyzerPass_FillsOnlyAfterLocalAnalysisFindsNothing()
    {
        var config = Config();
        var (_, episode) = SetupSeries(config);
        episode.Chapters = [];
        var source = new FakeSource(Found(intro: new CommunityRange(0, 90.083)));
        var task = new BaseItemAnalyzerTask(NullLoggerFactory.Instance, null!, new StubFFmpegService(), null!, null!, _db.Database);

        await task.AnalyzeItemsAsync([episode], AnalysisMode.Introduction, AnalyzerAction.Default, false, CancellationToken.None, communityFallback: Fallback(source));

        Assert.Single(source.Asked);
        Assert.Equal(EpisodeState.Analyzed, episode.GetAnalyzed(AnalysisMode.Introduction));
        var segment = Assert.Single(await _db.Database.GetSegmentsAsync(episode.EpisodeId));
        Assert.Equal(SegmentSource.Community, segment.Source);
        var snapshot = await _db.Database.GetSeasonQueueSnapshotAsync(episode.SeasonId, [episode.EpisodeId]);
        Assert.Equal(segment.ConfigHash, snapshot.AnalysisRecords[(episode.EpisodeId, AnalysisMode.Introduction)].ConfigHash);
    }

    [Theory]
    [InlineData(7, SegmentSource.Community)]
    [InlineData(8, SegmentSource.Subtitle)]
    [InlineData(9, SegmentSource.KeyframeVisuals)]
    public async Task PersistedSources_RemainDistinct(int storedValue, SegmentSource expected)
    {
        var itemId = Guid.NewGuid();
        await _db.Database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(100, 120))], (SegmentSource)storedValue, "source-test");

        Assert.Equal(expected, Assert.Single(await _db.Database.GetSegmentsAsync(itemId)).Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreditsHashAdoption_PreservesSeasonPreviewOverride(bool enabling)
    {
        var config = new PluginConfiguration { EnableCommunityFallback = enabling, AnimePreviewFromCreditsEnd = true };
        var itemId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var previous = ConfigHasher.Analysis(config, AnalysisMode.Credits, AnalyzerAction.Default, true, previewFromCreditsEndOverride: false, communityFallback: !enabling);
        var current = ConfigHasher.Analysis(config, AnalysisMode.Credits, AnalyzerAction.Default, true, previewFromCreditsEndOverride: false);
        await _db.Database.ReplaceAutoSegmentsAsync(itemId, AnalysisMode.Credits, [new Segment(itemId, new TimeRange(100, 120))], SegmentSource.KeyframeVisuals, previous);
        await _db.Database.MarkItemsAnalyzedAsync(AnalysisMode.Credits, [(itemId, null)], previous);
        var snapshot = await _db.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId]);

        Assert.True(await CommunityHashAdoption.UpgradeAsync(_db.Database, snapshot, config, previewFromCreditsEndOverride: false));
        var updated = await _db.Database.GetSeasonQueueSnapshotAsync(seasonId, [itemId]);
        Assert.Equal(current, updated.AnalysisRecords[(itemId, AnalysisMode.Credits)].ConfigHash);
        var segment = Assert.Single(await _db.Database.GetSegmentsAsync(itemId));
        Assert.Equal(SegmentSource.KeyframeVisuals, segment.Source);
        Assert.Equal(current, segment.ConfigHash);
    }

    private static PluginConfiguration Config() => new()
    {
        EnableCommunityFallback = true,
        IntroEndOffset = 5,
        AdjustIntroBasedOnChapters = false,
        AdjustIntroBasedOnSilence = false,
        SnapToKeyframe = false,
        EndSnapThreshold = 0,
    };

    private static CommunityLookup Episode(string? imdb, string? tmdb, string? tvdb)
        => new(false, imdb, tmdb, tvdb, 1, 16, string.Empty, Duration);

    private static CommunityAnswer Found(CommunityRange? intro = null, CommunityRange? credits = null)
        => new(CommunityAnswerKind.Found, intro is { } i ? [i] : [], credits is { } c ? [c] : [], null);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static QueuedEpisode QueuedEpisode(Guid seriesId) => new()
    {
        EpisodeId = Guid.NewGuid(),
        SeriesId = seriesId,
        SeasonNumber = 1,
        EpisodeNumber = 16,
        Name = "Episode",
        Duration = Duration,
        Category = QueuedMediaCategory.AnimeEpisode,
    };

    private CommunityFallback Fallback(params ICommunitySource[] sources)
        => new(NullLogger<CommunityFallback>.Instance, sources, new StubFFmpegService(), _db.Database);

    private (Guid SeriesId, QueuedEpisode Episode) SetupSeries(PluginConfiguration config)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Series" };
        series.SetProviderId(MetadataProvider.Imdb, "tt33258199");
        _scope?.Dispose();
        _scope = EntrypointTestHelpers.CreatePluginScope(config);
        EntrypointTestHelpers.SetPrivateField(Plugin.Instance!, "_libraryManager", EntrypointTestHelpers.CreateLibraryManager(series));
        return (series.Id, QueuedEpisode(series.Id));
    }

    private sealed class FakeSource(CommunityAnswer answer, string name = "fake") : ICommunitySource
    {
        public List<CommunityLookup> Asked { get; } = [];

        public string Name => name;

        public bool IsEnabled(PluginConfiguration config) => true;

        public Task<CommunityAnswer> LookupAsync(CommunityLookup lookup, CancellationToken cancellationToken)
        {
            Asked.Add(lookup);
            return Task.FromResult(answer);
        }
    }
}
