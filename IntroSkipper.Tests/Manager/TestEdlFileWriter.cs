// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Manager;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// The sidecar <c>.edl</c> follows the configured servable segments with the playback tail cap
/// and first-episode policy, and the writer only ever deletes files its own manifest says it wrote.
/// </summary>
public sealed class TestEdlFileWriter : IDisposable
{
    private const long Second = TimeSpan.TicksPerSecond;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edl-" + Guid.NewGuid().ToString("N"));

    public TestEdlFileWriter() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ContentSortsDedupesAndCapsOutroAtTheTail()
    {
        var id = Guid.NewGuid();
        var content = EdlFileWriter.BuildContent(
            [
                Seg(id, MediaSegmentType.Outro, 1300 * Second, 1420 * Second),
                Seg(id, MediaSegmentType.Intro, 90 * Second + (Second / 2), 120 * Second),
                Seg(id, MediaSegmentType.Intro, 90 * Second + (Second / 2), 120 * Second),
                Seg(id, MediaSegmentType.Outro, 1419 * Second, 1420 * Second), // consumed by the tail
            ],
            1420 * Second,
            5);

        Assert.Equal("90.5 120 2\n1300 1415 2\n", content);
    }

    [Fact]
    public void ContentFiltersSegmentTypesAndCapsOptedInPreview()
    {
        var id = Guid.NewGuid();
        var allTypes = new[]
        {
            Seg(id, MediaSegmentType.Recap, 0, 30 * Second),
            Seg(id, MediaSegmentType.Intro, 60 * Second, 150 * Second),
            Seg(id, MediaSegmentType.Commercial, 600 * Second, 630 * Second),
            Seg(id, MediaSegmentType.Outro, 1260 * Second, 1350 * Second),
            Seg(id, MediaSegmentType.Preview, 1350 * Second, 1420 * Second),
        };

        // By default only Intro and Outro are exported, so a post-credits scene marked Preview is not cut.
        Assert.Equal(
            "60 150 2\n1260 1350 2\n",
            EdlFileWriter.BuildContent(allTypes, 1420 * Second, 5, new PluginConfiguration()));

        // Opting into Preview also caps a tail-reaching Preview at the 5 s tail buffer.
        var withPreview = new PluginConfiguration { EdlIncludePreview = true, ApplyOutroTailBufferToPreview = true };
        Assert.Equal(
            "60 150 2\n1260 1350 2\n1350 1415 2\n",
            EdlFileWriter.BuildContent(allTypes, 1420 * Second, 5, withPreview));

        var allEnabled = new PluginConfiguration
        {
            EdlIncludeRecap = true,
            EdlIncludePreview = true,
            EdlIncludeCommercial = true,
            ApplyOutroTailBufferToPreview = false,
        };
        Assert.Equal(
            "0 30 2\n60 150 2\n600 630 2\n1260 1350 2\n1350 1420 2\n",
            EdlFileWriter.BuildContent(allTypes, 1420 * Second, 5, allEnabled));
    }

    [Fact]
    public void SyncHonorsFirstEpisodeIntroSuppressionAndImmediateMediaRemoval()
    {
        using var scope = Scope(new PluginConfiguration
        {
            WriteEdlOnSegmentChange = true,
            FirstEpisodeIntroMode = PluginConfiguration.FirstEpisodeIntroModeIgnore,
            OutroTailBufferSeconds = 5,
        });
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var series = JellyfinItems.Series(seriesId);
        var season = JellyfinItems.Season(seasonId, seriesId, 1);
        var ep1 = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId, seasonNumber: 1, episodeNumber: 1);
        ep1.ParentId = seasonId;
        ep1.Path = Path.Combine(_dir, "s01e01.mkv");
        ep1.RunTimeTicks = 1420 * Second;
        File.WriteAllText(ep1.Path, string.Empty);

        var library = EntrypointTestHelpers.FakeLibraryManager.Create([], [series, season, ep1]);
        var writer = new EdlFileWriter(library, NullLogger<EdlFileWriter>.Instance) { ManifestPathOverride = Path.Combine(_dir, "manifest.json") };

        writer.Sync(ep1.Id, [Seg(ep1.Id, MediaSegmentType.Intro, 10 * Second, 90 * Second), Seg(ep1.Id, MediaSegmentType.Outro, 1330 * Second, 1420 * Second)]);
        var edl = Path.ChangeExtension(ep1.Path, ".edl");
        Assert.Equal("1330 1415 2\n", File.ReadAllText(edl));
        Assert.Equal(1, writer.GetManagedCount());

        // Removing the media notifies RemoveForMediaPath and deletes the managed .edl immediately.
        writer.RemoveForMediaPath(ep1.Path);
        Assert.False(File.Exists(edl));
        Assert.Equal(0, writer.GetManagedCount());
    }

    [Fact]
    public async Task SyncLibraryAsyncRewritesAllItemsAndCleansWhenDisabled()
    {
        using var db = new TempSegmentDb();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var series = JellyfinItems.Series(seriesId);
        var season = JellyfinItems.Season(seasonId, seriesId, 1);
        var ep = JellyfinItems.Episode(Guid.NewGuid(), seriesId, seasonId, seasonNumber: 1, episodeNumber: 2);
        ep.ParentId = seasonId;
        ep.Path = Path.Combine(_dir, "s01e02.mkv");
        ep.RunTimeTicks = 1420 * Second;
        await File.WriteAllTextAsync(ep.Path, string.Empty);

        await db.Database.ReplaceAutoSegmentsAsync(ep.Id, AnalysisMode.Credits, [new Segment(ep.Id, new TimeRange(1330, 1420))], SegmentSource.Chapter);

        var config = new PluginConfiguration { WriteEdlOnSegmentChange = true, OutroTailBufferSeconds = 5 };
        using var scope = Scope(config);
        var library = EntrypointTestHelpers.FakeLibraryManager.Create([JellyfinItems.Folder("Media")], [series, season, ep]);
        var resolver = EntrypointTestHelpers.CreateSeasonResolver(library);
        var dtoFactory = new SegmentDtoFactory(db.Database);
        var writer = new EdlFileWriter(library, NullLogger<EdlFileWriter>.Instance) { ManifestPathOverride = Path.Combine(_dir, "sync-all.json") };
        var edl = Path.ChangeExtension(ep.Path, ".edl");

        var first = await writer.SyncLibraryAsync(resolver, dtoFactory, CancellationToken.None);
        Assert.Equal(new EdlSyncResponse(1, 0, 0, 0), first);
        Assert.Equal("1330 1415 2\n", await File.ReadAllTextAsync(edl));

        // Changing OutroTailBufferSeconds without re-analysis and running SyncLibraryAsync updates the .edl.
        config.OutroTailBufferSeconds = 8;
        var updated = await writer.SyncLibraryAsync(resolver, dtoFactory, CancellationToken.None);
        Assert.Equal(new EdlSyncResponse(1, 0, 0, 0), updated);
        Assert.Equal("1330 1412 2\n", await File.ReadAllTextAsync(edl));

        // Disabling WriteEdlOnSegmentChange and running SyncLibraryAsync removes all managed .edl files.
        config.WriteEdlOnSegmentChange = false;
        var cleaned = await writer.SyncLibraryAsync(resolver, dtoFactory, CancellationToken.None);
        Assert.Equal(new EdlSyncResponse(0, 0, 1, 0), cleaned);
        Assert.False(File.Exists(edl));
        Assert.Equal(0, writer.GetManagedCount());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NoTailKeepsTheOutroEnd(int keep)
        => Assert.Equal("1300 1420 2\n", EdlFileWriter.BuildContent([Seg(Guid.NewGuid(), MediaSegmentType.Outro, 1300 * Second, 1420 * Second)], 1420 * Second, keep));

    [Fact]
    public void EmptyInputProducesNoContent()
        => Assert.Equal(string.Empty, EdlFileWriter.BuildContent([], 100 * Second, 5));

    [Fact]
    public void SyncWritesOnceAndLeavesAnUnchangedFileAlone()
    {
        using var scope = Scope(new PluginConfiguration { WriteEdlOnSegmentChange = true, OutroTailBufferSeconds = 5 });
        var (writer, id, media) = Create(scope);
        var segments = new[] { Seg(id, MediaSegmentType.Outro, 1300 * Second, 1420 * Second) };

        writer.Sync(id, segments);
        var edl = Path.ChangeExtension(media, ".edl");
        Assert.Equal("1300 1415 2\n", File.ReadAllText(edl));

        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(edl, stamp);
        writer.Sync(id, segments);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(edl));
    }

    [Fact]
    public void SyncOverwritesAForeignEdlButOnlyDeletesWhatItWrote()
    {
        using var scope = Scope(new PluginConfiguration { WriteEdlOnSegmentChange = true, OutroTailBufferSeconds = 5 });
        var (writer, id, media) = Create(scope);
        var edl = Path.ChangeExtension(media, ".edl");

        // A foreign file with nothing to say is left alone.
        File.WriteAllText(edl, "1 2 3\n");
        writer.Sync(id, []);
        Assert.Equal("1 2 3\n", File.ReadAllText(edl));

        // With segments it is replaced, and from then on it is ours.
        writer.Sync(id, [Seg(id, MediaSegmentType.Intro, 10 * Second, 20 * Second)]);
        Assert.Equal("10 20 2\n", File.ReadAllText(edl));

        // Deleting the last segment removes the file we wrote.
        writer.Sync(id, []);
        Assert.False(File.Exists(edl));
    }

    [Fact]
    public void SyncPrunesManagedFilesWhoseMediaIsGone()
    {
        using var scope = Scope(new PluginConfiguration { WriteEdlOnSegmentChange = true });
        var (writer, id, media) = Create(scope);
        writer.Sync(id, [Seg(id, MediaSegmentType.Intro, 10 * Second, 20 * Second)]);
        var edl = Path.ChangeExtension(media, ".edl");
        Assert.True(File.Exists(edl));
        File.Delete(media);

        // A fresh writer is a restart: it prunes on its first sync, whatever item that is.
        var (restarted, otherId, _) = Create(scope, "other.mkv", writer.ManifestPathOverride);
        restarted.Sync(otherId, []);

        Assert.False(File.Exists(edl));
    }

    [Fact]
    public void DisabledSettingAndUnwritableFolderNeverThrow()
    {
        using (var scope = Scope(new PluginConfiguration { WriteEdlOnSegmentChange = false }))
        {
            var (writer, id, media) = Create(scope);
            writer.Sync(id, [Seg(id, MediaSegmentType.Intro, 10 * Second, 20 * Second)]);
            Assert.False(File.Exists(Path.ChangeExtension(media, ".edl")));
        }

        using (var scope = Scope(new PluginConfiguration { WriteEdlOnSegmentChange = true }))
        {
            var id = Guid.NewGuid();
            var missing = Path.Combine(_dir, "gone", "ep.mkv");
            var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, Path = missing, RunTimeTicks = 100 * Second });
            var writer = new EdlFileWriter(library, NullLogger<EdlFileWriter>.Instance) { ManifestPathOverride = Path.Combine(_dir, "m.json") };

            writer.Sync(id, [Seg(id, MediaSegmentType.Intro, 10 * Second, 20 * Second)]);

            Assert.False(File.Exists(Path.ChangeExtension(missing, ".edl")));
        }
    }

    private static IDisposable Scope(PluginConfiguration configuration) => EntrypointTestHelpers.CreatePluginScope(configuration);

    private (EdlFileWriter Writer, Guid Id, string Media) Create(IDisposable scope, string name = "ep.mkv", string? manifest = null)
    {
        _ = scope;
        var id = Guid.NewGuid();
        var media = Path.Combine(_dir, name);
        File.WriteAllText(media, string.Empty);
        var library = EntrypointTestHelpers.CreateLibraryManager(new Episode { Id = id, Path = media, RunTimeTicks = 1420 * Second });
        return (new EdlFileWriter(library, NullLogger<EdlFileWriter>.Instance) { ManifestPathOverride = manifest ?? Path.Combine(_dir, "manifest.json") }, id, media);
    }

    private static MediaSegmentDto Seg(Guid itemId, MediaSegmentType type, long start, long end)
        => new() { Id = Guid.NewGuid(), ItemId = itemId, Type = type, StartTicks = start, EndTicks = end };
}
