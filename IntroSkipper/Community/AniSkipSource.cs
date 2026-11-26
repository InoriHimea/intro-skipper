// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using IntroSkipper.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Community;

/// <summary>
/// AniSkip (aniskip.com): anonymous reads of <c>GET /v2/skip-times/{malId}/{episode}</c>, the
/// community database that covers anime best. Addressed by MyAnimeList id: a season-level or
/// series-level <c>MyAnimeList</c>/<c>Mal</c> id when present, otherwise mapped through the
/// read-only ARM v2 service from single-season anime ids (<c>AniList</c>, <c>AniDB</c>,
/// <c>Kitsu</c> via <c>/api/v2/ids</c>) or from multi-season general ids (<c>thetvdb</c>,
/// <c>themoviedb</c>, <c>imdb</c> via <c>/api/v2/{source}</c>) and cached in memory.
/// </summary>
/// <remarks>
/// ARM's <c>/api/v2/{thetvdb|themoviedb|imdb}</c> endpoints return every anime entry associated
/// with a series together with its <c>thetvdb-season</c> and <c>themoviedb-season</c>. A season
/// (including Season 2 and later) uses its mapped MAL id when all matching entries agree on a
/// single <c>myanimelist</c> id; a split-cour season that maps to multiple distinct MAL ids is
/// treated as an unambiguous miss rather than guessing episode boundaries.
/// </remarks>
/// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
/// <param name="logger">Logger.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed partial class AniSkipSource(IHttpClientFactory httpClientFactory, ILogger<AniSkipSource> logger, TimeProvider timeProvider)
    : CommunityHttpSource(httpClientFactory, logger, timeProvider)
{
    internal const string BaseUrl = "https://api.aniskip.com/v2/skip-times";
    internal const string ArmBaseUrl = "https://arm.haglund.dev/api/v2";
    internal const string MappingUrl = ArmBaseUrl + "/ids";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<AniSkipSource> _logger = logger;

    // 1-to-1 anime source (anilist, anidb, kitsu) -> MAL id; null remembers a definitive miss.
    private readonly ConcurrentDictionary<(string Source, string Id), int?> _singleMapped = new();

    // Multi-season source (thetvdb, themoviedb, imdb) -> parsed ARM entries; empty remembers a definitive miss.
    private readonly ConcurrentDictionary<(string Source, string Id), IReadOnlyList<ArmEntry>> _seriesMapped = new();

    /// <inheritdoc />
    public override string Name => "AniSkip";

    /// <inheritdoc />
    protected override TimeSpan RequestInterval => TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    public override bool IsEnabled(PluginConfiguration config) => config.CommunityUseAniSkip;

    /// <inheritdoc />
    public override async Task<CommunityAnswer> LookupAsync(CommunityLookup lookup, CancellationToken cancellationToken)
    {
        var episode = lookup.IsMovie ? 1 : lookup.Episode;
        if (episode is not >= 1
            || (!lookup.IsMovie && (lookup.Season is not >= 1 || !CommunityFallback.IsDefaultOrder(lookup.DisplayOrder))))
        {
            return CommunityAnswer.NotFound;
        }

        if ((PositiveMalId(lookup.SeasonMalId) ?? PositiveMalId(lookup.MalId)) is { } directMal)
        {
            return await GetAsync(BuildUrl(directMal, episode.Value), Parse, cancellationToken).ConfigureAwait(false);
        }

        var (mapped, unavailable) = await ResolveMalIdAsync(lookup, cancellationToken).ConfigureAwait(false);
        if (mapped is not { } mal)
        {
            return unavailable ? CommunityAnswer.Unavailable : CommunityAnswer.NotFound;
        }

        return await GetAsync(BuildUrl(mal, episode.Value), Parse, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the request URL for one episode.
    /// </summary>
    /// <param name="malId">MyAnimeList id.</param>
    /// <param name="episode">Episode number.</param>
    /// <returns>The URL.</returns>
    internal static string BuildUrl(int malId, int episode)
        => string.Create(CultureInfo.InvariantCulture, $"{BaseUrl}/{malId}/{episode}?types=op&types=ed&types=mixed-op&types=mixed-ed&episodeLength=0");

    /// <summary>
    /// Builds the ARM v2 multi-season lookup URL for a general metadata provider.
    /// </summary>
    /// <param name="source">ARM provider slug (<c>thetvdb</c>, <c>themoviedb</c>, or <c>imdb</c>).</param>
    /// <param name="id">Provider identifier.</param>
    /// <returns>The absolute ARM URL.</returns>
    internal static string BuildSeriesMappingUrl(string source, string id)
        => $"{ArmBaseUrl}/{source}?id={Uri.EscapeDataString(id)}";

    /// <summary>
    /// Parses a 200 body. Per kind one skip time is used, a plain <c>op</c>/<c>ed</c> in
    /// preference to the <c>mixed-</c> variants that overlap episode content, so a kind never
    /// produces two overlapping segments.
    /// </summary>
    /// <param name="root">Response root.</param>
    /// <returns>The answer.</returns>
    internal static CommunityAnswer Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return CommunityAnswer.NotFound;
        }

        var intro = Pick(results, "op", "mixed-op");
        var credits = Pick(results, "ed", "mixed-ed");
        if (intro is null && credits is null)
        {
            return CommunityAnswer.NotFound;
        }

        var reference = (intro?.Length ?? credits?.Length) is > 0 and var length ? length : (double?)null;
        return new CommunityAnswer(
            CommunityAnswerKind.Found,
            intro is { } i ? [i.Range] : [],
            credits is { } c ? [c.Range] : [],
            reference);
    }

    private static int? PositiveMalId(string? value)
        => int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mal) && mal > 0 ? mal : null;

    private static (CommunityRange Range, double? Length)? Pick(JsonElement results, params ReadOnlySpan<string> preferredKinds)
    {
        foreach (var kind in preferredKinds)
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object
                    || !result.TryGetProperty("skipType", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || type.GetString() != kind
                    || !result.TryGetProperty("interval", out var interval))
                {
                    continue;
                }

                var start = OptionalNumber(interval, "startTime");
                var end = OptionalNumber(interval, "endTime");
                if (start is >= 0 && end is { } e && e > start)
                {
                    return (new CommunityRange(start.Value, e), OptionalNumber(result, "episodeLength"));
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<ArmEntry> ParseArmEntries(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            return TryParseEntry(root) is { } single ? [single] : [];
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ArmEntry>();
        foreach (var item in root.EnumerateArray())
        {
            if (TryParseEntry(item) is { } entry)
            {
                list.Add(entry);
            }
        }

        return list;

        static ArmEntry? TryParseEntry(JsonElement element)
        {
            if (OptionalNumber(element, "myanimelist") is not (> 0 and var malValue))
            {
                return null;
            }

            var tvdbSeason = OptionalNumber(element, "thetvdb-season") is { } tvdb ? (int)tvdb : (int?)null;
            var tmdbSeason = OptionalNumber(element, "themoviedb-season") is { } tmdb ? (int)tmdb : (int?)null;
            var media = element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("media", out var mediaProp)
                && mediaProp.ValueKind == JsonValueKind.String
                ? mediaProp.GetString()
                : null;
            return new ArmEntry((int)malValue, tvdbSeason, tmdbSeason, media);
        }
    }

    private static int? SelectMalId(IReadOnlyList<ArmEntry> entries, string source, CommunityLookup lookup)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        int? matched = null;
        foreach (var entry in entries)
        {
            if (!MatchesLookup(entry, source, lookup))
            {
                continue;
            }

            if (matched is null)
            {
                matched = entry.MalId;
            }
            else if (matched.Value != entry.MalId)
            {
                // Split-cour seasons carry multiple distinct MAL ids under one TVDB/TMDB season;
                // episode boundaries are unknown, so refuse ambiguous mappings.
                return null;
            }
        }

        return matched;
    }

    private static bool MatchesLookup(ArmEntry entry, string source, CommunityLookup lookup)
    {
        var isMovieMedia = string.Equals(entry.Media, "MOVIE", StringComparison.OrdinalIgnoreCase);
        if (lookup.IsMovie)
        {
            return isMovieMedia || (entry.Media is null && entry.TvdbSeason is null && entry.TmdbSeason is null);
        }

        if (isMovieMedia || lookup.Season is not { } season)
        {
            return false;
        }

        return source switch
        {
            "thetvdb" => entry.TvdbSeason == season || (entry.TvdbSeason is null && entry.TmdbSeason is null && season == 1),
            "themoviedb" => entry.TmdbSeason == season || (entry.TmdbSeason is null && entry.TvdbSeason is null && season == 1),
            _ => ((entry.TvdbSeason == season || entry.TmdbSeason == season)
                    && (entry.TvdbSeason is null || entry.TvdbSeason == season)
                    && (entry.TmdbSeason is null || entry.TmdbSeason == season))
                || (entry.TvdbSeason is null && entry.TmdbSeason is null && season == 1),
        };
    }

    private async Task<(int? MalId, bool Unavailable)> ResolveMalIdAsync(CommunityLookup lookup, CancellationToken cancellationToken)
    {
        var unavailable = false;

        foreach (var (source, id) in new[] { ("anilist", lookup.AniListId), ("anidb", lookup.AniDbId), ("kitsu", lookup.KitsuId) })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var (singleMal, singleUnavailable) = await ResolveSingleIdAsync(source, id.Trim(), cancellationToken).ConfigureAwait(false);
            if (singleMal is not null)
            {
                return (singleMal, false);
            }

            unavailable |= singleUnavailable;
        }

        foreach (var (source, id) in new[] { ("thetvdb", lookup.TvdbId), ("themoviedb", lookup.TmdbId), ("imdb", lookup.ImdbId) })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var (entries, seriesUnavailable) = await ResolveSeriesEntriesAsync(source, id.Trim(), cancellationToken).ConfigureAwait(false);
            unavailable |= seriesUnavailable;
            if (SelectMalId(entries, source, lookup) is { } selectedMal)
            {
                return (selectedMal, false);
            }
        }

        return (null, unavailable);
    }

    private async Task<(int? MalId, bool Unavailable)> ResolveSingleIdAsync(string source, string id, CancellationToken cancellationToken)
    {
        var key = (source, id);
        if (_singleMapped.TryGetValue(key, out var known))
        {
            return (known, false);
        }

        try
        {
            var url = $"{MappingUrl}?source={source}&id={Uri.EscapeDataString(id)}";
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                _singleMapped[key] = null;
                return (null, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, true);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                var mal = OptionalNumber(document.RootElement, "myanimelist") is > 0 and var value ? (int)value : (int?)null;
                _singleMapped[key] = mal;
                return (mal, false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogMappingFailed(_logger, ex.Message);
            return (null, true);
        }
    }

    private async Task<(IReadOnlyList<ArmEntry> Entries, bool Unavailable)> ResolveSeriesEntriesAsync(string source, string id, CancellationToken cancellationToken)
    {
        var key = (source, id);
        if (_seriesMapped.TryGetValue(key, out var cached))
        {
            return (cached, false);
        }

        try
        {
            var url = BuildSeriesMappingUrl(source, id);
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                _seriesMapped[key] = [];
                return ([], false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ([], true);
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                var entries = ParseArmEntries(document.RootElement);
                _seriesMapped[key] = entries;
                return (entries, false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ([], true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogMappingFailed(_logger, ex.Message);
            return ([], true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AniSkip id mapping unavailable ({Reason}); the item stays eligible for the next scan")]
    private static partial void LogMappingFailed(ILogger logger, string reason);

    private readonly record struct ArmEntry(int MalId, int? TvdbSeason, int? TmdbSeason, string? Media);
}
