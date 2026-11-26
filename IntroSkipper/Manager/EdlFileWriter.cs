// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using IntroSkipper.Configuration;
using IntroSkipper.Data;
using IntroSkipper.Filters;
using IntroSkipper.Providers;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Keeps a sidecar <c>&lt;media basename&gt;.edl</c> next to each media file equal to the
/// plugin's configured servable segments, so players that read EDL files (Infuse, Kodi) skip
/// without asking Jellyfin. <see cref="MediaSegmentMirror"/> calls <see cref="Sync"/> after it
/// converges an item. Each Outro (and optionally tail-reaching Preview) is capped at the
/// playback tail (<see cref="PluginConfiguration.EffectiveOutroTailBufferSeconds"/>) and season
/// premieres honor <see cref="PluginConfiguration.FirstEpisodeIntroMode"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: the writer overwrites an existing <c>.edl</c> whenever the item has exportable
/// segments, because that is the point of replacing another EDL generator, but it deletes a file
/// only when its own manifest (<c>edl-managed.json</c> in the plugin data folder) says it wrote
/// that file. A hand-made or foreign <c>.edl</c> is therefore never removed. Manifest entries
/// whose media file has disappeared are pruned on startup sync, on item removal, and during a
/// bulk sync pass.
/// </para>
/// <para>
/// Every I/O failure is logged as a warning and swallowed: an unwritable media folder must
/// never fail or roll back the database projection that calls this. Calls are synchronous
/// file operations serialized by one lock.
/// </para>
/// </remarks>
/// <param name="libraryManager">Library manager; supplies the media path, first-episode context and duration.</param>
/// <param name="logger">Logger.</param>
public sealed partial class EdlFileWriter(ILibraryManager libraryManager, ILogger<EdlFileWriter> logger)
{
    private const string ManifestFileName = "edl-managed.json";

    private readonly Lock _gate = new();
    private Dictionary<string, string>? _manifest;
    private bool _pruned;

    private enum SyncOutcome
    {
        Written,
        Unchanged,
        Removed,
        Failed,
    }

    /// <summary>
    /// Gets the manifest file path override for tests; null uses the plugin data folder.
    /// </summary>
    internal string? ManifestPathOverride { get; init; }

    private string ManifestPath => ManifestPathOverride ?? Path.Combine(Plugin.Instance!.DataFolderPath, ManifestFileName);

    /// <summary>
    /// Gets the number of sidecar <c>.edl</c> files currently tracked in the manifest.
    /// </summary>
    /// <returns>The count of managed files, or zero when the manifest is unreadable.</returns>
    public int GetManagedCount()
    {
        lock (_gate)
        {
            try
            {
                return LoadManifest().Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LogSyncFailed(logger, ManifestFileName, ex);
                return 0;
            }
        }
    }

    /// <summary>
    /// Brings one item's <c>.edl</c> in line with its servable segments when
    /// <see cref="PluginConfiguration.WriteEdlOnSegmentChange"/> is on.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="segments">The item's servable segments.</param>
    public void Sync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.WriteEdlOnSegmentChange)
        {
            return;
        }

        var item = libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        _ = SyncItemCore(item, segments, configuration, pruneStartupOrphans: true);
    }

    /// <summary>
    /// Removes the managed <c>.edl</c> file associated with a removed media path immediately,
    /// leaving any non-managed file untouched.
    /// </summary>
    /// <param name="mediaPath">Path of the removed media file.</param>
    public void RemoveForMediaPath(string? mediaPath)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            return;
        }

        var edlPath = Path.ChangeExtension(mediaPath, ".edl");
        lock (_gate)
        {
            try
            {
                var manifest = LoadManifest();
                if (manifest.Remove(edlPath))
                {
                    if (File.Exists(edlPath))
                    {
                        File.Delete(edlPath);
                    }

                    SaveManifest(manifest);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LogSyncFailed(logger, edlPath, ex);
            }
        }
    }

    /// <summary>
    /// Synchronizes sidecar <c>.edl</c> files across the entire non-excluded library when
    /// <see cref="PluginConfiguration.WriteEdlOnSegmentChange"/> is enabled, or removes all
    /// managed <c>.edl</c> files when it is disabled.
    /// </summary>
    /// <param name="seasonResolver">Resolves the non-excluded library items.</param>
    /// <param name="segmentDtoFactory">Reads each item's current servable segments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts of written, unchanged, removed and failed files.</returns>
    public async Task<EdlSyncResponse> SyncLibraryAsync(
        SeasonResolver seasonResolver,
        SegmentDtoFactory segmentDtoFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seasonResolver);
        ArgumentNullException.ThrowIfNull(segmentDtoFactory);

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || !configuration.WriteEdlOnSegmentChange)
        {
            return CleanAllManaged();
        }

        var written = 0;
        var unchanged = 0;
        var removed = 0;
        var failed = 0;
        var activeMediaPaths = new HashSet<string>(StringComparer.Ordinal);

        var episodes = seasonResolver.ResolveLibrary(includeExcluded: false, cancellationToken).Seasons
            .SelectMany(static season => season.Episodes)
            .DistinctBy(static episode => episode.EpisodeId);

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = libraryManager.GetItemById(episode.EpisodeId);
            if (item is null || string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            activeMediaPaths.Add(item.Path);
            var segments = await segmentDtoFactory.CreateAsync(episode.EpisodeId, cancellationToken).ConfigureAwait(false);
            switch (SyncItemCore(item, segments, configuration, pruneStartupOrphans: false))
            {
                case SyncOutcome.Written:
                    written++;
                    break;
                case SyncOutcome.Unchanged:
                    unchanged++;
                    break;
                case SyncOutcome.Removed:
                    removed++;
                    break;
                case SyncOutcome.Failed:
                    failed++;
                    break;
            }
        }

        var (orphanRemoved, orphanFailed) = PruneInactiveManaged(activeMediaPaths);
        return new EdlSyncResponse(written, unchanged, removed + orphanRemoved, failed + orphanFailed);
    }

    /// <summary>
    /// Formats segments as EDL lines: types filtered by configuration, first-episode Intro
    /// suppressed when requested, Outro (and optionally Preview) capped at the protected tail,
    /// sorted by start, exact duplicates and empty ranges dropped.
    /// </summary>
    /// <param name="segments">Servable segments.</param>
    /// <param name="durationTicks">Media duration; non-positive when unknown.</param>
    /// <param name="keepSeconds">Tail seconds to keep outside Outro; non-positive disables the cap.</param>
    /// <param name="configuration">Optional configuration controlling exported segment types and Preview tail capping; <see langword="null"/> exports Intro and Outro only.</param>
    /// <param name="suppressIntro">Whether Intro segments are suppressed for a season premiere.</param>
    /// <returns>The file content, empty when no range remains.</returns>
    internal static string BuildContent(
        IReadOnlyList<MediaSegmentDto> segments,
        long durationTicks,
        int keepSeconds,
        PluginConfiguration? configuration = null,
        bool suppressIntro = false)
    {
        var clipPreview = configuration?.ApplyOutroTailBufferToPreview ?? true;
        var ranges = new SortedSet<(long Start, long End)>();
        foreach (var segment in segments)
        {
            if (!IsExportedType(segment.Type, configuration, suppressIntro))
            {
                continue;
            }

            var clipped = PlaybackOutroTailFilter.ClipOutro(segment, durationTicks, keepSeconds, clipPreview);
            if (clipped is not null && clipped.StartTicks >= 0 && clipped.EndTicks > clipped.StartTicks)
            {
                ranges.Add((clipped.StartTicks, clipped.EndTicks));
            }
        }

        var builder = new StringBuilder();
        foreach (var (start, end) in ranges)
        {
            builder.Append(Seconds(start)).Append(' ').Append(Seconds(end)).Append(" 2\n");
        }

        return builder.ToString();

        static string Seconds(long ticks)
            => ((double)ticks / TimeSpan.TicksPerSecond).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool IsExportedType(MediaSegmentType type, PluginConfiguration? configuration, bool suppressIntro) => type switch
    {
        MediaSegmentType.Intro => !suppressIntro,
        MediaSegmentType.Outro => true,
        MediaSegmentType.Recap => configuration?.EdlIncludeRecap == true,
        MediaSegmentType.Preview => configuration?.EdlIncludePreview == true,
        MediaSegmentType.Commercial => configuration?.EdlIncludeCommercial == true,
        _ => false,
    };

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private SyncOutcome SyncItemCore(
        BaseItem item,
        IReadOnlyList<MediaSegmentDto> segments,
        PluginConfiguration configuration,
        bool pruneStartupOrphans)
    {
        var keepSeconds = configuration.ApplyOutroTailBufferToEdl ? configuration.EffectiveOutroTailBufferSeconds : 0;
        var suppressIntro = MediaSegmentsFirstEpisodeFilter.ShouldFilterIntro(libraryManager, item, configuration);
        var content = BuildContent(segments, item.RunTimeTicks ?? 0, keepSeconds, configuration, suppressIntro);
        var edlPath = Path.ChangeExtension(item.Path, ".edl");

        lock (_gate)
        {
            try
            {
                var manifest = LoadManifest();
                if (pruneStartupOrphans)
                {
                    PruneOnce(manifest);
                }

                if (content.Length == 0)
                {
                    if (manifest.Remove(edlPath))
                    {
                        var existed = File.Exists(edlPath);
                        if (existed)
                        {
                            File.Delete(edlPath);
                        }

                        SaveManifest(manifest);
                        return existed ? SyncOutcome.Removed : SyncOutcome.Unchanged;
                    }

                    return SyncOutcome.Unchanged;
                }

                var changed = !File.Exists(edlPath) || !Normalize(File.ReadAllText(edlPath)).Equals(Normalize(content), StringComparison.Ordinal);
                if (changed)
                {
                    WriteAtomically(edlPath, content);
                }

                if (!manifest.TryGetValue(edlPath, out var recorded) || recorded != item.Path)
                {
                    manifest[edlPath] = item.Path;
                    SaveManifest(manifest);
                }

                return changed ? SyncOutcome.Written : SyncOutcome.Unchanged;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LogSyncFailed(logger, edlPath, ex);
                return SyncOutcome.Failed;
            }
        }
    }

    private EdlSyncResponse CleanAllManaged()
    {
        var removed = 0;
        var failed = 0;
        lock (_gate)
        {
            Dictionary<string, string> manifest;
            try
            {
                manifest = LoadManifest();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LogSyncFailed(logger, ManifestFileName, ex);
                return new EdlSyncResponse(0, 0, 0, 1);
            }

            if (manifest.Count == 0)
            {
                return new EdlSyncResponse(0, 0, 0, 0);
            }

            foreach (var edlPath in manifest.Keys.ToList())
            {
                try
                {
                    if (File.Exists(edlPath))
                    {
                        File.Delete(edlPath);
                        removed++;
                    }

                    manifest.Remove(edlPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    LogSyncFailed(logger, edlPath, ex);
                    failed++;
                }
            }

            try
            {
                SaveManifest(manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LogSyncFailed(logger, ManifestFileName, ex);
                failed++;
            }
        }

        return new EdlSyncResponse(0, 0, removed, failed);
    }

    private (int Removed, int Failed) PruneInactiveManaged(HashSet<string> activeMediaPaths)
    {
        var removed = 0;
        var failed = 0;
        lock (_gate)
        {
            Dictionary<string, string> manifest;
            try
            {
                manifest = LoadManifest();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LogSyncFailed(logger, ManifestFileName, ex);
                return (0, 1);
            }

            _pruned = true;
            var stale = manifest
                .Where(entry => !activeMediaPaths.Contains(entry.Value) || !File.Exists(entry.Value))
                .Select(entry => entry.Key)
                .ToList();
            if (stale.Count == 0)
            {
                return (0, 0);
            }

            foreach (var edlPath in stale)
            {
                try
                {
                    if (File.Exists(edlPath))
                    {
                        File.Delete(edlPath);
                        removed++;
                    }

                    manifest.Remove(edlPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    LogSyncFailed(logger, edlPath, ex);
                    failed++;
                }
            }

            try
            {
                SaveManifest(manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LogSyncFailed(logger, ManifestFileName, ex);
                failed++;
            }
        }

        return (removed, failed);
    }

    private void PruneOnce(Dictionary<string, string> manifest)
    {
        if (_pruned)
        {
            return;
        }

        _pruned = true;
        var orphans = manifest.Where(entry => !File.Exists(entry.Value)).Select(entry => entry.Key).ToList();
        foreach (var edlPath in orphans)
        {
            if (File.Exists(edlPath))
            {
                File.Delete(edlPath);
            }

            manifest.Remove(edlPath);
        }

        if (orphans.Count > 0)
        {
            SaveManifest(manifest);
        }
    }

    private Dictionary<string, string> LoadManifest()
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        var path = ManifestPath;
        _manifest = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];
        return _manifest;
    }

    private void SaveManifest(Dictionary<string, string> manifest)
    {
        var path = ManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, JsonSerializer.Serialize(manifest));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not sync EDL file {EdlPath}; the segment projection is unaffected.")]
    private static partial void LogSyncFailed(ILogger logger, string edlPath, Exception exception);
}
