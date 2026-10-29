using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PavlovBot.Core.Network;

/// <summary>One active CrowdSec decision.</summary>
/// <param name="Origin"><c>crowdsec</c> (a local scenario), <c>cscli</c> (by hand, or by this bot), <c>CAPI</c>/<c>lists</c> (community).</param>
/// <param name="Scenario">What triggered it, e.g. <c>crowdsecurity/ssh-bf</c>, or the reason given with <c>cscli decisions add</c>.</param>
public sealed record CrowdSecDecision(long Id, string Value, string Scope, string Type, string Origin, string Scenario, DateTimeOffset? Until)
{
    /// <summary>Whether this bot placed it: its reasons start with <see cref="CrowdSecOutput.BotReason"/>.</summary>
    public bool FromBot => Scenario.StartsWith(CrowdSecOutput.BotReason, StringComparison.Ordinal);
}

/// <summary>One CrowdSec alert: what a scenario saw, and whether it led to a decision.</summary>
public sealed record CrowdSecAlert(long Id, string Scenario, string? Source, DateTimeOffset Created, int Events, int Decisions);

/// <summary>A registered bouncer, and when it last fetched decisions.</summary>
public sealed record CrowdSecBouncer(string Name, DateTimeOffset? LastPull, bool Revoked, string Version);

/// <summary>One acquisition source's line counts: zero parsed out of many read is a broken parser.</summary>
public sealed record CrowdSecSource(string Name, long Reads, long Parsed, long Unparsed)
{
    public bool Failing => Reads > 0 && Parsed == 0;
}

/// <summary>
/// Reading <c>cscli ... -o json</c>.
/// </summary>
/// <remarks>
/// AGAINST WHAT CSCLI ACTUALLY PRINTS. The shapes here were taken from CrowdSec 1.8.2's own output
/// (the fixtures in the tests). Anything else - an older version's shape, an error message, an
/// empty list, which cscli prints as <c>null</c> - reads as empty, never as an exception.
/// </remarks>
public static partial class CrowdSecOutput
{
    /// <summary>Every decision this bot adds starts its reason with this, so its own bans are told apart.</summary>
    public const string BotReason = "pavlovbot:";

    /// <summary>The allowlist this bot keeps the owner's trusted addresses in.</summary>
    public const string Allowlist = "pavlovbot";

    /// <summary>Active decisions, from <c>cscli decisions list -o json</c> (a list of alerts, each with its decisions).</summary>
    public static IReadOnlyList<CrowdSecDecision> Decisions(string? json, DateTimeOffset now)
    {
        var found = new List<CrowdSecDecision>();
        foreach (var alert in Array(json))
        {
            if (!alert.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array) continue;
            foreach (var d in decisions.EnumerateArray())
            {
                if (!d.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
                var until = Str(d, "duration") is { } duration && Duration(duration) is { } left ? now + left : (DateTimeOffset?)null;
                found.Add(new CrowdSecDecision(id, Str(d, "value") ?? "", Str(d, "scope") ?? "", Str(d, "type") ?? "",
                    Str(d, "origin") ?? "", Str(d, "scenario") ?? "", until));
            }
        }
        return found;
    }

    /// <summary>Alerts, from <c>cscli alerts list -o json</c>.</summary>
    public static IReadOnlyList<CrowdSecAlert> Alerts(string? json)
    {
        var found = new List<CrowdSecAlert>();
        foreach (var a in Array(json))
        {
            if (!a.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
            var created = Str(a, "created_at") is { } c &&
                          DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
            var source = a.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? Str(s, "value") : null;
            var events = a.TryGetProperty("events_count", out var e) && e.TryGetInt32(out var n) ? n : 0;
            var decisions = a.TryGetProperty("decisions", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0;
            found.Add(new CrowdSecAlert(id, Str(a, "scenario") ?? "", source, created, events, decisions));
        }
        return found;
    }

    /// <summary>Bouncers, from <c>cscli bouncers list -o json</c>.</summary>
    public static IReadOnlyList<CrowdSecBouncer> Bouncers(string? json) =>
        [.. Array(json).Select(b => new CrowdSecBouncer(
            Str(b, "name") ?? "",
            Str(b, "last_pull") is { } pull && DateTimeOffset.TryParse(pull, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null,
            b.TryGetProperty("revoked", out var r) && r.ValueKind == JsonValueKind.True,
            Str(b, "version") ?? ""))];

    /// <summary>Acquisition sources, from <c>cscli metrics show acquisition -o json</c>.</summary>
    public static IReadOnlyList<CrowdSecSource> Acquisition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("acquisition", out var acquisition) || acquisition.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            return [.. acquisition.EnumerateObject().Select(p => new CrowdSecSource(
                p.Name, Num(p.Value, "reads"), Num(p.Value, "parsed"), Num(p.Value, "unparsed")))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A Go duration as cscli prints it: <c>3h59m48s</c>, <c>59m59.5s</c>, <c>-5s</c> (expired).
    /// </summary>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = GoDuration().Match(text.Trim());
        if (!match.Success) return null;

        double Part(string name) => match.Groups[name].Success
            ? double.Parse(match.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;

        var span = TimeSpan.FromHours(Part("h")) + TimeSpan.FromMinutes(Part("m")) + TimeSpan.FromSeconds(Part("s")) +
                   TimeSpan.FromMilliseconds(Part("ms"));
        return match.Groups["neg"].Success ? -span : span;
    }

    [GeneratedRegex(@"^(?<neg>-)?(?:(?<h>\d+)h)?(?:(?<m>\d+)m(?!s))?(?:(?<s>\d+(?:\.\d+)?)s)?(?:(?<ms>\d+(?:\.\d+)?)ms)?$")]
    private static partial Regex GoDuration();

    /// <summary>A duration for <c>cscli decisions add --duration</c>: whole hours or minutes, which every version accepts.</summary>
    public static string DurationArgument(TimeSpan span) =>
        span.TotalMinutes % 60 == 0
            ? ((long)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
            : ((long)Math.Ceiling(span.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + "m";

    private static JsonElement[] Array(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
