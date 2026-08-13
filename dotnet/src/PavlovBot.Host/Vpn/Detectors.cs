using System.Text.Json;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Vpn;

namespace PavlovBot.Host.Vpn;

/// <param name="Reading">The verdict, or null when there is none.</param>
/// <param name="Failure">Why there is no verdict. Null when there is one.</param>
/// <remarks>
/// The failure text is the whole point of this type. Every path that could not produce a
/// verdict used to return a bare null, so a bad key, an exhausted quota, a blocked outbound
/// port and a provider that simply omitted the field all arrived as the same nothing - and
/// <c>/vpncheck</c>, whose entire job is telling you which of those it is, could only print
/// "no verdict".
/// </remarks>
public sealed record DetectorOutcome(DetectorReading? Reading, string? Failure)
{
    public static DetectorOutcome Answered(DetectorReading reading) => new(reading, null);

    public static DetectorOutcome None(string why) => new(null, why);

    /// <summary>The provider answered, but the body had nothing to read a verdict from.</summary>
    public static DetectorOutcome Unusable(string what) => new(null, $"answered, but {what}");
}

/// <summary>One reputation provider.</summary>
/// <remarks>
/// <see cref="LookupAsync"/> MUST NOT throw, and MUST return an outcome with no reading
/// when the lookup itself failed. "No verdict" is tracked separately from "answered clean",
/// so an outage cannot be read as innocence.
/// </remarks>
public interface IVpnDetector
{
    string Name { get; }

    /// <summary>1 = regular check on every address, 2 = final confirmation on flagged only.</summary>
    int Tier { get; }

    /// <summary>False when unconfigured. An unconfigured detector is skipped, not failed.</summary>
    bool Enabled { get; }

    Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct);
}

/// <summary>API keys for the reputation providers.</summary>
/// <remarks>
/// Every one is optional and the whole subsystem is a no-op with none set. ipapi.is and
/// proxycheck work keyless, which is why a bot with no configuration at all still screens
/// - at a quota low enough that a busy server needs a key.
/// </remarks>
public sealed record VpnKeys(
    string? IpHub = null,
    string? Ipqs = null,
    string? ProxyCheck = null,
    string? IpapiIs = null,
    string? Sentinel = null)
{
    public IReadOnlyCollection<string> Secrets =>
        new[] { IpHub, Ipqs, ProxyCheck, IpapiIs, Sentinel }
            .Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).ToList();
}

internal static class JsonHelp
{
    public static JsonElement? Member(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : null;

    public static string? Text(JsonElement parent, string name)
    {
        var value = Member(parent, name);
        var text = value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.ToString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static bool? Bool(JsonElement parent, string name) => Member(parent, name)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static double? Number(JsonElement parent, string name)
    {
        var value = Member(parent, name);
        return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var n) ? n : null;
    }

    public static int? Int(JsonElement parent, string name)
    {
        var value = Member(parent, name);
        return value?.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var n) ? n : null;
    }
}

/// <summary>
/// IPHub. <c>block</c>: 0 residential/unclassified, 1 non-residential, 2 unknown.
/// </summary>
/// <remarks>
/// Only <c>block == 1</c> is a hit. Block 2 means IPHub does not know, which is the whole
/// tri-state problem in one field - reading "not 1" as clean would treat every unknown
/// address as verified innocent.
/// </remarks>
public sealed class IpHubDetector(ResilientJsonClient client, VpnKeys keys) : IVpnDetector
{
    public string Name => "iphub";
    public int Tier => 1;
    public bool Enabled => !string.IsNullOrEmpty(keys.IpHub);

    public async Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct)
    {
        var fetch = await client.FetchAsync(
            $"https://v2.api.iphub.info/ip/{Uri.EscapeDataString(ip)}", Name,
            headers: new Dictionary<string, string> { ["X-Key"] = keys.IpHub! },
            secrets: keys.Secrets, ct: ct).ConfigureAwait(false);

        using var document = fetch.Document;
        if (document is null) return DetectorOutcome.None(fetch.Failure ?? "no verdict");

        var root = document.RootElement;
        if (JsonHelp.Int(root, "block") is not { } block)
            return DetectorOutcome.Unusable("the response had no `block` field");

        return DetectorOutcome.Answered(new DetectorReading
        {
            Name = Name, Tier = Tier,
            Flagged = block == 1,
            Detail = $"block:{block}",
            IpHubBlock = block,
            // Block 2 is UNKNOWN and must stay null in both directions.
            Hosting = block == 1 ? true : block == 0 ? false : null,
            Residential = block == 0 ? true : block == 1 ? false : null,
            Isp = JsonHelp.Text(root, "isp"),
            Organization = JsonHelp.Text(root, "isp"),
            Asn = VpnMerge.NormaliseAsn(JsonHelp.Text(root, "asn")),
            Country = JsonHelp.Text(root, "countryName"),
            CountryCode = JsonHelp.Text(root, "countryCode"),
        });
    }
}

/// <summary>ipapi.is. Works keyless, which makes it the baseline screener.</summary>
public sealed class IpapiIsDetector(ResilientJsonClient client, VpnKeys keys) : IVpnDetector
{
    public string Name => "ipapi.is";
    public int Tier => 1;
    public bool Enabled => true;   // keyless-capable

    public async Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(keys.IpapiIs) ? "" : $"&key={keys.IpapiIs}";
        var fetch = await client.FetchAsync(
            $"https://api.ipapi.is/?q={Uri.EscapeDataString(ip)}{key}", Name,
            headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            secrets: keys.Secrets, ct: ct).ConfigureAwait(false);

        using var document = fetch.Document;
        if (document is null) return DetectorOutcome.None(fetch.Failure ?? "no verdict");

        var root = document.RootElement;
        if (JsonHelp.Bool(root, "is_vpn") is null)
            return DetectorOutcome.Unusable("the response had no `is_vpn` field - usually the free quota being spent");

        var location = JsonHelp.Member(root, "location") ?? default;
        var asn = JsonHelp.Member(root, "asn") ?? default;
        var company = JsonHelp.Member(root, "company") ?? default;

        /* Anonymising signals only. is_datacenter alone is NOT a hit - plenty of
           legitimate players sit behind carrier and cloud ranges - but it is reported so
           the classification merge can use it. */
        var hits = new[] { ("vpn", "is_vpn"), ("proxy", "is_proxy"), ("tor", "is_tor"), ("abuser", "is_abuser") }
            .Where(h => JsonHelp.Bool(root, h.Item2) == true).Select(h => h.Item1).ToList();

        var datacenter = JsonHelp.Bool(root, "is_datacenter");
        var companyType = JsonHelp.Text(company, "type");

        return DetectorOutcome.Answered(new DetectorReading
        {
            Name = Name, Tier = Tier,
            Flagged = hits.Count > 0,
            Detail = (hits.Count > 0 ? string.Join("+", hits) : "clean") + (datacenter == true ? " (datacenter)" : ""),
            Vpn = JsonHelp.Bool(root, "is_vpn"),
            Proxy = JsonHelp.Bool(root, "is_proxy"),
            Tor = JsonHelp.Bool(root, "is_tor"),
            Abuser = JsonHelp.Bool(root, "is_abuser"),
            Hosting = datacenter,
            Mobile = JsonHelp.Bool(root, "is_mobile"),
            Residential = companyType is null ? null : companyType == "isp",
            Provider = JsonHelp.Text(company, "name"),
            Asn = VpnMerge.NormaliseAsn(JsonHelp.Text(asn, "asn")),
            Organization = JsonHelp.Text(asn, "org") ?? JsonHelp.Text(company, "name"),
            Isp = JsonHelp.Text(asn, "org") ?? JsonHelp.Text(company, "name"),
            Country = JsonHelp.Text(location, "country"),
            CountryCode = JsonHelp.Text(location, "country_code"),
            Region = JsonHelp.Text(location, "state") ?? JsonHelp.Text(location, "region"),
            City = JsonHelp.Text(location, "city"),
        });
    }
}

/// <summary>
/// proxycheck.io. The only source of the consumer VPN BRAND name.
/// </summary>
/// <remarks>
/// Tier 1 despite its small quota, because <c>operator.name</c> ("NordVPN") is what staff
/// actually want to read - so screening every connection means the feed can name the
/// provider even for addresses that come back clean. Keyless is 100/day.
/// </remarks>
public sealed class ProxyCheckDetector(ResilientJsonClient client, VpnKeys keys) : IVpnDetector
{
    public string Name => "proxycheck";
    public int Tier => 1;
    public bool Enabled => true;   // keyless-capable

    public async Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct)
    {
        var key = string.IsNullOrEmpty(keys.ProxyCheck) ? "" : $"&key={keys.ProxyCheck}";
        var fetch = await client.FetchAsync(
            $"https://proxycheck.io/v2/{Uri.EscapeDataString(ip)}?vpn=1&risk=1&asn=1{key}", Name,
            headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            secrets: keys.Secrets, ct: ct).ConfigureAwait(false);

        using var document = fetch.Document;
        if (document is null) return DetectorOutcome.None(fetch.Failure ?? "no verdict");

        var root = document.RootElement;

        var status = JsonHelp.Text(root, "status");
        if (status is not ("ok" or "warning"))
            return DetectorOutcome.Unusable($"status was `{status ?? "missing"}` - usually the keyless 100/day quota being spent");
        if (JsonHelp.Member(root, ip) is not { } record)
            return DetectorOutcome.Unusable($"the response carried no record for {ip}");

        // VPN | Business | Residential | Public Proxy | Compromised Server | ...
        var type = (JsonHelp.Text(record, "type") ?? "").ToLowerInvariant();
        var proxy = (JsonHelp.Text(record, "proxy") ?? "").ToLowerInvariant();
        var risk = JsonHelp.Number(record, "risk");
        var operatorName = JsonHelp.Member(record, "operator") is { } op ? JsonHelp.Text(op, "name") : null;

        return DetectorOutcome.Answered(new DetectorReading
        {
            Name = Name, Tier = Tier,
            Flagged = proxy == "yes",
            Detail = $"proxy:{proxy}" + (type.Length > 0 ? $" type:{type}" : "") + (risk is { } r ? $" risk:{r:0}" : ""),
            // Only assert what the type actually says; an unrecognised type stays unknown.
            Vpn = type == "vpn" ? true : null,
            Tor = type.Contains("tor", StringComparison.Ordinal) ? true : null,
            Hosting = type is "business" || type.Contains("hosting", StringComparison.Ordinal)
                                         || type.Contains("server", StringComparison.Ordinal) ? true : null,
            Residential = type == "residential" ? true : null,
            ThreatScore = risk,
            // operator.name is the consumer brand; provider is the underlying network.
            Provider = operatorName ?? JsonHelp.Text(record, "provider"),
            Asn = VpnMerge.NormaliseAsn(JsonHelp.Text(record, "asn")),
            Organization = JsonHelp.Text(record, "organisation") ?? JsonHelp.Text(record, "provider"),
            Isp = JsonHelp.Text(record, "provider") ?? JsonHelp.Text(record, "organisation"),
            Country = JsonHelp.Text(record, "country"),
            CountryCode = JsonHelp.Text(record, "isocode"),
            Region = JsonHelp.Text(record, "region"),
            City = JsonHelp.Text(record, "city"),
        });
    }
}

/// <summary>
/// Sentinel (sntlhq.com). Quota is per HOUR rather than per day.
/// </summary>
/// <remarks>
/// 1,000/hour means it is the one screener a busy server cannot realistically exhaust.
/// Uses the IP-ONLY lookup endpoint: <c>/v1/evaluate</c> needs a token minted by a browser
/// SDK, which a game server has no way to produce. That also means the consumer brand
/// field is not in this response, so proxycheck stays the source for the brand name.
///
/// Parsing lives in <see cref="SentinelParser"/> - see its remarks for why it needs 200
/// lines of its own.
/// </remarks>
public sealed class SentinelDetector(ResilientJsonClient client, VpnKeys keys, ILogger<SentinelDetector> logger) : IVpnDetector
{
    private bool _shapeWarned;

    public string Name => "sentinel";
    public int Tier => 1;
    public bool Enabled => !string.IsNullOrEmpty(keys.Sentinel);

    public async Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct)
    {
        var fetch = await client.FetchAsync(
            $"https://sntlhq.com/v1/lookup/{Uri.EscapeDataString(ip)}", Name,
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {keys.Sentinel}",
                ["Accept"] = "application/json",
            },
            secrets: keys.Secrets, ct: ct).ConfigureAwait(false);

        using var document = fetch.Document;
        if (document is null) return DetectorOutcome.None(fetch.Failure ?? "no verdict");

        var reading = SentinelParser.Parse(document.RootElement, out var unrecognised);
        if (unrecognised is not null && !_shapeWarned)
        {
            // Once, not per join - but loudly, with the values, because the keys alone
            // were never enough to spot what was wrong.
            _shapeWarned = true;
            logger.LogWarning("sentinel {Detail}", unrecognised);
        }

        return reading is null
            ? DetectorOutcome.Unusable(unrecognised ?? "the response had no verdict in it")
            : DetectorOutcome.Answered(reading);
    }
}

/// <summary>
/// IPQualityScore. Tier 2 - the final confirmation, spent only on a flagged address.
/// </summary>
public sealed class IpqsDetector(ResilientJsonClient client, VpnKeys keys) : IVpnDetector
{
    public string Name => "ipqs";
    public int Tier => 2;
    public bool Enabled => !string.IsNullOrEmpty(keys.Ipqs);

    public async Task<DetectorOutcome> LookupAsync(string ip, CancellationToken ct)
    {
        var fetch = await client.FetchAsync(
            $"https://www.ipqualityscore.com/api/json/ip/{keys.Ipqs}/{Uri.EscapeDataString(ip)}?strictness=1", Name,
            secrets: keys.Secrets, ct: ct).ConfigureAwait(false);

        using var document = fetch.Document;
        if (document is null) return DetectorOutcome.None(fetch.Failure ?? "no verdict");

        var root = document.RootElement;
        if (JsonHelp.Bool(root, "success") != true)
            return DetectorOutcome.Unusable(JsonHelp.Text(root, "message") ?? "`success` was not true - usually an invalid key or a spent quota");

        var vpn = JsonHelp.Bool(root, "vpn") ?? false;
        var proxy = JsonHelp.Bool(root, "proxy") ?? false;
        var tor = JsonHelp.Bool(root, "tor") ?? false;
        var fraud = JsonHelp.Number(root, "fraud_score");

        /* connection_type is the most direct residential/mobile/datacenter signal any
           provider gives. Trust it only when actually present - and NEVER use IPQS's
           `host`, which is the address's hostname string, not a hosting flag: it is truthy
           on nearly every address, which would mark the whole player base as datacenter. */
        var connectionType = (JsonHelp.Text(root, "connection_type") ?? "").ToLowerInvariant();
        var hasType = connectionType.Length > 0;

        return DetectorOutcome.Answered(new DetectorReading
        {
            Name = Name, Tier = Tier,
            Flagged = vpn || proxy || tor,
            Detail = $"vpn:{vpn} proxy:{proxy} tor:{tor} fraud:{(fraud is { } f ? $"{f:0}" : "?")}",
            Vpn = vpn, Proxy = proxy, Tor = tor,
            // When the type IS present, report an explicit false rather than null, so
            // "the provider says not mobile" is not confused with "nobody knows".
            Hosting = hasType ? connectionType.Contains("data center", StringComparison.Ordinal) : null,
            Residential = hasType ? connectionType.Contains("residential", StringComparison.Ordinal) : null,
            Mobile = hasType
                ? JsonHelp.Bool(root, "mobile") == true || connectionType.Contains("mobile", StringComparison.Ordinal)
                : JsonHelp.Bool(root, "mobile"),
            ThreatScore = fraud,
            Asn = VpnMerge.NormaliseAsn(JsonHelp.Text(root, "ASN")),
            Organization = JsonHelp.Text(root, "organization") ?? JsonHelp.Text(root, "ISP"),
            Isp = JsonHelp.Text(root, "ISP"),
            Country = JsonHelp.Text(root, "country_code"),
            CountryCode = JsonHelp.Text(root, "country_code"),
            Region = JsonHelp.Text(root, "region"),
            City = JsonHelp.Text(root, "city"),
        });
    }
}
