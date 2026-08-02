using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Vpn;

/// <summary>
/// Screening one address against every configured detector, once, ever.
/// </summary>
/// <remarks>
/// Results are cached PER ADDRESS, not per account: a clean address stays clean whoever
/// connects from it next, and a VPN exit node does not need re-screening for every player
/// who tries it.
///
/// Three separate protections against wasting quota, each of which was added after
/// watching it be wasted:
///
///   UNROUTABLE ADDRESSES ARE NEVER LOOKED UP. A LAN player reconnecting twenty times used
///   to spend five lookups a join on an address no provider can say anything about.
///
///   CONCURRENT CHECKS OF THE SAME ADDRESS SHARE ONE RESULT. Two players joining from one
///   household within a second would otherwise each run the full stack.
///
///   AN OUTAGE IS NOT CACHED. If nothing answered, nothing is stored, and the next
///   connection retries. Caching a failure as "clean" would permanently whitelist every
///   address that connected during a provider blip.
/// </remarks>
public sealed class VpnScreeningService
{
    private readonly IReadOnlyList<IVpnDetector> _detectors;
    private readonly SerializedStore _store;
    private readonly IGeoLocator _geo;
    private readonly IReadOnlyList<IVpnDetector> _all;
    private readonly VpnThresholds _thresholds;
    private readonly MetricsRegistry _metrics;
    private readonly ILogger<VpnScreeningService> _logger;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, Task<VpnRecord?>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a cached verdict stays authoritative. Zero means forever.
    /// </summary>
    /// <remarks>
    /// Addresses do get reassigned - a residential address can become a VPN exit and back -
    /// but not quickly, and re-screening is quota nobody has. Thirty days is the compromise.
    /// </remarks>
    private readonly TimeSpan _cacheTtl;

    public VpnScreeningService(
        IEnumerable<IVpnDetector> detectors,
        SerializedStore store,
        IGeoLocator geo,
        MetricsRegistry metrics,
        ILogger<VpnScreeningService> logger,
        VpnThresholds? thresholds = null,
        TimeSpan? cacheTtl = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _all = detectors.ToList();
        _detectors = _all.Where(d => d.Enabled).ToList();
        _store = store;
        _geo = geo;
        _metrics = metrics;
        _logger = logger;
        _thresholds = (thresholds ?? VpnThresholds.Default).Sanitised();
        _cacheTtl = cacheTtl ?? TimeSpan.FromDays(30);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>False when no detector is configured; the whole subsystem is then a no-op.</summary>
    public bool Enabled => _detectors.Count > 0;

    public IReadOnlyList<IVpnDetector> Detectors => _detectors;

    /// <summary>Every detector the build knows about, configured or not. For diagnostics.</summary>
    public IReadOnlyList<IVpnDetector> AllDetectors => _all;

    private IReadOnlyList<IVpnDetector> Tier(int tier) => _detectors.Where(d => d.Tier == tier).ToList();

    /// <summary>Every stored verdict, keyed by address.</summary>
    public Dictionary<string, VpnRecord> LoadCache() =>
        _store.Read<Dictionary<string, VpnRecord>>(Datasets.VpnChecks, new Dictionary<string, VpnRecord>(StringComparer.Ordinal));

    public VpnRecord? Cached(string ip) => LoadCache().GetValueOrDefault(ip);

    private bool IsUsable(VpnRecord? record) =>
        record is not null &&
        record.Schema == VpnMerge.Schema &&                       // an older merge never saw the newer detectors
        (_cacheTtl == TimeSpan.Zero || _time.GetUtcNow() - record.CheckedAt < _cacheTtl);

    /// <summary>
    /// Screen an address, reusing a cached or in-flight result where possible.
    /// </summary>
    /// <returns>Null when nothing could be determined - do NOT treat that as clean.</returns>
    public Task<VpnRecord?> CheckAsync(string ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip)) return Task.FromResult<VpnRecord?>(null);

        var cached = Cached(ip);
        if (IsUsable(cached))
        {
            _metrics.Increment("vpn_checks_total", MetricLabels.Of("outcome", "cached"), help: "VPN screenings by outcome");
            return Task.FromResult<VpnRecord?>(cached);
        }

        return _inFlight.GetOrAdd(ip, key => RunAsync(key, ct));
    }

    private async Task<VpnRecord?> RunAsync(string ip, CancellationToken ct)
    {
        try
        {
            var now = _time.GetUtcNow();

            if (IpAddresses.IsUnroutable(ip))
            {
                var local = IpAddresses.LocalRecord(ip, now);
                await SaveAsync(local, ct).ConfigureAwait(false);
                _logger.LogDebug("{Ip} is a private/unroutable address - skipped all detectors", ip);
                _metrics.Increment("vpn_checks_total", MetricLabels.Of("outcome", "local"));
                return local;
            }

            // Geolocation first: free, keyless, and useful for every address regardless of
            // which detector keys are set.
            var geo = await _geo.LocateAsync(ip, ct).ConfigureAwait(false);

            TierOutcome? screen = null;
            TierOutcome? confirm = null;

            if (Enabled)
            {
                screen = await RunTierAsync(1, ip, ct).ConfigureAwait(false);

                if (screen.Answered == 0)
                {
                    _logger.LogWarning(
                        "every regular check failed for {Ip} - not caching, will retry on the next connection", ip);
                    _metrics.Increment("vpn_checks_total", MetricLabels.Of("outcome", "outage"));
                    return null;
                }

                if (screen.Hits >= _thresholds.ScreenMin)
                    confirm = await RunTierAsync(2, ip, ct).ConfigureAwait(false);
            }

            var decision = VpnConsensus.Decide(screen, confirm, Tier(2).Count, _thresholds);
            var record = VpnMerge.Merge(ip, decision, screen, confirm, geo, now);

            _logger.LogInformation(
                "{Ip} -> screen {ScreenHits}/{ScreenAnswered}{Confirm} | verdict={Verdict} | action={Action} ({Reason}) | {Breakdown}",
                ip, record.ScreenHits, record.ScreenAnswered,
                confirm is not null ? $" | confirm {record.ConfirmHits}/{record.ConfirmAnswered}" : "",
                decision.Label, decision.Actionable ? "BAN" : "none", decision.Reason,
                string.Join(" ", record.Detectors.Select(d => $"{d.Name}:{(d.Flagged ? "HIT" : "clean")}")));

            await SaveAsync(record, ct).ConfigureAwait(false);
            _metrics.Increment("vpn_checks_total", MetricLabels.Of("outcome", decision.Label.Split(' ')[0].ToLowerInvariant()));
            return record;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "VPN screening failed for {Ip}", ip);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(ip, out _);
        }
    }

    private async Task<TierOutcome> RunTierAsync(int tier, string ip, CancellationToken ct)
    {
        var detectors = Tier(tier);
        if (detectors.Count == 0) return new TierOutcome(0, 0, 0, []);

        var outcomes = await Task.WhenAll(detectors.Select(async detector =>
        {
            try { return await detector.LookupAsync(ip, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A detector must never take down the screen. No reading = "no verdict",
                // which is counted separately from "answered clean".
                _logger.LogWarning("{Detector} threw for {Ip}: {Message}", detector.Name, ip, ex.Message);
                return DetectorOutcome.None(ex.Message);
            }
        })).ConfigureAwait(false);

        /* Said out loud, once per screen, at INFORMATION. A tier where nothing answered
           produces a record with no verdict in it, and every surface downstream then shows
           "not checked" - correctly, but with no way to tell whether that means the keys
           are wrong, the quota is spent, or the host cannot reach the internet at all. */
        if (outcomes.Length > 0 && outcomes.All(o => o.Reading is null))
        {
            _logger.LogInformation("No tier {Tier} detector could screen {Ip}: {Reasons}", tier, ip,
                string.Join("; ", detectors.Zip(outcomes, (d, o) => $"{d.Name}: {o.Failure ?? "no verdict"}")));
        }

        return TierOutcome.From(outcomes.Select(o => o.Reading));
    }

    private Task SaveAsync(VpnRecord record, CancellationToken ct) =>
        _store.UpdateAsync<Dictionary<string, VpnRecord>>(
            Datasets.VpnChecks,
            new Dictionary<string, VpnRecord>(StringComparer.Ordinal),
            cache => { cache[record.Ip] = record; return cache; },
            ct);

    /// <summary>
    /// Probe EVERY detector against one address, ignoring the tier gating.
    /// </summary>
    /// <remarks>
    /// Diagnostic only, for <c>/vpncheck</c>. It spends one lookup per configured detector,
    /// which is why the command is owner-gated.
    /// </remarks>
    public async Task<IReadOnlyList<DetectorProbe>> ProbeAsync(string ip, CancellationToken ct = default)
    {
        return await Task.WhenAll(_all.Select(async detector =>
        {
            /* An UNCONFIGURED detector is listed, not hidden. Omitting it made "I have no
               key for this" and "this one is broken" the same blank space, so the one
               screen that should answer "why is nothing screening?" could not tell you that
               five of the six providers simply have no key set. */
            if (!detector.Enabled)
                return new DetectorProbe(detector.Name, detector.Tier, false, null, null, 0, null, Configured: false);

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var outcome = await detector.LookupAsync(ip, ct).ConfigureAwait(false);
                return new DetectorProbe(detector.Name, detector.Tier, outcome.Reading is not null,
                    outcome.Reading?.Flagged, outcome.Reading?.Detail,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome.Failure, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new DetectorProbe(detector.Name, detector.Tier, false, null, null,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message, true);
            }
        })).ConfigureAwait(false);
    }

    /// <summary>A configuration that can never auto-ban, said out loud at startup.</summary>
    public string? ConfigurationWarning()
    {
        if (!Enabled) return "No VPN detector is configured - screening is disabled entirely.";
        if (VpnConsensus.CanEverAutoBan(Tier(1).Count, Tier(2).Count, _thresholds)) return null;

        return $"Only {Tier(1).Count} regular check(s) and {Tier(2).Count} confirmer(s) are configured, but " +
               $"{_thresholds.ScreenBanMin} must agree to ban without a confirmation detector. " +
               "Auto-ban cannot trigger in this configuration - add another detector, or lower VPN_SCREEN_BAN_MIN.";
    }
}

/// <param name="Answered">False means the detector could not be reached or gave no verdict.</param>
/// <param name="Configured">False when no API key is set. It was never asked, not failed.</param>
public sealed record DetectorProbe(
    string Name, int Tier, bool Answered, bool? Flagged, string? Detail, double DurationMs, string? Error,
    bool Configured = true)
{
    /// <summary>What this detector is FOR, in words, for the diagnostic output.</summary>
    public string Role => Tier == 1 ? "Regular check (every IP)" : "Final confirmation (flagged IPs only)";

    /// <summary>The one line that says what happened, and never a bare "no verdict".</summary>
    public string Outcome =>
        !Configured ? "no API key set - not checked"
        : Answered ? Detail ?? "answered"
        : Error ?? "no verdict, and no reason given";
}

/// <summary>Whois-style geolocation. Separate from reputation: it is free and always run.</summary>
public interface IGeoLocator
{
    Task<GeoLocation?> LocateAsync(string ip, CancellationToken ct);
}

/// <summary>ip-api.com. Keyless, city-level, and generous enough for a game server.</summary>
public sealed class IpApiGeoLocator(ResilientJsonClient client) : IGeoLocator
{
    public async Task<GeoLocation?> LocateAsync(string ip, CancellationToken ct)
    {
        const string fields = "status,country,countryCode,region,regionName,city,zip,lat,lon,timezone,isp,org,as";
        using var document = await client.GetAsync(
            $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields={fields}", "ip-api", ct: ct).ConfigureAwait(false);

        if (document is null) return null;
        var root = document.RootElement;
        if (JsonHelp.Text(root, "status") != "success") return null;

        // The `as` field is "AS15169 Google LLC" - the ASN is the first token.
        var asField = JsonHelp.Text(root, "as");
        var asn = asField?.Split(' ', 2)[0];

        return new GeoLocation(
            City: JsonHelp.Text(root, "city"),
            Region: JsonHelp.Text(root, "regionName") ?? JsonHelp.Text(root, "region"),
            Country: JsonHelp.Text(root, "country"),
            CountryCode: JsonHelp.Text(root, "countryCode"),
            Zip: JsonHelp.Text(root, "zip"),
            Isp: JsonHelp.Text(root, "isp"),
            Organization: JsonHelp.Text(root, "org"),
            Asn: VpnMerge.NormaliseAsn(asn),
            Timezone: JsonHelp.Text(root, "timezone"),
            Latitude: JsonHelp.Number(root, "lat"),
            Longitude: JsonHelp.Number(root, "lon"));
    }
}

/// <summary>
/// Geoapify's IP Geolocation endpoint. Keyed, HTTPS, and a real quota.
/// </summary>
/// <remarks>
/// A SECOND OPINION ON LOCATION ONLY. Geoapify does not report ISP or ASN, and both of
/// those are load-bearing here - <c>EvasionScorer</c> matches a joining player's ASN
/// against the networks banned players used, so a geolocator that cannot supply one would
/// quietly weaken ban-evasion detection if it displaced ip-api. That is why this is a
/// FALLBACK in <see cref="GeoLocatorChain"/> rather than a replacement: ip-api answers
/// first and Geoapify only runs when it could not.
///
/// The response nests its fields (<c>city.name</c>, <c>country.iso_code</c>), and the
/// parsing below accepts either the nested object or a bare string at each position. That
/// is not defensive padding - this bot has no way to notice a provider reshaping its JSON
/// except by every lookup silently returning null, and an unknown location degrades the
/// admin feed rather than announcing itself.
/// </remarks>
public sealed class GeoapifyGeoLocator(ResilientJsonClient client, string apiKey) : IGeoLocator
{
    /// <summary>Nested <c>{"name": "..."}</c>, or the value already being a string.</summary>
    private static string? Named(JsonElement root, string property, string field = "name")
    {
        var member = JsonHelp.Member(root, property);
        return member?.ValueKind switch
        {
            JsonValueKind.Object => JsonHelp.Text(member.Value, field),
            JsonValueKind.String => member.Value.GetString(),
            _ => null,
        };
    }

    public async Task<GeoLocation?> LocateAsync(string ip, CancellationToken ct)
    {
        using var document = await client.GetAsync(
            $"https://api.geoapify.com/v1/ipinfo?ip={Uri.EscapeDataString(ip)}&apiKey={Uri.EscapeDataString(apiKey)}",
            "geoapify",
            // The key travels in the query string, so it would otherwise be logged verbatim
            // on every timeout and rate limit.
            secrets: [apiKey],
            ct: ct).ConfigureAwait(false);

        if (document is null) return null;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var location = JsonHelp.Member(root, "location");

        // `state` and `subdivisions[0]` are the same thing by different names depending on
        // the country; either is a better answer than none.
        var region = Named(root, "state") ?? Named(root, "region");
        if (region is null && JsonHelp.Member(root, "subdivisions") is { ValueKind: JsonValueKind.Array } subdivisions)
        {
            foreach (var entry in subdivisions.EnumerateArray())
            {
                region = JsonHelp.Text(entry, "name");
                if (region is not null) break;
            }
        }

        var result = new GeoLocation(
            City: Named(root, "city"),
            Region: region,
            Country: Named(root, "country"),
            CountryCode: Named(root, "country", "iso_code"),
            Zip: JsonHelp.Text(root, "postcode"),
            Timezone: Named(root, "timezone", "name"),
            Latitude: location is { } point ? JsonHelp.Number(point, "latitude") : null,
            Longitude: location is { } same ? JsonHelp.Number(same, "longitude") : null);

        /* An object that parsed but carried no usable field is NOT an answer. Returning it
           would satisfy the chain's "first non-null wins" rule and stop ip-api from ever
           being tried - turning a provider outage into a permanent blank location. */
        return result.Country is null && result.City is null && result.CountryCode is null ? null : result;
    }
}

/// <summary>
/// Several geolocation providers, tried in order until one answers.
/// </summary>
/// <remarks>
/// FIRST NON-NULL WINS, and the order is the policy: ip-api goes first because it is
/// keyless and returns ISP and ASN, which the evasion scorer needs and the other providers
/// do not supply. A second provider is therefore not a load-balancer - it is what answers
/// when the first is rate-limited, blocked, or down.
///
/// A provider that throws does not end the chain. These are third-party endpoints on the
/// join path; one of them failing badly must not cost the lookup entirely.
/// </remarks>
public sealed class GeoLocatorChain(IEnumerable<IGeoLocator> providers, ILogger<GeoLocatorChain> logger) : IGeoLocator
{
    private readonly IReadOnlyList<IGeoLocator> _providers = providers.ToList();

    public async Task<GeoLocation?> LocateAsync(string ip, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            try
            {
                if (await provider.LocateAsync(ip, ct).ConfigureAwait(false) is { } located) return located;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Geolocation provider {Provider} failed - trying the next one",
                    provider.GetType().Name);
            }
        }

        return null;
    }
}
