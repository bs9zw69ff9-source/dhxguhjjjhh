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

        /* ROUNDED UP, NEVER "0m". A ban with fifty seconds left is still a ban, and
           remaining.Minutes truncates it to 0. That string is not cosmetic: the ban file
           written from it is read back by the importer, and ParseBanSpan rejects a zero
           quantity - so a temp ban in its final minute came back as PERMANENT. A live ban
           must not describe itself as having no time left. */
        return $"{Math.Max(1, remaining.Minutes)}m";
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

        // "d" and the bare/"m" default are the overflowing ones: FromDays(long.MaxValue)
        // throws rather than saturating, and the regex puts no ceiling on the digits.
        var unit = match.Groups[2].Value;
        return TryTicks(n, unit.Length > 0 ? unit : "m", out var ticks) ? new TimeSpan(ticks) : null;
    }

    /// <summary>
    /// The longest span either parser will produce. Longer input is UNPARSEABLE, not clamped.
    /// </summary>
    /// <remarks>
    /// A hundred years is past every real ban and short of where <see cref="TimeSpan"/>
    /// overflows, so the ceiling is reached by absurd input rather than by arithmetic.
    /// REJECTED rather than clamped because the callers already handle "cannot read this":
    /// the ban importer records it as permanent with the raw text kept in the reason, which is
    /// both the right answer for "999999999d" and one a human can correct. Silently turning it
    /// into a hundred-year ban would look deliberate.
    /// </remarks>
    public static TimeSpan MaxSpan { get; } = TimeSpan.FromDays(365 * 100);

    /// <summary>
    /// One quantity and unit as ticks, or false if it does not fit.
    /// </summary>
    /// <remarks>
    /// THE DIVISION IS THE POINT. Every one of these used to be
    /// <c>TimeSpan.FromDays(365 * n)</c>, which overflows twice over - the multiply wraps
    /// silently in a long, and FromDays THROWS rather than saturating on what survives.
    /// Measured, not assumed: "99999999d", "999999999999w" and "9999999999y" each threw
    /// OverflowException out of the parser, and "99999999999999999999d" threw out of
    /// long.Parse before that. Every one is reachable - from a moderator's typo in /tempban,
    /// and from the game's own banlist.txt, where it took out the whole import batch.
    /// Comparing against the quotient first means nothing is ever multiplied out of range.
    /// </remarks>
    private static bool TryTicks(long n, string unit, out long ticks)
    {
        ticks = 0;
        if (n < 0) return false;

        var perUnit = unit switch
        {
            "s" => TimeSpan.TicksPerSecond,
            "m" => TimeSpan.TicksPerMinute,
            "h" => TimeSpan.TicksPerHour,
            "d" => TimeSpan.TicksPerDay,
            "w" => TimeSpan.TicksPerDay * 7,
            "mo" => TimeSpan.TicksPerDay * 30,
            "y" => TimeSpan.TicksPerDay * 365,
            _ => 0L,
        };
        if (perUnit == 0) return false;

        if (n > MaxSpan.Ticks / perUnit) return false;
        ticks = n * perUnit;
        return true;
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

        var total = 0L;
        foreach (Match part in parts)
        {
            var unit = SpanUnit.Match(part.Value);

            /* TryParse, not Parse: the regex is `\d+` with no ceiling, so twenty digits get
               here intact and long.Parse threw. Unreadable rather than fatal - the importer
               already treats that as permanent and keeps the raw text in the reason. */
            if (!long.TryParse(unit.Groups[1].Value, CultureInfo.InvariantCulture, out var n)) return null;
            if (!TryTicks(n, unit.Groups[2].Value, out var ticks)) return null;

            // "3d 4h 9999999999y" must not wrap back into a plausible-looking span.
            if (total > MaxSpan.Ticks - ticks) return null;
            total += ticks;
        }
        return total == 0 ? null : new TimeSpan(total);
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
