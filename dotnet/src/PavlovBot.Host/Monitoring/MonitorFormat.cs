using System.Globalization;
using Discord;
using PavlovBot.Core.Monitoring;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Monitoring;

/// <summary>
/// The one place monitoring state is turned into the little strings and colours the UI shows.
/// </summary>
/// <remarks>
/// SHARED SO THE BOARD AND THE COMMAND CANNOT DRIFT. <c>/monitor status</c> and the live board
/// both render the same health, and a state dot or a CPU figure formatted two subtly different
/// ways in two files is exactly the kind of rot this repo already carries scars from. One state,
/// one rendering.
/// </remarks>
internal static class MonitorFormat
{
    /// <summary>Whether the server is answering RCON at all (ONLINE or DEGRADED, not down).</summary>
    public static bool Answering(HealthState state) => state is HealthState.Online or HealthState.Degraded;

    public static string StateDot(HealthState state) => state switch
    {
        HealthState.Online => "🟢",
        HealthState.Degraded => "🟡",
        HealthState.Reconnecting => "🟠",
        HealthState.Offline => "🔴",
        _ => "⚪",
    };

    public static Color StateColour(HealthState state) => state switch
    {
        HealthState.Online => Theme.Green,
        HealthState.Degraded => Theme.Amber,
        HealthState.Reconnecting => Theme.Amber,
        HealthState.Offline => Theme.BanRed,
        _ => Theme.Grey,
    };

    /// <summary>The dot for a severity, for the recent-events list.</summary>
    public static string SeverityDot(Severity severity) => severity switch
    {
        Severity.Critical => "🔴",
        Severity.Warning => "🟡",
        _ => "🟢",
    };

    public static string Tick(bool ok) => ok ? "🟢" : "🔴";

    public static string Unknownable(bool? state) => state switch { true => "🟢", false => "🔴", null => "⚪" };

    public static string Ms(TimeSpan t) => $"{t.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture)}ms";

    public static string Cpu(UnitStats u) => u.CpuPercent is { } c ? $"{c.ToString("0.0", CultureInfo.InvariantCulture)}%" : "—";

    public static string Ram(UnitStats u)
    {
        if (u.MemoryBytes is not { } bytes) return "—";
        var used = $"{(bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture)} MB";
        // A limit is shown only when systemd actually caps the unit; unset reads as "infinity"
        // and comes back null, so an uncapped server shows just its usage, not "/ ∞".
        return u.MemoryLimitBytes is { } limit && limit > 0
            ? $"{used} / {(limit / 1048576.0).ToString("0", CultureInfo.InvariantCulture)} MB"
            : used;
    }
}
