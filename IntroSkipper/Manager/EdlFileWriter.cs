// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using IntroSkipper.Filters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Manager;

/// <summary>
/// Keeps a sidecar <c>&lt;media basename&gt;.edl</c> next to each media file equal to the
/// plugin's servable segments, so players that read EDL files (Infuse, Kodi) skip without
/// asking Jellyfin. <see cref="MediaSegmentMirror"/> calls <see cref="Sync"/> after it
/// converges an item. Each Outro is capped at the playback tail
/// (<see cref="Configuration.PluginConfiguration.EffectiveOutroTailBufferSeconds"/>) exactly as
/// <see cref="PlaybackOutroTailFilter"/> caps the HTTP response.
/// </summary>
/// <remarks>
/// <para>
/// Ownership: the writer overwrites an existing <c>.edl</c> whenever the item has segments,
/// because that is the point of replacing another EDL generator, but it deletes a file only
/// when its own manifest (<c>edl-managed.json</c> in the plugin data folder) says it wrote
/// that file. A hand-made or foreign <c>.edl</c> is therefore never removed. Manifest
/// entries whose media file has disappeared are pruned, with their <c>.edl</c>, on the first
/// sync after startup.
/// </para>
/// <para>
/// Every I/O failure is logged as a warning and swallowed: an unwritable media folder must
/// never fail or roll back the database projection that calls this. Calls are synchronous
/// file operations serialized by one lock.
/// </para>
/// </remarks>
/// <param name="libraryManager">Library manager; supplies the media path and duration.</param>
/// <param name="logger">Logger.</param>
internal sealed partial class EdlFileWriter(ILibraryManager libraryManager, ILogger<EdlFileWriter> logger)
{
    private const string ManifestFileName = "edl-managed.json";

    private readonly Lock _gate = new();
    private Dictionary<string, string>? _manifest;
    private bool _pruned;

    /// <summary>
    /// Gets the manifest file path override for tests; null uses the plugin data folder.
    /// </summary>
    internal string? ManifestPathOverride { get; init; }

    private string ManifestPath => ManifestPathOverride ?? Path.Combine(Plugin.Instance!.DataFolderPath, ManifestFileName);

    /// <summary>
    /// Brings one item's <c>.edl</c> in line with its servable segments when
    /// <see cref="Configuration.PluginConfiguration.WriteEdlOnSegmentChange"/> is on.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="segments">The item's servable segments.</param>
    public void Sync(Guid itemId, IReadOnlyList<MediaSegmentDto> segments)
    {
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

        var keepSeconds = configuration.ApplyOutroTailBufferToEdl ? configuration.EffectiveOutroTailBufferSeconds : 0;
        var content = BuildContent(segments, item.RunTimeTicks ?? 0, keepSeconds);
        var edlPath = Path.ChangeExtension(item.Path, ".edl");

        lock (_gate)
        {
            try
            {
                var manifest = LoadManifest();
                PruneOnce(manifest);
                if (content.Length == 0)
                {
                    if (manifest.Remove(edlPath))
                    {
                        File.Delete(edlPath);
                        SaveManifest(manifest);
                    }

                    return;
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
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                LogSyncFailed(logger, edlPath, ex);
            }
        }
    }

    /// <summary>
    /// Formats segments as EDL lines: Outro capped at the protected tail, sorted by start,
    /// exact duplicates and empty ranges dropped.
    /// </summary>
    /// <param name="segments">Servable segments.</param>
    /// <param name="durationTicks">Media duration; non-positive when unknown.</param>
    /// <param name="keepSeconds">Tail seconds to keep outside Outro; non-positive disables the cap.</param>
    /// <returns>The file content, empty when no range remains.</returns>
    internal static string BuildContent(IReadOnlyList<MediaSegmentDto> segments, long durationTicks, int keepSeconds)
    {
        var ranges = new SortedSet<(long Start, long End)>();
        foreach (var segment in segments)
        {
            var clipped = PlaybackOutroTailFilter.ClipOutro(segment, durationTicks, keepSeconds);
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

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
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
            File.Delete(edlPath);
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
