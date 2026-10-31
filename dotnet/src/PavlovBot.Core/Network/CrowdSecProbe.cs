using System.Text.RegularExpressions;

namespace PavlovBot.Core.Network;

/// <summary>A line that must parse, built for one log source, and what its failing to parse would mean.</summary>
/// <param name="Line">The probe, in the source's own timestamp format; null when <paramref name="Problem"/> already says what is wrong.</param>
/// <param name="Meaning">What CrowdSec would miss if the probe does not parse.</param>
/// <param name="Problem">Set when the file itself shows the fault, without running anything.</param>
public sealed partial record CrowdSecProbe(string? Line, string Meaning, string? Problem = null)
{
    /// <summary>A documentation address (RFC 5737): never a real attacker, never a real ban.</summary>
    private const string ProbeAddress = "192.0.2.1";

    private const string SshLine = $"sshd[1]: Invalid user pavlovbot-probe from {ProbeAddress} port 1";

    private const string UfwLine =
        $"kernel: [UFW BLOCK] IN=eth0 OUT= MAC= SRC={ProbeAddress} DST=192.0.2.2 LEN=40 TOS=0x00 PREC=0x00 TTL=64 ID=1 PROTO=TCP SPT=1 DPT=22 WINDOW=1024 RES=0x00 SYN URGP=0";

    /// <summary>
    /// The probe for a source that has read lines but parsed none, or null when there is no known
    /// line that source should parse - then "none parsed" says nothing either way.
    /// </summary>
    /// <remarks>
    /// WHY A PROBE. "Read, none parsed" is the normal state of a quiet auth.log: cron and sudo
    /// sessions, which no parser wants, are most of it, and only failed logins parse. What does
    /// show a broken parser is a line that should match - a failed SSH login, a ufw block - in the
    /// file's own timestamp format failing to. That is the failure that matters (a changed log
    /// format, a parser removed by a hub upgrade), and it is the one this tests.
    /// </remarks>
    /// <param name="source">The acquisition source as cscli names it, e.g. <c>file:/var/log/auth.log</c>.</param>
    /// <param name="lastLine">The newest line of that file, for its timestamp format; null if unread.</param>
    public static CrowdSecProbe? For(string source, string? lastLine)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.StartsWith("file:", StringComparison.Ordinal)) return null;
        var name = Path.GetFileName(source["file:".Length..]);

        var (message, meaning) =
            name.Contains("auth", StringComparison.Ordinal) || name.Contains("secure", StringComparison.Ordinal)
                ? (SshLine, "failed SSH logins are not seen, so SSH brute force is never banned")
            : name.Contains("kern", StringComparison.Ordinal) || name.Contains("ufw", StringComparison.Ordinal) || name == "syslog"
                ? (UfwLine, "ufw's blocked packets are not seen, so port scans are never banned")
            : (null, null);
        if (message is null) return null;

        // No line to copy the format from: the parser can still be tested in the default format.
        if (string.IsNullOrWhiteSpace(lastLine)) return new CrowdSecProbe("2026-01-01T00:00:00.000000+00:00 host " + message, meaning!);

        var m = SyslogPrefix().Match(lastLine);
        return m.Success
            ? new CrowdSecProbe(m.Value + message, meaning!)
            : new CrowdSecProbe(null, meaning!, $"its lines are not in a syslog format CrowdSec reads, so {meaning}");
    }

    /// <summary>
    /// Both formats rsyslog writes: RFC 3339 (Ubuntu 24.04's default, <c>2026-10-31T21:40:02.123456+00:00 host </c>)
    /// and the traditional one (<c>Oct  1 21:40:02 host </c>). The prefix is copied as written.
    /// </summary>
    [GeneratedRegex(@"^(?:\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)|[A-Z][a-z]{2} [ \d]\d \d\d:\d\d:\d\d) \S+ ",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SyslogPrefix();
}
