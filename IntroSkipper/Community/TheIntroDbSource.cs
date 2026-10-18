// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using IntroSkipper.Configuration;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Community;

/// <summary>
/// TheIntroDB (theintrodb.org): anonymous reads of <c>GET /v4/media</c>. An id the service has
/// not mapped answers 404 even when another id of the same title resolves, so the ids are tried
/// in the service's own priority order (TVDB, TMDB, IMDb) until one is known.
/// </summary>
/// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
/// <param name="logger">Logger.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class TheIntroDbSource(IHttpClientFactory httpClientFactory, ILogger<TheIntroDbSource> logger, TimeProvider timeProvider)
    : CommunityHttpSource(httpClientFactory, logger, timeProvider)
{
    internal const string BaseUrl = "https://api.theintrodb.org/v4/media";

    /// <inheritdoc />
    public override string Name => "TheIntroDB";

    /// <inheritdoc />
    /// <remarks>Anonymous window: 30 requests per 10 seconds.</remarks>
    protected override TimeSpan RequestInterval => TimeSpan.FromMilliseconds(400);

    /// <inheritdoc />
    public override bool IsEnabled(PluginConfiguration config) => config.CommunityUseTheIntroDb;

    /// <inheritdoc />
    public override async Task<CommunityAnswer> LookupAsync(CommunityLookup lookup, CancellationToken cancellationToken)
    {
        foreach (var url in BuildUrls(lookup))
        {
            var answer = await GetAsync(url, Parse, cancellationToken).ConfigureAwait(false);
            if (answer.Kind != CommunityAnswerKind.NotFound)
            {
                return answer;
            }
        }

        return CommunityAnswer.NotFound;
    }

    /// <summary>
    /// Builds one request per known id, in TVDB, TMDB, IMDb order. Episodes carry the local
    /// numbering and, for a non-default series order, that order as <c>mapping</c> so the
    /// service converts the coordinates. Every request carries the local duration so the
    /// service picks the matching release, and asks for <c>details</c> to learn that release's duration.
    /// </summary>
    /// <param name="lookup">Item identity.</param>
    /// <returns>The candidate URLs.</returns>
    internal static IReadOnlyList<string> BuildUrls(CommunityLookup lookup)
    {
        if (!lookup.IsMovie && (lookup.Season is not >= 0 || lookup.Episode is not >= 0))
        {
            return [];
        }

        var suffix = new StringBuilder();
        if (!lookup.IsMovie)
        {
            suffix.Append(CultureInfo.InvariantCulture, $"&season={lookup.Season}&episode={lookup.Episode}");
            if (!CommunityFallback.IsDefaultOrder(lookup.DisplayOrder))
            {
                suffix.Append("&mapping=").Append(Uri.EscapeDataString(lookup.DisplayOrder));
            }
        }

        if (lookup.Duration > 0)
        {
            suffix.Append(CultureInfo.InvariantCulture, $"&duration_ms={(long)Math.Round(lookup.Duration * 1000)}");
        }

        suffix.Append("&details=true");

        var urls = new List<string>(3);
        if (PositiveInteger(lookup.TvdbId) is { } tvdb)
        {
            urls.Add(string.Create(CultureInfo.InvariantCulture, $"{BaseUrl}?tvdb_id={tvdb}{suffix}"));
        }

        if (PositiveInteger(lookup.TmdbId) is { } tmdb)
        {
            urls.Add(string.Create(CultureInfo.InvariantCulture, $"{BaseUrl}?tmdb_id={tmdb}{suffix}"));
        }

        if (!string.IsNullOrWhiteSpace(lookup.ImdbId))
        {
            urls.Add($"{BaseUrl}?imdb_id={Uri.EscapeDataString(lookup.ImdbId.Trim())}{suffix}");
        }

        return urls;
    }

    /// <summary>
    /// Parses a 200 body: <c>intro</c> and <c>credits</c> arrays of millisecond ranges. An intro
    /// start of null means the start of the media and an end of 0 means "no intro"; a credits end
    /// of null means the end of the media and a 0 range means "no credits".
    /// </summary>
    /// <param name="root">Response root.</param>
    /// <returns>The answer.</returns>
    internal static CommunityAnswer Parse(JsonElement root)
    {
        // The episode-list fallback shape carries no timestamps.
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out _))
        {
            return CommunityAnswer.NotFound;
        }

        List<CommunityRange> intro = [];
        foreach (var item in Items(root, "intro"))
        {
            var start = OptionalNumber(item, "start_ms") ?? 0;
            var end = OptionalNumber(item, "end_ms");
            if (end is > 0 && end > start)
            {
                intro.Add(new CommunityRange(start / 1000, end / 1000));
            }
        }

        List<CommunityRange> credits = [];
        foreach (var item in Items(root, "credits"))
        {
            var start = OptionalNumber(item, "start_ms");
            var end = OptionalNumber(item, "end_ms");
            if (start is > 0 && (end is null || end > start))
            {
                credits.Add(new CommunityRange(start.Value / 1000, end / 1000));
            }
        }

        var groupDuration = OptionalNumber(root, "group_duration_ms");
        return intro.Count == 0 && credits.Count == 0
            ? CommunityAnswer.NotFound
            : new CommunityAnswer(CommunityAnswerKind.Found, intro, credits, groupDuration is > 0 ? groupDuration / 1000 : null);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string name)
        => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    private static long? PositiveInteger(string? value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
}
