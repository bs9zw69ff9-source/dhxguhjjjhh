using System.Globalization;

namespace PavlovBot.Core.Vpn;

/// <summary>
/// Turning what the detectors said into a verdict, and separately into permission to ban.
/// </summary>
/// <remarks>
/// Detection runs in two stacked tiers so accuracy is high without burning quota:
///
///   TIER 1 - REGULAR CHECK, high quotas, run on EVERY not-yet-checked IP.
///   TIER 2 - FINAL CONFIRMATION, tiny quota, highest accuracy, ONLY on a screen hit.
///
/// A clean screen ends the check immediately, so tier 2's quota is only ever spent on
/// addresses that already look suspicious.
///
/// THE VERDICT AND THE PERMISSION TO BAN ARE SEPARATE FIELDS, and separating them was a
/// bug fix, not a design flourish. They were once one tri-state, and a <c>null</c>
/// (inconclusive, or below the screening threshold) read as permission to ban - which
/// banned players on a single screening hit and made the consensus threshold dead code.
///
/// PERMISSION TO BAN IS A HEAD COUNT ACROSS BOTH TIERS: at least
/// <see cref="VpnThresholds.BanMin"/> detectors flagged the address. The tier-2 confirmer
/// used to hold a VETO - one dissent from it made a booking unactionable however many
/// screeners had agreed - and on a free IPQS key, which is 35 lookups a day, that veto
/// silently became "auto-ban is off" the moment the quota ran out. It still names the
/// verdict as confirmed or disputed for whoever reads the card; it no longer overrules a
/// consensus by itself.
/// </remarks>
public static class VpnConsensus
{
    /// <param name="screen">Tier-1 outcome, or null when detection is disabled entirely.</param>
    /// <param name="confirm">Tier-2 outcome, or null when the screen came back clean.</param>
    /// <param name="configuredConfirmers">
    /// How many tier-2 detectors exist. Distinguishes "all confirmers failed" from "none
    /// configured", which need different messages to whoever is reading the log.
    /// </param>
    public static VpnDecision Decide(
        TierOutcome? screen,
        TierOutcome? confirm,
        int configuredConfirmers,
        VpnThresholds? thresholds = null)
    {
        var limits = (thresholds ?? VpnThresholds.Default).Sanitised();

        if (screen is null)
            return new VpnDecision(false, null, false, "detection disabled");

        /* An outage must NEVER be cached as clean. The caller treats this as "do not
           cache, retry on the next connection" - the alternative is that a five-minute
           provider blip permanently whitelists every IP that connected during it. */
        if (screen.Answered == 0)
            return new VpnDecision(false, null, false, "every regular check failed - no verdict");

        var flagged = screen.Hits >= limits.ScreenMin;
        if (!flagged)
            return new VpnDecision(false, null, false, "all regular checks clean");

        /* THE HEAD COUNT, across both tiers. A confirmer's agreement is one more voice for
           banning, and its disagreement is recorded without being a veto - which is what it
           used to be, and what quietly turned auto-ban off whenever its quota ran dry. */
        var hits = screen.Hits + (confirm?.Hits ?? 0);
        var answered = screen.Answered + (confirm?.Answered ?? 0);
        var actionable = hits >= limits.BanMin;

        var count = $"{hits} of {answered} detector(s) flagged it, {limits.BanMin} required -> " +
                    (actionable ? "actionable" : "NOT actionable");

        if (confirm is not null && confirm.Answered > 0)
        {
            var confirmed = confirm.Hits >= limits.ConfirmMin;
            return new VpnDecision(
                Flagged: true,
                Confirmed: confirmed,
                Actionable: actionable,
                /* confirm.Hits, NOT a literal 0. "Disputed" only means the hits fell short of
                   ConfirmMin, and that threshold is configurable (VPN_CONFIRM_MIN): at 2, one
                   confirmer flagging the address reads as "disputed it (0/2)" when it was
                   1 of 2. This string is the justification a moderator reads behind an
                   automatic ban, so understating the evidence is the wrong way to be wrong. */
                Reason: (confirmed
                    ? $"confirmation agreed ({confirm.Hits}/{confirm.Answered})"
                    : $"confirmation disputed it ({confirm.Hits}/{confirm.Answered}, {limits.ConfirmMin} required)")
                    + $"; {count}");
        }

        // Nothing could confirm, which changes nothing about the count - it only removes a
        // voice from it, and the reason says which so a dry quota is distinguishable from a
        // configuration that never had a confirmer.
        var why = configuredConfirmers > 0 ? "all confirmers failed" : "none configured";

        return new VpnDecision(
            Flagged: true,
            Confirmed: null,
            Actionable: actionable,
            Reason: $"no confirmation available ({why}); {count}");
    }

    /// <summary>
    /// Whether this configuration could ever reach the ban threshold at all.
    /// </summary>
    /// <remarks>
    /// Worth saying out loud at startup: a setup with one detector and a ban threshold of
    /// two never bans anybody, and looks identical to one that simply never sees a VPN.
    ///
    /// Counts BOTH TIERS, because both now vote. It is still optimistic - it asks whether
    /// enough detectors exist, not whether their quotas are intact - so it is a floor on
    /// what can go wrong, not a guarantee that anything works.
    /// </remarks>
    public static bool CanEverAutoBan(int configuredScreeners, int configuredConfirmers, VpnThresholds? thresholds = null)
    {
        var limits = (thresholds ?? VpnThresholds.Default).Sanitised();
        return configuredScreeners + configuredConfirmers >= limits.BanMin;
    }
}

/// <summary>
/// One address's merged intelligence, after every detector has had its say.
/// </summary>
public sealed record VpnRecord
{
    public required string Ip { get; init; }
    public required VpnDecision Decision { get; init; }

    public bool? Vpn { get; init; }
    public bool? Proxy { get; init; }
    public bool? Tor { get; init; }
    public bool? Hosting { get; init; }
    public bool? Residential { get; init; }
    public bool? Mobile { get; init; }
    public double? ThreatScore { get; init; }

    public string? Provider { get; init; }
    public string? Asn { get; init; }
    public string? Organization { get; init; }
    public string? Isp { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? Region { get; init; }
    public string? City { get; init; }

    /// <summary>
    /// Approximate coordinates, when the geolocator supplied them.
    /// </summary>
    /// <remarks>
    /// NOT part of <see cref="Schema"/> on purpose. Records are cached per address
    /// effectively forever, so bumping the schema to gain a map would force a full
    /// re-screen of every known address through every detector - spending the entire
    /// day's quota on something cosmetic. An address screened before these existed simply
    /// has no coordinates and shows no map; <c>/iplookup refresh:true</c> fills them in for
    /// the one address somebody actually cares about.
    ///
    /// City-level at best, and often only the ISP's registered centroid. Precise enough to
    /// say "not the country they claim", never precise enough to say where somebody lives.
    /// </remarks>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public int ScreenHits { get; init; }
    public int ScreenAnswered { get; init; }
    public int ConfirmHits { get; init; }
    public int ConfirmAnswered { get; init; }

    /// <summary>Which providers contributed, for the audit trail.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public IReadOnlyList<DetectorReading> Detectors { get; init; } = [];

    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>
    /// Bumped whenever the merge changes shape or a detector is added.
    /// </summary>
    /// <remarks>
    /// Load-bearing. Results are cached per IP effectively forever, so a cache written by
    /// an older merge would never be re-screened by the new one - which is exactly what
    /// happened when Sentinel was added without bumping it: every already-known IP stayed
    /// on a verdict that had never consulted the new detector.
    /// </remarks>
    public int Schema { get; init; } = VpnMerge.Schema;

    /// <summary>True for a private/unroutable address, which no provider can check.</summary>
    public bool Local { get; init; }
}

/// <summary>
/// Merging disagreeing providers into one record.
/// </summary>
/// <remarks>
/// Providers disagree, and each knows different things, so nothing is assumed from a
/// single source. Three different merge strategies, chosen per field:
///
///   ANY-TRUE for anonymising signals. One provider knowing an address is Tor is enough;
///   the others simply have not seen it.
///
///   PREFERENCE ORDER for classifications. "Is this residential?" has a right answer, and
///   a provider with a direct classification must outrank one with a coarse proxy for it.
///
///   FIRST NON-NULL for descriptive fields, where any answer is as good as another.
/// </remarks>
public static class VpnMerge
{
    /// <summary>See <see cref="VpnRecord.Schema"/>. Bump when the merge changes.</summary>
    public const int Schema = 3;

    /// <summary>Provider preference for "is this a datacenter address".</summary>
    /// <remarks>
    /// IPQS's <c>connection_type</c> and ipapi.is's <c>is_datacenter</c> are direct
    /// classifications. IPHub's <c>block==1</c> only means "non-residential", which is
    /// coarse and noisy - ORing would let it override IPQS saying the address is plainly
    /// residential, so it is the last resort.
    /// </remarks>
    private static readonly string[] HostingPreference = ["ipqs", "ipapi.is", "sentinel", "proxycheck", "iphub"];

    /// <summary>Provider preference for "is this residential".</summary>
    /// <remarks>
    /// Sentinel is LAST on purpose: it only ever asserts residential=false (when it saw a
    /// datacenter) and never residential=true, so ahead of a real classifier its one-sided
    /// answer would win a question it cannot actually answer.
    /// </remarks>
    private static readonly string[] ResidentialPreference = ["ipqs", "proxycheck", "ipapi.is", "iphub", "sentinel"];

    private static readonly string[] ThreatScorePreference = ["ipqs", "sentinel", "proxycheck"];
    private static readonly string[] OrganizationPreference = ["ipqs", "vpnapi", "proxycheck", "ipapi.is", "sentinel"];

    /// <param name="geo">Whois geolocation, which is city-level and beats a detector's country.</param>
    public static VpnRecord Merge(
        string ip,
        VpnDecision decision,
        TierOutcome? screen,
        TierOutcome? confirm,
        GeoLocation? geo,
        DateTimeOffset checkedAt)
    {
        var all = new List<DetectorReading>();
        if (screen is not null) all.AddRange(screen.Readings);
        if (confirm is not null) all.AddRange(confirm.Readings);

        var hosting = Preferring(all, HostingPreference, r => r.Hosting);
        var residentialRaw = Preferring(all, ResidentialPreference, r => r.Residential);

        /* The consumer brand is what staff actually want to read. proxycheck's
           operator.name is the only source of it; every other detector knows the
           underlying network ("M247", "Datacamp"), so they are the fallback. */
        var provider = all.FirstOrDefault(r => r.Name == "proxycheck" && r.Provider is not null)?.Provider
                       ?? First(all, r => r.Provider);

        return new VpnRecord
        {
            Ip = ip,
            Decision = decision,

            // A flagged address with no explicit vpn signal is still reported as one -
            // something anonymising was detected even if nobody named which.
            Vpn = AnyTrue(all, r => r.Vpn) ?? (decision.Flagged ? true : null),
            Proxy = AnyTrue(all, r => r.Proxy),
            Tor = AnyTrue(all, r => r.Tor),
            Hosting = hosting,
            // A hosting address is by definition not residential, even if no provider said so.
            Residential = hosting == true ? false : residentialRaw,
            Mobile = AnyTrue(all, r => r.Mobile),
            ThreatScore = PreferringNumber(all, ThreatScorePreference, r => r.ThreatScore),

            Provider = provider,
            Asn = First(all, r => r.Asn) ?? geo?.Asn,
            Organization = Preferring(all, OrganizationPreference, r => r.Organization) ?? geo?.Organization,
            Isp = geo?.Isp ?? First(all, r => r.Isp),
            Country = geo?.Country ?? First(all, r => r.Country),
            CountryCode = geo?.CountryCode ?? First(all, r => r.CountryCode),
            Region = geo?.Region ?? First(all, r => r.Region),
            City = geo?.City ?? First(all, r => r.City),
            Latitude = geo?.Latitude,
            Longitude = geo?.Longitude,

            ScreenHits = screen?.Hits ?? 0,
            ScreenAnswered = screen?.Answered ?? 0,
            ConfirmHits = confirm?.Hits ?? 0,
            ConfirmAnswered = confirm?.Answered ?? 0,

            Sources = all.Select(r => r.Name).ToList(),
            Detectors = all,
            CheckedAt = checkedAt,
        };
    }

    /// <summary>
    /// True when any provider says true, false when any says false, null when nobody knows.
    /// </summary>
    /// <remarks>
    /// The asymmetry is deliberate: a positive is evidence somebody has seen this address
    /// used for anonymising, and the providers that have not seen it are not contradicting
    /// that - they have no data.
    /// </remarks>
    internal static bool? AnyTrue(IEnumerable<DetectorReading> readings, Func<DetectorReading, bool?> field)
    {
        var values = readings.Select(field).ToList();
        if (values.Any(v => v == true)) return true;
        if (values.Any(v => v == false)) return false;
        return null;
    }

    internal static T? First<T>(IEnumerable<DetectorReading> readings, Func<DetectorReading, T?> field) where T : class =>
        readings.Select(field).FirstOrDefault(v => v is not null);

    /// <summary>The first non-null answer from a named provider, then anyone's.</summary>
    internal static bool? Preferring(IReadOnlyList<DetectorReading> readings, string[] order, Func<DetectorReading, bool?> field)
    {
        foreach (var name in order)
        {
            var value = readings.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (value is not null && field(value) is { } answer) return answer;
        }
        return readings.Select(field).FirstOrDefault(v => v is not null);
    }

    internal static string? Preferring(IReadOnlyList<DetectorReading> readings, string[] order, Func<DetectorReading, string?> field)
    {
        foreach (var name in order)
        {
            var value = readings.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (value is not null && field(value) is { Length: > 0 } answer) return answer;
        }
        return First(readings, field);
    }

    internal static double? PreferringNumber(IReadOnlyList<DetectorReading> readings, string[] order, Func<DetectorReading, double?> field)
    {
        foreach (var name in order)
        {
            var value = readings.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            if (value is not null && field(value) is { } answer) return answer;
        }
        return readings.Select(field).FirstOrDefault(v => v is not null);
    }

    /// <summary>Normalise an ASN to "AS1234", accepting it with or without the prefix.</summary>
    public static string? NormaliseAsn(object? raw)
    {
        var text = raw switch
        {
            null => null,
            string s => s.Trim(),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => ((long)d).ToString(CultureInfo.InvariantCulture),
            _ => raw.ToString()?.Trim(),
        };
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("AS", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return text.Length == 0 ? null : $"AS{text}";
    }
}

/// <param name="Isp">Who provides the connection, as whois reports it.</param>
public sealed record GeoLocation(
    string? City = null,
    string? Region = null,
    string? Country = null,
    string? CountryCode = null,
    string? Zip = null,
    string? Isp = null,
    string? Organization = null,
    string? Asn = null,
    string? Timezone = null,
    double? Latitude = null,
    double? Longitude = null)
{
    /// <summary>"Toronto, Ontario, Canada M5V  -  Bell Canada  -  TZ America/Toronto".</summary>
    public string? FullDescription()
    {
        var place = string.Join(", ", new[] { City, Region, Country }.Where(p => !string.IsNullOrEmpty(p)));
        if (!string.IsNullOrEmpty(Zip)) place = $"{place} {Zip}".Trim();

        var parts = new[]
        {
            place.Length > 0 ? place : Country,
            Isp,
            Timezone is { Length: > 0 } tz ? $"TZ {tz}" : null,
        }.Where(p => !string.IsNullOrEmpty(p)).ToList();

        return parts.Count > 0 ? string.Join("  -  ", parts) : null;
    }
}
