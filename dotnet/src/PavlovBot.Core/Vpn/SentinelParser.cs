using System.Globalization;
using System.Text.Json;

namespace PavlovBot.Core.Vpn;

/// <summary>
/// Reading Sentinel's (sntlhq.com) reply.
/// </summary>
/// <remarks>
/// This parser has its own file because it produced three separate production bugs, each
/// of which presented identically - "responded but gave no verdict" - and each of which
/// had a different cause. All three are encoded as rules here:
///
///   TWO BODY SHAPES, BOTH USING `network` FOR DIFFERENT THINGS. The lookup style puts
///   the verdict in `signals` and ASN/org in `network`; the evaluate style puts the
///   verdict IN `network`. Reading only one shape returned nothing for every address.
///   Which shape it is has to be detected by looking for boolean members, not assumed.
///
///   BOOLEANS ARE ENCODED LOOSELY. true, 1, "true", "yes" all appear. Accepting only a
///   real boolean made a body full of 0/1 look like it had no verdict in it.
///
///   AN ADDRESS WITH NO REPUTATION DATA STILL HAS A VERDICT. `known: false` comes back
///   with null signals and an explicit allow/review/block. Taking the verdict is right;
///   inferring the per-category fields from it is not - their overall call is known, but
///   not that the address is specifically "not a proxy".
///
/// Returns null ONLY when nothing recognisable is present. Never a guess.
/// </remarks>
public static class SentinelParser
{
    /// <summary>
    /// Coerce a loosely-encoded boolean. Null means ABSENT OR UNRECOGNISED, never false.
    /// </summary>
    public static bool? Boolish(JsonElement? value)
    {
        if (value is not { } v) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return v.TryGetDouble(out var n) ? n != 0 : null;
            case JsonValueKind.String:
                return v.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => true,
                    "false" or "no" or "n" or "0" => false,
                    _ => null,
                };
            default: return null;   // including Null - which is UNKNOWN, not false
        }
    }

    private static JsonElement? Member(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) ? value : null;

    /// <summary>The first recognisable boolean among several candidate locations.</summary>
    private static bool? FirstBool(params JsonElement?[] candidates)
    {
        foreach (var candidate in candidates)
            if (Boolish(candidate) is { } answer) return answer;
        return null;
    }

    private static string? Text(JsonElement parent, string name)
    {
        var value = Member(parent, name);
        if (value is null) return null;
        var text = value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.ToString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static double? Number(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Member(parent, name);
            if (value?.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var n)) return n;
            if (value?.ValueKind == JsonValueKind.String &&
                double.TryParse(value.Value.GetString(), CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    /// <summary>These keys appearing as booleans in `network` mean it is the EVALUATE shape.</summary>
    private static readonly string[] VerdictKeys = ["vpn", "proxy", "datacenter", "tor", "anonymous", "residential"];

    /// <param name="unrecognised">
    /// Set when the body parsed but held nothing usable, carrying the detail needed to fix
    /// it. The keys alone were never enough to spot that the booleans were encoded as
    /// something else, so the VALUES go in the message too.
    /// </param>
    public static DetectorReading? Parse(JsonElement root, out string? unrecognised)
    {
        unrecognised = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            unrecognised = $"body is {root.ValueKind}, not an object";
            return null;
        }

        var signals = Member(root, "signals") ?? default;
        var network = Member(root, "network") ?? default;

        // Which shape is this? `network` carries the verdict booleans in the evaluate
        // style; in the lookup style those keys are absent and it carries ASN/org.
        var networkIsVerdict = network.ValueKind == JsonValueKind.Object &&
                               VerdictKeys.Any(k => Boolish(Member(network, k)) is not null);
        var verdictScope = networkIsVerdict ? network : default;

        var vpn = FirstBool(Member(signals, "vpn"), Member(verdictScope, "vpn"), Member(root, "vpn"));
        var proxy = FirstBool(Member(signals, "proxied"), Member(signals, "proxy"), Member(verdictScope, "proxy"), Member(root, "proxy"));
        var tor = FirstBool(Member(signals, "tor"), Member(verdictScope, "tor"), Member(root, "tor"));
        var datacenter = FirstBool(Member(signals, "dch"), Member(signals, "datacenter"),
                                   Member(verdictScope, "datacenter"), Member(root, "datacenter"), Member(root, "hosting"));
        var anonymous = FirstBool(Member(signals, "anon"), Member(signals, "anonymous"),
                                  Member(verdictScope, "anonymous"), Member(root, "anonymous"));

        var noSignals = vpn is null && proxy is null && tor is null && datacenter is null && anonymous is null;
        var risk = Number(root, "risk_score", "riskScore");
        var verdict = (Text(root, "verdict") ?? Text(root, "decision"))?.ToLowerInvariant();

        // ASN/org only exist in the LOOKUP-style body, where `network` is not the verdict.
        var geoScope = networkIsVerdict ? default : network;

        if (noSignals && verdict is "allow" or "review" or "block")
        {
            var known = Boolish(Member(root, "known"));
            return new DetectorReading
            {
                Name = "sentinel",
                Tier = 1,
                Flagged = verdict is "block" or "review",
                Detail = $"verdict:{verdict}" +
                         (risk is { } r ? $" risk:{r:0}" : "") +
                         (known == false ? " (no reputation data)" : ""),
                /* Every per-category field stays UNKNOWN. Their overall call is known; that
                   the address is specifically "not a proxy" is not, and asserting it would
                   let this outvote a provider that actually classified the address. */
                Vpn = null, Proxy = null, Tor = null, Hosting = null, Residential = null,
                ThreatScore = risk,
                Asn = VpnMerge.NormaliseAsn(Text(network, "asn")),
                Organization = Text(network, "org"),
                Isp = Text(network, "org"),
                Country = Text(network, "country"),
                CountryCode = CountryCode(Text(network, "country")),
                City = Text(network, "city"),
            };
        }

        if (noSignals)
        {
            var error = Text(root, "error");
            unrecognised =
                $"unrecognised body - no verdict. Top-level keys: [{string.Join(", ", root.EnumerateObject().Select(p => p.Name))}]" +
                $"; signals={Sample(signals)}; network={Sample(network)}" +
                (error is not null ? $"; error: {error}" : "");
            return null;
        }

        /* Anonymising signals only. Datacenter is REPORTED but is not a hit on its own -
           the same rule ipapi.is follows, since honest players sit behind carrier and
           cloud ranges. */
        var hits = new[] { ("vpn", vpn), ("proxy", proxy), ("tor", tor), ("anon", anonymous) }
            .Where(h => h.Item2 == true).Select(h => h.Item1).ToList();

        return new DetectorReading
        {
            Name = "sentinel",
            Tier = 1,
            Flagged = hits.Count > 0,
            Detail = (hits.Count > 0 ? string.Join("+", hits) : "clean") +
                     (datacenter == true ? " (datacenter)" : "") +
                     (verdict is not null ? $" verdict:{verdict}" : "") +
                     (risk is { } r2 ? $" risk:{r2:0}" : ""),
            Vpn = vpn, Proxy = proxy, Tor = tor,
            Hosting = datacenter,
            /* The evaluate-style body states residential outright - trust it. Otherwise the
               only thing inferable is that a datacenter address is not residential;
               anything else stays null so this never outvotes a real classifier. */
            Residential = Boolish(Member(verdictScope, "residential")) ?? (datacenter == true ? false : null),
            ThreatScore = risk,
            // The consumer brand ("PROTON_VPN") - evaluate-style body only.
            Provider = Text(verdictScope, "service") ?? Text(root, "service"),
            Asn = VpnMerge.NormaliseAsn(Text(geoScope, "asn")),
            Organization = Text(geoScope, "org"),
            Isp = Text(geoScope, "org"),
            Country = Text(geoScope, "country") ?? Text(root, "country"),
            CountryCode = CountryCode(Text(geoScope, "country")),
            City = Text(geoScope, "city"),
        };
    }

    private static string? CountryCode(string? country) =>
        country is { Length: 2 } ? country.ToUpperInvariant() : null;

    private static string Sample(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return "absent";
        var text = element.GetRawText();
        return text.Length > 200 ? text[..200] : text;
    }
}
