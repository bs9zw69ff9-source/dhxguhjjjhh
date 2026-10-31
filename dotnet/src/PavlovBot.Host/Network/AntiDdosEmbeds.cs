using System.Globalization;
using System.Text;
using Discord;
using PavlovBot.Core.Network;
using PavlovBot.Core.Text;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Network;

/// <summary>
/// The anti-DDoS alerts posted to the alert channel.
/// </summary>
/// <remarks>
/// ADDRESSES ARE SHOWN, so the alert channel must be staff-only (<c>ANTIDDOS_ALERT_CHANNEL</c>, or
/// <c>ATTACK_CHANNEL</c>). A player's address is marked as one, and nothing else from the logs -
/// no packet contents, no other players - is ever posted.
///
/// ONE EMBED A MINUTE AT MOST per kind: the loop batches a minute's detections and decisions, and
/// each list is cut at <see cref="MaxRows"/> with a count of the rest.
/// </remarks>
public static class AntiDdosEmbeds
{
    internal const int MaxRows = 10;

    public static EmbedBuilder Detections(DateTimeOffset at, IReadOnlyList<(PlannedResponse Plan, string? Problem)> items, AntiDdosOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        var bans = items.Count(i => i.Plan.Ban is not null && i.Problem is null);
        var permanent = items.Count(i => i.Plan.Permanent && i.Problem is null);
        var b = new StringBuilder();
        foreach (var (plan, problem) in items.Take(MaxRows))
        {
            var what = string.Join(", ", plan.Detections.Select(d => d.Reason));
            var action = plan.Ban is { } ban
                ? problem is not null ? $"ban FAILED: {Sanitize.Code(problem)}"
                : plan.Permanent ? "**banned permanently** (part of a flood)"
                : $"**banned {Span(ban)}** (until {Discord(at + ban)})"
                : "watching";
            b.AppendLine(CultureInfo.InvariantCulture, $"`{plan.Source}` · {what} · {action}");
        }
        if (items.Count > MaxRows) b.AppendLine(CultureInfo.InvariantCulture, $"… and {items.Count - MaxRows} more - `/antiddos detections`");

        var title = permanent > 0 ? $"{permanent} flood source(s) permanently banned{(bans > permanent ? $", {bans - permanent} more banned" : "")}"
            : bans > 0 ? $"{bans} address(es) banned for repeated abuse"
            : $"{items.Count} suspicious source(s)";
        var rule = permanent > 0
            ? "Detected during a flood: banned at once, permanently · /antiddos unban lifts one"
            : $"Banned after {options.EscalateAfter} detection minutes in {options.CorrelationMinutes} min";
        return (bans > 0 ? Theme.Warning(title) : Theme.Notice(title))
            .WithDescription(b.ToString())
            .WithFooter(string.Create(CultureInfo.InvariantCulture,
                $"{rule} · players and trusted are never auto-banned · {AntiDdosRecords.Stamp(at)}"));
    }

    public static EmbedBuilder CrowdSecDecisions(IReadOnlyList<CrowdSecDecision> decisions, IReadOnlySet<string> players)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(players);
        var b = new StringBuilder();
        foreach (var d in decisions.Take(MaxRows))
        {
            var player = players.Contains(d.Value) ? " ⚠️ **known player**" : "";
            b.AppendLine(CultureInfo.InvariantCulture,
                $"`{d.Value}` · {d.Type} · {Sanitize.Code(d.Scenario)}{(d.Until is { } u ? $" · until {Discord(u)}" : "")}{player}");
        }
        if (decisions.Count > MaxRows) b.AppendLine(CultureInfo.InvariantCulture, $"… and {decisions.Count - MaxRows} more - `/antiddos crowdsec`");

        var anyPlayer = decisions.Any(d => players.Contains(d.Value));
        return (anyPlayer ? Theme.Warning($"CrowdSec: {decisions.Count} new decision(s)") : Theme.Notice($"CrowdSec: {decisions.Count} new decision(s)"))
            .WithDescription(b.ToString())
            .WithFooter(anyPlayer ? "A known player was banned - check it with /antiddos history ip:<address>, lift it with /antiddos unban" : "Enforced by the CrowdSec bouncer");
    }

    public static EmbedBuilder Storage(StorageVerdict verdict, StorageLevel previous)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        var effect = verdict.Level switch
        {
            StorageLevel.Critical => "Packet logging is OFF and the capture is paused. Filtering and the event log carry on.",
            StorageLevel.Pressure => "Logging is cut to the minimum, the capture is paused, and the oldest rotated logs are being removed. Filtering is unchanged.",
            StorageLevel.Warning => "Nothing is changed yet. Free space or raise ANTIDDOS_LOG_MAX_MB before it reaches its limit.",
            _ => "Logging is back to normal.",
        };
        var title = $"Anti-DDoS storage: {verdict.Level.ToString().ToLowerInvariant()} (was {previous.ToString().ToLowerInvariant()})";
        return (verdict.Level >= StorageLevel.Pressure ? Theme.Failure(title) : verdict.Level == StorageLevel.Warning ? Theme.Warning(title) : Theme.Success(title))
            .WithDescription($"{verdict.Reason}\n{effect}");
    }

    internal static string Span(TimeSpan span) =>
        span >= TimeSpan.FromDays(3000) ? "permanently"
        : span.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"for {span.TotalHours:0.#}h")
        : string.Create(CultureInfo.InvariantCulture, $"for {span.TotalMinutes:0}m");

    /// <summary>A Discord timestamp: shown in each reader's own time zone.</summary>
    internal static string Discord(DateTimeOffset t) =>
        string.Create(CultureInfo.InvariantCulture, $"<t:{t.ToUnixTimeSeconds()}:f>");
}
