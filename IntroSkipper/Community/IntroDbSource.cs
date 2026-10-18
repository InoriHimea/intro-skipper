// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json;
using IntroSkipper.Configuration;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Community;

/// <summary>
/// IntroDB (introdb.app): anonymous reads of <c>GET /segments</c>, addressed by IMDb id plus
/// season and episode in the aired order. Unknown items answer 200 with every segment null.
/// </summary>
/// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
/// <param name="logger">Logger.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class IntroDbSource(IHttpClientFactory httpClientFactory, ILogger<IntroDbSource> logger, TimeProvider timeProvider)
    : CommunityHttpSource(httpClientFactory, logger, timeProvider)
{
    internal const string BaseUrl = "https://api.introdb.app/segments";

    /// <inheritdoc />
    public override string Name => "IntroDB";

    /// <inheritdoc />
    protected override TimeSpan RequestInterval => TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public override bool IsEnabled(PluginConfiguration config) => config.CommunityUseIntroDb;

    /// <inheritdoc />
    public override Task<CommunityAnswer> LookupAsync(CommunityLookup lookup, CancellationToken cancellationToken)
    {
        var url = BuildUrl(lookup);
        return url is null ? Task.FromResult(CommunityAnswer.NotFound) : GetAsync(url, Parse, cancellationToken);
    }

    /// <summary>
    /// Builds the request URL, or returns <see langword="null"/> when the item cannot be
    /// addressed: no IMDb id, a special (IntroDB numbers seasons from 1), or a series in a
    /// non-aired order whose numbers IntroDB cannot convert.
    /// </summary>
    /// <param name="lookup">Item identity.</param>
    /// <returns>The URL or <see langword="null"/>.</returns>
    internal static string? BuildUrl(CommunityLookup lookup)
    {
        if (string.IsNullOrWhiteSpace(lookup.ImdbId))
        {
            return null;
        }

        var imdb = Uri.EscapeDataString(lookup.ImdbId.Trim());
        if (lookup.IsMovie)
        {
            return $"{BaseUrl}?imdb_id={imdb}&is_movie=true";
        }

        if (lookup.Season is not >= 1 || lookup.Episode is not >= 1 || !CommunityFallback.IsDefaultOrder(lookup.DisplayOrder))
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{BaseUrl}?imdb_id={imdb}&season={lookup.Season}&episode={lookup.Episode}");
    }

    /// <summary>
    /// Parses a 200 body: <c>intro</c> and <c>outro</c> objects with <c>start_sec</c>/<c>end_sec</c>, or null.
    /// </summary>
    /// <param name="root">Response root.</param>
    /// <returns>The answer.</returns>
    internal static CommunityAnswer Parse(JsonElement root)
    {
        var intro = Range(root, "intro");
        var outro = Range(root, "outro");
        if (intro is null && outro is null)
        {
            return CommunityAnswer.NotFound;
        }

        return new CommunityAnswer(
            CommunityAnswerKind.Found,
            intro is { } i ? [i] : [],
            outro is { } o ? [o] : [],
            ReferenceDuration: null);
    }

    private static CommunityRange? Range(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var segment) || segment.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var start = OptionalNumber(segment, "start_sec");
        var end = OptionalNumber(segment, "end_sec");
        return start is >= 0 && end is { } e && e > start ? new CommunityRange(start.Value, e) : null;
    }
}
