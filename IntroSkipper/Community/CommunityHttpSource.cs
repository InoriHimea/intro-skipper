// SPDX-FileCopyrightText: 2026 InoriHimea
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Net;
using System.Text.Json;
using IntroSkipper.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.Community;

/// <summary>
/// Shared HTTP plumbing of the community sources: one request per item (both services forbid
/// bulk downloads), a per-source pace, and a cool-down after a rate-limit answer so a scan does
/// not keep hammering a service that already refused it.
/// </summary>
/// <param name="httpClientFactory">Jellyfin's HTTP client factory.</param>
/// <param name="logger">Logger.</param>
/// <param name="timeProvider">Clock for pacing and cool-downs.</param>
internal abstract partial class CommunityHttpSource(IHttpClientFactory httpClientFactory, ILogger logger, TimeProvider timeProvider) : ICommunitySource, IDisposable
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _defaultCoolDown = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _coolDownUntil = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>
    /// Gets the minimum gap between two requests to this source.
    /// </summary>
    protected abstract TimeSpan RequestInterval { get; }

    /// <inheritdoc />
    public abstract bool IsEnabled(PluginConfiguration config);

    /// <inheritdoc />
    public abstract Task<CommunityAnswer> LookupAsync(CommunityLookup lookup, CancellationToken cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the request gate.
    /// </summary>
    /// <param name="disposing">Whether managed resources are released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Sends one GET and parses the body. A 404 is a definitive miss; a 429, 5xx, timeout or
    /// transport failure is <see cref="CommunityAnswerKind.Unavailable"/>; any other status and
    /// unreadable bodies are misses, since asking again would get the same answer.
    /// </summary>
    /// <param name="url">Absolute request URL.</param>
    /// <param name="parse">Parses a 200 body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer.</returns>
    protected async Task<CommunityAnswer> GetAsync(string url, Func<JsonElement, CommunityAnswer> parse, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (now < _coolDownUntil)
            {
                return CommunityAnswer.Unavailable;
            }

            if (now < _nextRequestAt)
            {
                await Task.Delay(_nextRequestAt - now, timeProvider, cancellationToken).ConfigureAwait(false);
            }

            _nextRequestAt = timeProvider.GetUtcNow() + RequestInterval;

            using var client = httpClientFactory.CreateClient(NamedClient.Default);
            client.Timeout = _requestTimeout;
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = RetryAfter(response);
                _coolDownUntil = timeProvider.GetUtcNow() + retryAfter;
                LogRateLimited(logger, Name, retryAfter);
                return CommunityAnswer.Unavailable;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return CommunityAnswer.NotFound;
            }

            if ((int)response.StatusCode >= 500)
            {
                LogServerError(logger, Name, (int)response.StatusCode);
                return CommunityAnswer.Unavailable;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogUnexpectedStatus(logger, Name, (int)response.StatusCode);
                return CommunityAnswer.NotFound;
            }

            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                return parse(document.RootElement);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation.
            LogUnavailable(logger, Name, "timeout");
            return CommunityAnswer.Unavailable;
        }
        catch (HttpRequestException ex)
        {
            LogUnavailable(logger, Name, ex.Message);
            return CommunityAnswer.Unavailable;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            LogMalformed(logger, Name, ex.Message);
            return CommunityAnswer.NotFound;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads an optional number property.
    /// </summary>
    /// <param name="element">JSON object.</param>
    /// <param name="name">Property name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or null.</returns>
    protected static double? OptionalNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        // TheIntroDB reports resets in seconds: a short window reset, and a daily usage reset
        // that is 0 while the daily cap is not the cause. Prefer the daily one when it is set.
        var reset = Seconds(response, "X-UsageLimit-Reset") is > 0 and var usage ? usage : Seconds(response, "X-RateLimit-Reset");
        return reset is > 0 ? TimeSpan.FromSeconds(Math.Max(reset.Value, 10)) : _defaultCoolDown;
    }

    private static double? Seconds(HttpResponseMessage response, string header)
        => response.Headers.TryGetValues(header, out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} rate limit reached; pausing community lookups for {Delay}")]
    private static partial void LogRateLimited(ILogger logger, string source, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} returned server error {Status}; the item stays eligible for the next scan")]
    private static partial void LogServerError(ILogger logger, string source, int status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Source} returned unexpected status {Status}")]
    private static partial void LogUnexpectedStatus(ILogger logger, string source, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} unavailable ({Reason}); the item stays eligible for the next scan")]
    private static partial void LogUnavailable(ILogger logger, string source, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Source} returned an unreadable body: {Reason}")]
    private static partial void LogMalformed(ILogger logger, string source, string reason);
}
