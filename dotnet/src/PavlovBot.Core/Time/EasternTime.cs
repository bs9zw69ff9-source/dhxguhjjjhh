using System.Globalization;
using System.Text.RegularExpressions;

namespace PavlovBot.Core.Time;

/// <summary>
/// Every human-readable timestamp the bot prints, in the server's local time.
/// </summary>
/// <remarks>
/// America/New_York, DST-aware, so it reads EST in winter and EDT in summer. This is not a
/// preference - printing UTC is what made the connection feed look four to five hours off,
/// and a ban expiry shown in the wrong hour is a support ticket.
///
/// Discord's own <c>&lt;t:…:R&gt;</c> markers are deliberately left alone elsewhere in the
/// bot: those auto-localise per viewer, which is better than any fixed zone.
/// </remarks>
public static partial class EasternTime
{
    /// <summary>
    /// The zone, resolved once.
    /// </summary>
    /// <remarks>
    /// Throws at first use if the tzdata is missing rather than silently falling back to
    /// UTC. A fallback would make every printed time wrong by up to five hours with
    /// nothing anywhere saying so; a startup failure is recoverable, silent drift is not.
    /// (The host also health-checks this, so the failure is visible before it matters.)
    /// </remarks>
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateTimeOffset Now(TimeProvider? time = null) =>
        TimeZoneInfo.ConvertTime((time ?? TimeProvider.System).GetUtcNow(), Zone);

    public static DateTimeOffset At(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    /// <summary>"YYYY-MM-DD HH:MM:SS" Eastern, or "unknown" for a never/invalid timestamp.</summary>
    /// <remarks>
    /// A zero or absent timestamp means NEVER SEEN, and rendering it as 1970-01-01 is a
    /// lie that reads as real data. "unknown" is the honest answer and is visibly not a date.
    /// </remarks>
    public static string Stamp(DateTimeOffset? instant) =>
        instant is null || instant.Value.ToUnixTimeMilliseconds() <= 0
            ? "unknown"
            : At(instant.Value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The Eastern calendar day, not the UTC one.</summary>
    public static string Date(DateTimeOffset? instant)
    {
        var stamp = Stamp(instant);
        return stamp == "unknown" ? stamp : stamp[..10];
    }

    /// <summary>Wall clock as the day key and "HH:MM", for the scheduled map rotation.</summary>
    public static (string Date, string HourMinute) Clock(DateTimeOffset? instant = null, TimeProvider? time = null)
    {
        var local = At(instant ?? (time ?? TimeProvider.System).GetUtcNow());
        return (local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                local.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// How long is left, coarsely: "3d 4h", "5h 20m", "12m", or "expired".
    /// </summary>
    /// <remarks>
    /// Two units, never three. A ban with "2d 3h 17m 4s" left invites someone to read the
    /// seconds as meaningful; the moderator only ever needs the magnitude.
    /// </remarks>
    public static string TimeLeft(DateTimeOffset expires, TimeProvider? time = null)
    {
        var remaining = expires - (time ?? TimeProvider.System).GetUtcNow();
        if (remaining <= TimeSpan.Zero) return "expired";

        if (remaining.Days > 0) return $"{remaining.Days}d {remaining.Hours}h";
        if (remaining.Hours > 0) return $"{remaining.Hours}h {remaining.Minutes}m";
        return $"{remaining.Minutes}m";
    }

    [GeneratedRegex(@"^(\d+)\s*(s|m|h|d)?$")]
    private static partial Regex SimpleDuration { get; }

    /// <summary>
    /// "30s", "10m", "2h", "1d" - a bare number means MINUTES. Null when unparseable.
    /// </summary>
    /// <remarks>
    /// Used for short operational spans (a mute, a sentence). Bans use
    /// <see cref="ParseBanSpan"/>, which understands compound spans and longer units.
    /// </remarks>
    public static TimeSpan? ParseDuration(string? raw)
    {
        var match = SimpleDuration.Match((raw ?? "").Trim().ToLowerInvariant());
        if (!match.Success) return null;
        if (!long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var n) || n == 0) return null;

        return match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            _ => TimeSpan.FromMinutes(n),   // bare number, and "m"
        };
    }

    // "mo" must be matched before the single letters or "1mo" reads as one MINUTE.
    [GeneratedRegex(@"\d+\s*(?:mo|[smhdwy])")]
    private static partial Regex SpanPart { get; }

    [GeneratedRegex(@"^(\d+)\s*(mo|[smhdwy])$")]
    private static partial Regex SpanUnit { get; }

    /// <summary>
    /// A ban length as a compound span: "1d", "3d 4h", "1w", "1mo", "1y".
    /// </summary>
    /// <remarks>
    /// SPANS ONLY - never a calendar date. "until friday" and "2026-08-01" are rejected
    /// rather than guessed at, because a moderator who types a date and gets a length is
    /// worse off than one who gets an error. Anything left over after the span parts are
    /// removed means the input was not purely a span.
    ///
    /// A month is 30 days and a year is 365. Both are approximations, and deliberately so:
    /// calendar arithmetic here would make "1mo" mean a different length depending on when
    /// it was issued, which nobody wants to explain during an appeal.
    /// </remarks>
    public static TimeSpan? ParseBanSpan(string? raw)
    {
        var text = (raw ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return null;

        var parts = SpanPart.Matches(text);
        if (parts.Count == 0) return null;
        if (SpanPart.Replace(text, "").Trim().Length > 0) return null;

        var total = TimeSpan.Zero;
        foreach (Match part in parts)
        {
            var unit = SpanUnit.Match(part.Value);
            var n = long.Parse(unit.Groups[1].Value, CultureInfo.InvariantCulture);
            total += unit.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                "d" => TimeSpan.FromDays(n),
                "w" => TimeSpan.FromDays(7 * n),
                "mo" => TimeSpan.FromDays(30 * n),
                "y" => TimeSpan.FromDays(365 * n),
                _ => TimeSpan.Zero,
            };
        }
        return total == TimeSpan.Zero ? null : total;
    }

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))?(am|pm)?$")]
    private static partial Regex ClockTime { get; }

    /// <summary>"18:30", "6:30pm", "3pm", "0:00" - normalised to 24-hour "HH:MM", or null.</summary>
    public static string? ParseClockTime(string? raw)
    {
        var text = new string(((raw ?? "").Trim().ToLowerInvariant()).Where(c => !char.IsWhiteSpace(c)).ToArray());
        var match = ClockTime.Match(text);
        if (!match.Success) return null;

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59) return null;

        var meridiem = match.Groups[3].Value;
        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12) return null;
            if (meridiem == "pm" && hour != 12) hour += 12;
            if (meridiem == "am" && hour == 12) hour = 0;
        }
        else if (hour > 23) return null;

        return $"{hour:00}:{minute:00}";
    }
}

/// <summary>The <c>/tempban</c> duration picker.</summary>
/// <remarks>
/// The labels are COMPACT SPANS ("1d", "1mo"), never dates. A moderator picks a LENGTH,
/// which is the thing they actually have an opinion about; the expiry date is derived.
/// </remarks>
public static class BanDurations
{
    public static readonly IReadOnlyList<(string Value, TimeSpan Length)> All =
    [
        ("1h", TimeSpan.FromHours(1)),
        ("6h", TimeSpan.FromHours(6)),
        ("1d", TimeSpan.FromDays(1)),
        ("3d", TimeSpan.FromDays(3)),
        ("5d", TimeSpan.FromDays(5)),
        ("1w", TimeSpan.FromDays(7)),
        ("2w", TimeSpan.FromDays(14)),
        ("1mo", TimeSpan.FromDays(30)),
        ("3mo", TimeSpan.FromDays(90)),
        ("6mo", TimeSpan.FromDays(180)),
        ("1y", TimeSpan.FromDays(365)),
    ];

    /// <summary>The sentinel the picker uses for a ban that never expires.</summary>
    public const string Permanent = "permanent";

    /// <summary>
    /// Resolve a picker value to a length. Null means PERMANENT or unrecognised - the
    /// caller must distinguish those, which is why this does not return a zero span.
    /// </summary>
    public static TimeSpan? Length(string? value)
    {
        if (value is null) return null;
        foreach (var (key, length) in All)
            if (string.Equals(key, value, StringComparison.OrdinalIgnoreCase)) return length;
        // Fall back to the free-text parser so a typed "3d 4h" works wherever a pick does.
        return EasternTime.ParseBanSpan(value);
    }
}
