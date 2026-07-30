using System.Collections.Concurrent;
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
        _detectors = detectors.Where(d => d.Enabled).ToList();
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

        var readings = await Task.WhenAll(detectors.Select(async detector =>
        {
            try { return await detector.LookupAsync(ip, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A detector must never take down the screen. Null = "no verdict", which
                // is counted separately from "answered clean".
                _logger.LogWarning("{Detector} threw for {Ip}: {Message}", detector.Name, ip, ex.Message);
                return null;
            }
        })).ConfigureAwait(false);

        return TierOutcome.From(readings);
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
        return await Task.WhenAll(_detectors.Select(async detector =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var reading = await detector.LookupAsync(ip, ct).ConfigureAwait(false);
                return new DetectorProbe(detector.Name, detector.Tier, reading is not null, reading?.Flagged,
                    reading?.Detail, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new DetectorProbe(detector.Name, detector.Tier, false, null, null,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.Message);
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
public sealed record DetectorProbe(
    string Name, int Tier, bool Answered, bool? Flagged, string? Detail, double DurationMs, string? Error)
{
    /// <summary>What this detector is FOR, in words, for the diagnostic output.</summary>
    public string Role => Tier == 1 ? "Regular check (every IP)" : "Final confirmation (flagged IPs only)";
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
