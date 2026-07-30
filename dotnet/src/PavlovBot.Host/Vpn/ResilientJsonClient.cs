using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PavlovBot.Rcon;

namespace PavlovBot.Host.Vpn;

/// <summary>
/// HTTP JSON with a hard timeout, jittered retry, and rate-limit memory.
/// </summary>
/// <remarks>
/// Providers time out, 429, and 5xx. Without this a slow endpoint holds a detector open
/// forever and a burst of joins trips a rate limit with no recovery.
///
/// IT NEVER THROWS FOR A RATE LIMIT. It returns null, so the detector counts as "no
/// verdict" rather than "clean" - an exhausted quota must never read as innocence, which
/// is the single most dangerous failure mode in this subsystem.
/// </remarks>
public sealed class ResilientJsonClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    /// <summary>Host -> when lookups to it may resume.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _rateLimitedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hosts already reported as rejecting us, so the warning is said once, not per join.</summary>
    private readonly ConcurrentDictionary<string, byte> _authWarned = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _time;

    public ResilientJsonClient(HttpClient http, ILogger logger, TimeProvider? time = null)
    {
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <param name="secrets">API keys to scrub from any message that gets logged.</param>
    /// <returns>The parsed body, or null when no verdict could be obtained for any reason.</returns>
    public async Task<JsonDocument?> GetAsync(
        string url,
        string label,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyCollection<string>? secrets = null,
        int attempts = 3,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        if (_rateLimitedUntil.TryGetValue(host, out var until) && _time.GetUtcNow() < until)
        {
            _logger.LogDebug("{Label} skipped - rate limited for another {Seconds}s",
                label, (int)(until - _time.GetUtcNow()).TotalSeconds);
            return null;
        }

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers is not null)
                    foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(6));

                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var wait = response.Headers.RetryAfter?.Delta
                               ?? (response.Headers.RetryAfter?.Date is { } date ? date - _time.GetUtcNow() : (TimeSpan?)null)
                               ?? TimeSpan.FromMinutes(1);
                    if (wait <= TimeSpan.Zero) wait = TimeSpan.FromMinutes(1);

                    _rateLimitedUntil[host] = _time.GetUtcNow() + wait;
                    _logger.LogWarning("{Label} rate limited ({Status}) - pausing lookups to it for {Seconds}s",
                        label, (int)response.StatusCode, (int)wait.TotalSeconds);
                    return null;
                }

                if ((int)response.StatusCode >= 500) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                if ((int)response.StatusCode >= 400)
                {
                    /* A 4xx that is not a rate limit is a CONFIGURATION problem - a bad
                       key, a suspended account, an address that is not allowlisted. Its
                       body has no verdict in it, so it would otherwise fall through and
                       read as a bland "no verdict" forever. Name it ONCE per host so it
                       is fixable instead of merely mysterious. */
                    if (_authWarned.TryAdd(host, 0))
                    {
                        var hint = await ErrorHint(response, secrets).ConfigureAwait(false);
                        _logger.LogWarning(
                            "{Label} rejected the request (HTTP {Status}){Hint}. {Advice}",
                            label, (int)response.StatusCode, hint,
                            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                                ? "Check the API key is correct and active."
                                : "No verdict will be recorded from it.");
                    }
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return JsonDocument.Parse(body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (attempt == attempts - 1)
                {
                    var message = ex is OperationCanceledException ? "timed out" : Scrub(ex.Message, secrets);
                    _logger.LogWarning("{Label} lookup failed: {Message}", label, message);
                    return null;
                }

                /* Exponential backoff with full jitter. Five detectors retry the same
                   upstream on the same schedule otherwise, so a blip makes them all come
                   back in lockstep - the burst most likely to trip the rate limit this is
                   trying to avoid. */
                await Task.Delay(Backoff.Delay(attempt, baseMs: 300, maxMs: 5_000), _time, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static async Task<string> ErrorHint(HttpResponseMessage response, IReadOnlyCollection<string>? secrets)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.GetString() is { } message)
            {
                var text = Scrub(message, secrets);
                return $" - {(text.Length > 120 ? text[..120] : text)}";
            }
        }
        catch (Exception) { }
        return "";
    }

    /// <summary>Replace API keys with asterisks. Provider errors quote the URL back at you.</summary>
    private static string Scrub(string text, IReadOnlyCollection<string>? secrets)
    {
        if (secrets is null) return text;
        foreach (var secret in secrets)
            if (secret.Length > 0) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }
}
