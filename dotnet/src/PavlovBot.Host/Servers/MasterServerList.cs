using System.Net.Http;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Servers;

namespace PavlovBot.Host.Servers;

/// <summary>
/// The public Pavlov server browser, fetched from Vankrupt's master server.
/// </summary>
/// <remarks>
/// CACHED, and not because the request is slow. The list is around 90KB and describes every
/// Pavlov server on the platform, so a browse session that re-fetched on every page turn
/// would hammer somebody else's infrastructure for a list that changes once a minute at
/// most. It would also make paging incoherent - page 2 of a list that has since reordered
/// itself is not page 2 of anything.
///
/// A FAILED FETCH DOES NOT CLEAR THE CACHE. A momentary outage should show a slightly stale
/// list with its age visible, not an empty browser that reads as "there are no servers".
/// </remarks>
public sealed class MasterServerList
{
    /// <summary>
    /// The endpoint, with the game version baked into the path.
    /// </summary>
    /// <remarks>
    /// Vankrupt version the URL rather than the payload, so a Pavlov update makes this
    /// return nothing at all rather than something misshapen. That is why an empty list is
    /// reported as a possible version mismatch instead of "no servers online" - the two look
    /// identical from here and have completely different fixes.
    /// </remarks>
    public const string DefaultVersion = "1.0.27";

    private static string Endpoint(string version) =>
        $"https://prod2-shack-pavlov-ms.vankrupt.net/servers/v2/list/{version}/oculus/0/0/0/all";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ILogger<MasterServerList> _logger;
    private readonly TimeProvider _time;
    private readonly string _version;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<ServerListing> _cached = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public MasterServerList(HttpClient http, ILogger<MasterServerList> logger,
        string? version = null, TimeProvider? time = null)
    {
        _http = http;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _version = string.IsNullOrWhiteSpace(version) ? DefaultVersion : version;
    }

    /// <param name="Age">How old the data is. Shown so nobody mistakes a stale list for live.</param>
    /// <param name="Failed">True when the last fetch failed and this is what we still had.</param>
    public sealed record Snapshot(IReadOnlyList<ServerListing> Servers, TimeSpan Age, bool Failed);

    /// <summary>The current list, fetching only when what we have has gone stale.</summary>
    public async Task<Snapshot> GetAsync(bool force = false, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();

        if (!force && _cached.Count > 0 && now - _fetchedAt < Ttl)
            return new Snapshot(_cached, now - _fetchedAt, Failed: false);

        /* Single-flight. Four people running the command at once should produce one request,
           not four - and the three that lose the race want the answer the winner got. */
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (!force && _cached.Count > 0 && now - _fetchedAt < Ttl)
                return new Snapshot(_cached, now - _fetchedAt, Failed: false);

            var fetched = await FetchAsync(ct).ConfigureAwait(false);
            if (fetched is null)
                return new Snapshot(_cached, _time.GetUtcNow() - _fetchedAt, Failed: true);

            _cached = fetched;
            _fetchedAt = _time.GetUtcNow();
            return new Snapshot(_cached, TimeSpan.Zero, Failed: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ServerListing>?> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));

            using var response = await _http.GetAsync(new Uri(Endpoint(_version)), cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Pavlov master server returned {Status} - the server browser will show what it had",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var servers = ServerList.Parse(body);

            if (servers.Count == 0)
            {
                _logger.LogWarning(
                    "Pavlov master server returned no servers for version {Version}. That is usually a game " +
                    "update rather than an empty platform - the version is part of the URL", _version);
            }

            return servers;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Could not reach the Pavlov master server: {Message}", ex.Message);
            return null;
        }
    }
}
