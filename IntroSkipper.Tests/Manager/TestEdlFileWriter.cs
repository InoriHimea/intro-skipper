// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.IO;
using IntroSkipper.Configuration;
using IntroSkipper.Manager;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntroSkipper.Tests;

/// <summary>
/// The sidecar <c>.edl</c> follows the servable segments with the playback tail cap, and the
/// writer only ever deletes files its own manifest says it wrote.
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
