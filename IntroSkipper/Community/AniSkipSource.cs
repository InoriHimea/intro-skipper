// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using IntroSkipper.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Community;

/// <summary>
/// AniSkip (aniskip.com): anonymous reads of <c>GET /v2/skip-times/{malId}/{episode}</c>, the
/// community database that covers anime best. It is addressed by MyAnimeList id, which a series
/// carries only when a metadata plugin set it; otherwise the id is mapped from the series'
/// TVDB, TMDB or IMDb id through the read-only ARM mapping service and remembered.
/// </summary>
/// <remarks>
/// A MAL id names one cour or season while the library numbers episodes per Jellyfin season,
/// so an id mapped from a general-purpose id is trusted only for season 1 (and movies). An id
/// the user set on the series is used for any real season, as they chose it for that series.
/// The server-side release filter is not used: the lookup asks for every release and reports
/// the matching release's length, so <see cref="CommunityFallback.ToSegments"/> applies the
/// configured mismatch tolerance like it does for every source.
/// </remarks>
/// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
/// <param name="logger">Logger.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed partial class AniSkipSource(IHttpClientFactory httpClientFactory, ILogger<AniSkipSource> logger, TimeProvider timeProvider)
    : CommunityHttpSource(httpClientFactory, logger, timeProvider)
{
    internal const string BaseUrl = "https://api.aniskip.com/v2/skip-times";
    internal const string MappingUrl = "https://arm.haglund.dev/api/v2/ids";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<AniSkipSource> _logger = logger;

    // (ARM source, id) to MAL id; null remembers a definitive "unknown". Transient failures are
    // never stored, so the next item asks again.
    private readonly ConcurrentDictionary<(string Source, string Id), int?> _mapped = new();

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

        if (int.TryParse(lookup.MalId, NumberStyles.None, CultureInfo.InvariantCulture, out var malId) && malId > 0)
        {
            return await GetAsync(BuildUrl(malId, episode.Value), Parse, cancellationToken).ConfigureAwait(false);
        }

        if (!lookup.IsMovie && lookup.Season != 1)
        {
            return CommunityAnswer.NotFound;
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

    private static (CommunityRange Range, double? Length)? Pick(JsonElement results, params string[] preferredKinds)
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

    private async Task<(int? MalId, bool Unavailable)> ResolveMalIdAsync(CommunityLookup lookup, CancellationToken cancellationToken)
    {
        var unavailable = false;
        foreach (var (source, id) in new[] { ("thetvdb", lookup.TvdbId), ("themoviedb", lookup.TmdbId), ("imdb", lookup.ImdbId) })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var key = (source, id.Trim());
            if (_mapped.TryGetValue(key, out var known))
            {
                if (known is not null)
                {
                    return (known, false);
                }

                continue;
            }

            try
            {
                var url = $"{MappingUrl}?source={source}&id={Uri.EscapeDataString(key.Item2)}";
                using var client = _httpClientFactory.CreateClient(NamedClient.Default);
                client.Timeout = TimeSpan.FromSeconds(10);
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _mapped[key] = null;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    unavailable = true;
                    continue;
                }

                var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (body.ConfigureAwait(false))
                {
                    using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var mal = OptionalNumber(document.RootElement, "myanimelist") is { } value and > 0 ? (int)value : (int?)null;
                    _mapped[key] = mal;
                    if (mal is not null)
                    {
                        return (mal, false);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                unavailable = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                LogMappingFailed(_logger, ex.Message);
                unavailable = true;
            }
        }

        return (null, unavailable);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AniSkip id mapping unavailable ({Reason}); the item stays eligible for the next scan")]
    private static partial void LogMappingFailed(ILogger logger, string reason);
}
