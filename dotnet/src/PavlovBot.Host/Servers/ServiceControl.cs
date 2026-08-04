using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Servers;

/// <param name="Ok">The action succeeded. False means it did not, for the reason given.</param>
/// <param name="Unit">The systemd unit this concerns.</param>
/// <param name="Detail">stderr/stdout, or the reason nothing ran. Never empty.</param>
public sealed record UnitResult(bool Ok, string Unit, string Detail);

/// <summary>What to do to a unit. The verb passed to systemctl, and nothing else.</summary>
/// <remarks>
/// An ENUM rather than a string, so the verb cannot come from anywhere but this list. It is
/// the second word of a privileged command line; a string parameter here would be a second
/// injection surface beside the unit name, and a smaller one to notice.
/// </remarks>
public enum UnitAction
{
    Start,
    Stop,
    Restart,
}

/// <param name="MemoryBytes">Resident memory the unit's cgroup is using. Null when unreported.</param>
/// <param name="MemoryPeakBytes">The most it has used since it started, where systemd tracks it.</param>
/// <param name="MemoryLimitBytes">The cgroup cap, or null when there is none.</param>
/// <param name="CpuPercent">
/// CPU over a short sampling window. 100% is ONE core fully used - a unit across two cores
/// reads 200%, the same convention as <c>top</c> and <c>systemd-cgtop</c>.
/// </param>
/// <param name="CpuTotal">Cumulative processor time since the unit started.</param>
public sealed record UnitStats(
    string Unit,
    UnitState State,
    long? MemoryBytes = null,
    long? MemoryPeakBytes = null,
    long? MemoryLimitBytes = null,
    double? CpuPercent = null,
    TimeSpan? CpuTotal = null,
    int? Tasks = null,
    TimeSpan? Uptime = null);

/// <summary>What systemd says a unit is doing right now.</summary>
/// <remarks>
/// <see cref="Unknown"/> is its own answer and is NOT "offline". It means the question could
/// not be asked - systemctl missing, a unit name systemd does not recognise, a query that
/// timed out - and a caller that collapses it into "down" would relabel every server the
/// moment the check itself broke.
/// </remarks>
public enum UnitState
{
    Unknown,
    Active,
    Starting,
    Inactive,
    Failed,
}

/// <summary>
/// Restarting the Pavlov game servers through systemd.
/// </summary>
/// <remarks>
/// THE UNIT NAMES NEVER COME FROM DISCORD. They are configuration, and the command picks
/// from them BY INDEX - so the worst a caller can do is choose a different configured unit.
/// This runs a privileged system command; it is the one place in the bot where an injected
/// argument would be a root-level compromise rather than a wrong ban, and an allow-list of
/// configured names is the only defence that cannot be reasoned around.
///
/// NO SHELL. <c>UseShellExecute = false</c> with an argv array, so there is no string for a
/// metacharacter to live in even if the list above were somehow wrong.
///
/// THE BOT USUALLY CANNOT DO THIS UNAIDED, and that is the failure worth designing for.
/// A bot running as <c>steam</c> gets "Access denied" from systemd, which without help reads
/// as "the command is broken". The reason is reported verbatim, with the two things that fix
/// it, because a permission problem that presents as a bug costs an evening.
/// </remarks>
public sealed class ServiceControl(
    IReadOnlyList<string> units,
    bool? useSudo,
    ILogger<ServiceControl> logger) : PavlovBot.Host.Rcon.IServerLifecycle
{
    /// <summary>
    /// Whether this server was stopped on purpose. See <c>IServerLifecycle</c>.
    /// </summary>
    /// <remarks>
    /// ONLY <c>Inactive</c> COUNTS. A failed unit is down for a reason nobody chose, so it
    /// keeps counting against health; a starting one is on its way up and will answer
    /// shortly; and <c>Unknown</c> means systemd could not be asked, which must not be
    /// allowed to excuse a genuine outage. The caller uses this to SUPPRESS an alert, so
    /// every uncertain answer has to fall on the side of still raising it.
    /// </remarks>
    public async Task<bool> IsIntentionallyStoppedAsync(string rconServerName, CancellationToken ct = default)
    {
        if (NumberFor(rconServerName) is not { } number || UnitFor(number) is not { } unit) return false;

        return await StateAsync(unit, ct).ConfigureAwait(false) is UnitState.Inactive;
    }

    /// <summary>The 1-based server number behind an RCON name: "server2" is 2.</summary>
    internal static int? NumberFor(string rconServerName) =>
        rconServerName.StartsWith("server", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(rconServerName.AsSpan("server".Length), CultureInfo.InvariantCulture, out var number) &&
        number > 0
            ? number
            : null;

    /// <summary>The configured units, in server order: index 0 is "Server 1".</summary>
    public IReadOnlyList<string> Units { get; } = units;

    /// <summary>
    /// The default unit names, in server order.
    /// </summary>
    /// <remarks>
    /// Server 1 is <c>pavlovserver</c>, server 2 is <c>pavlovserver1</c>, server 3 is
    /// <c>pavlovserver2</c> - the off-by-one is the operator's naming, not a mistake here.
    /// It is also the sequence <see cref="Storage.PavlovInstalls"/> discovers and the order
    /// the feeds number servers in, so "Server 2" means the same thing in a join line, in a
    /// player-count channel and here.
    /// </remarks>
    public static readonly string[] DefaultUnits = ["pavlovserver", "pavlovserver1", "pavlovserver2"];

    /// <summary>
    /// Whether systemctl needs <c>sudo</c> to reach root from here.
    /// </summary>
    /// <remarks>
    /// SYSTEMCTL MUST RUN AS ROOT - stopping and starting a system unit is not something an
    /// unprivileged user may do, and systemd refuses it rather than half-doing it. There are
    /// two ways to be root and the right one depends on how the bot was deployed, so this
    /// decides at runtime instead of making the operator declare it:
    ///
    ///   ALREADY ROOT (the bot's own process runs as uid 0) - call systemctl directly.
    ///   Wrapping it in sudo would work but adds a process and a failure mode for nothing.
    ///
    ///   NOT ROOT - go through <c>sudo -n</c>. <c>-n</c> is non-interactive: it fails
    ///   immediately rather than blocking forever on a password prompt that no one is
    ///   attached to answer, which would otherwise hang the command until its timeout.
    ///
    /// <c>PAVLOV_SYSTEMCTL_SUDO</c> overrides the detection in either direction, for the
    /// setups this cannot see - a container where uid 0 is not really root, or a doas/polkit
    /// arrangement where sudo is the wrong tool.
    /// </remarks>
    private readonly bool _useSudo = useSudo ?? !Environment.IsPrivilegedProcess;

    /// <summary>How the elevation resolved, for the startup line and <c>/health</c>.</summary>
    public string Elevation => _useSudo
        ? "via sudo -n (the bot is not root)"
        : Environment.IsPrivilegedProcess
            ? "directly (the bot is already root)"
            : "directly (forced by PAVLOV_SYSTEMCTL_SUDO - the bot is NOT root, so this will be refused)";

    /// <summary>
    /// Parse <c>PAVLOV_UNITS</c>. Anything that is not a plausible unit name is dropped.
    /// </summary>
    /// <remarks>
    /// Validated even though it comes from <c>.env</c> rather than from a user: this string
    /// becomes an argument to a privileged command, and a typo that smuggles a space would
    /// turn one argument into two. systemd unit names are letters, digits, and
    /// <c>- _ . @ \</c> - nothing here needs a shell character, so nothing is allowed one.
    /// </remarks>
    public static IReadOnlyList<string> ParseUnits(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return DefaultUnits;

        /* COMMA ONLY. Splitting on spaces as well looks friendlier and is wrong: it turns
           "bad name here" into three tokens that each pass validation individually, so a
           malformed entry silently becomes three units instead of being rejected as one. */
        var parsed = configured
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPlausibleUnitName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return parsed.Count > 0 ? parsed : DefaultUnits;
    }

    /// <summary>
    /// Whether this could be a systemd unit name.
    /// </summary>
    /// <remarks>
    /// IT MUST START WITH A LETTER OR DIGIT, and that clause is the one doing security work.
    /// The rest of the alphabet has to allow <c>-</c> for names like <c>pavlov-ttt</c> - but
    /// a name that BEGINS with one is not a unit, it is an OPTION. <c>--force</c> passes
    /// every "contains only safe characters" check ever written and would be handed to
    /// <c>systemctl restart</c> as a flag rather than as a target.
    ///
    /// Leading <c>.</c> and <c>@</c> go the same way: neither names a real unit, and both
    /// are the kind of thing that ends up somewhere unintended.
    /// </remarks>
    internal static bool IsPlausibleUnitName(string name) =>
        name.Length is > 0 and <= 64 &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '\\');

    /// <summary>The unit for a 1-based server number, or null when there is no such server.</summary>
    public string? UnitFor(int serverNumber) =>
        serverNumber >= 1 && serverNumber <= Units.Count ? Units[serverNumber - 1] : null;

    /// <summary>
    /// The RCON name for a 1-based server number. <c>RCON_HOST_2</c> is named "server2".
    /// </summary>
    /// <remarks>
    /// BY NAME, NEVER BY POSITION IN THE CONFIGURED LIST. RCON slots may have GAPS -
    /// <c>RCON_HOST_1</c> and <c>RCON_HOST_3</c> with no <c>_2</c> is supported - so indexing
    /// makes "server 2" mean the second CONFIGURED server rather than the second server. Kept
    /// here beside <see cref="UnitFor"/> so one type owns the whole of "what server N is":
    /// its systemd unit, its RCON name, and the label the feeds print.
    /// </remarks>
    public static string RconNameFor(int serverNumber) => $"server{serverNumber}";

    /// <summary>
    /// Whether systemd currently has this unit running.
    /// </summary>
    /// <remarks>
    /// <c>systemctl is-active</c>, NOT <c>systemctl status</c>. Status is a human-readable
    /// report - it wraps, it truncates to the terminal width, it is translated, and its
    /// layout is not a contract. Scraping "Active: active (running)" out of it works until a
    /// systemd release moves a line or a locale renames a word, and then every server reads
    /// as offline at once. <c>is-active</c> exists for exactly this question and answers it
    /// in one word on stdout.
    ///
    /// NOT ELEVATED. Querying state is read-only and works as any user, so this deliberately
    /// does not go through sudo even when the mutating calls do - otherwise a box with a
    /// sudoers entry for start/stop/restart but not for is-active would report every server
    /// offline, which is worse than not checking at all.
    /// </remarks>
    public async Task<UnitState> StateAsync(string unit, CancellationToken ct = default)
    {
        if (!IsPlausibleUnitName(unit)) return UnitState.Unknown;

        try
        {
            // A local query; anything slower is broken. ProcessRunner ends the child on the
            // deadline - this one runs on a timer, so a leak here compounds every tick.
            var run = await ProcessRunner.RunAsync(
                "systemctl", ["is-active", unit], TimeSpan.FromSeconds(5), logger, ct).ConfigureAwait(false);

            if (!run.Started || run.TimedOut) return UnitState.Unknown;

            /* The EXIT CODE is not the answer - is-active exits non-zero for anything that is
               not running, which is a perfectly good answer and not a failure. The word on
               stdout is the answer. */
            return run.Stdout.Trim() switch
            {
                "active" => UnitState.Active,
                "activating" or "reloading" => UnitState.Starting,
                "inactive" or "deactivating" => UnitState.Inactive,
                "failed" => UnitState.Failed,
                _ => UnitState.Unknown,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            /* UNKNOWN, never "offline". Not being able to ask is not the same as the answer
               being no, and callers key display decisions off this - a systemctl that cannot
               run must not relabel every server as down. */
            logger.LogDebug(ex, "Could not read the state of {Unit}", unit);
            return UnitState.Unknown;
        }
    }

    /// <summary>
    /// Memory and CPU for every unit, sampled together.
    /// </summary>
    /// <remarks>
    /// <c>systemctl show</c>, not <c>systemctl status</c>. Status renders these same numbers
    /// for a human to read - it wraps, truncates to the terminal width and is translated,
    /// and the sample the operator pasted was cut off mid-line twice. <c>show</c> emits the
    /// same data as <c>Key=Value</c> and is what systemd documents for scripting.
    ///
    /// CPU IS SAMPLED TWICE, and that is the whole reason this method takes every unit at
    /// once rather than one at a time. <c>CPUUsageNSec</c> - the field behind status's
    /// "CPU: 5.640s" - is CUMULATIVE processor time since the unit started, so printing it as
    /// "CPU usage" would show a number that only ever grows and says nothing about load. A
    /// percentage needs a delta over a known interval, so both samples are taken around ONE
    /// shared sleep: three units cost one wait rather than three.
    ///
    /// 100% IS ONE CORE, the convention <c>top</c> and <c>systemd-cgtop</c> use. A server
    /// spread over two cores reads 200%, which is the number that means "this box is
    /// working" to somebody who has ever run top.
    ///
    /// Not elevated: reading unit properties is allowed to any user, so this must not go
    /// through sudo even when the mutating verbs do.
    /// </remarks>
    public async Task<IReadOnlyList<UnitStats>> StatsAsync(CancellationToken ct = default)
    {
        var first = new Dictionary<string, (long Cpu, long At)>(StringComparer.Ordinal);
        var properties = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        foreach (var unit in Units)
        {
            var shown = await ShowAsync(unit, ct).ConfigureAwait(false);
            properties[unit] = shown;
            if (Long(shown, "CPUUsageNSec") is { } cpu) first[unit] = (cpu, Stopwatch.GetTimestamp());
        }

        /* Long enough that scheduler jitter is not most of the measurement, short enough that
           nobody notices the command hesitating. Below roughly a quarter of a second the
           answer is mostly noise. */
        if (first.Count > 0) await Task.Delay(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);

        var results = new List<UnitStats>();
        foreach (var unit in Units)
        {
            var shown = properties[unit];
            var state = StateOf(Text(shown, "ActiveState"));

            double? percent = null;
            if (first.TryGetValue(unit, out var before))
            {
                var after = await ShowAsync(unit, ct, "CPUUsageNSec").ConfigureAwait(false);
                if (Long(after, "CPUUsageNSec") is { } now)
                {
                    var elapsed = Stopwatch.GetElapsedTime(before.At).TotalNanoseconds;
                    // A restart between samples resets the counter, which would read as a
                    // huge negative. Dropped rather than shown as zero.
                    if (elapsed > 0 && now >= before.Cpu) percent = (now - before.Cpu) / elapsed * 100.0;
                }
            }

            results.Add(new UnitStats(
                unit,
                state,
                MemoryBytes: Long(shown, "MemoryCurrent"),
                MemoryPeakBytes: Long(shown, "MemoryPeak"),
                MemoryLimitBytes: Long(shown, "MemoryMax"),
                CpuPercent: percent,
                CpuTotal: Long(shown, "CPUUsageNSec") is { } total ? TimeSpan.FromSeconds(total / 1_000_000_000.0) : null,
                Tasks: Long(shown, "TasksCurrent") is { } tasks ? (int)tasks : null,
                Uptime: UptimeFrom(Long(shown, "ActiveEnterTimestampMonotonic"))));
        }

        return results;
    }

    /// <summary>
    /// How long the unit has been active, from systemd's MONOTONIC timestamp.
    /// </summary>
    /// <remarks>
    /// The monotonic one, in microseconds since boot, rather than the wall-clock
    /// <c>ActiveEnterTimestamp</c>. That one is formatted for humans - "Mon 2026-08-03
    /// 07:26:04 EDT" - so parsing it means depending on the host's locale and timezone
    /// abbreviations, which is the same trap as scraping <c>status</c>.
    /// </remarks>
    private static TimeSpan? UptimeFrom(long? activeEnterMonotonicMicroseconds)
    {
        if (activeEnterMonotonicMicroseconds is not { } since || since <= 0) return null;

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64 - (since / 1000.0));
        return uptime > TimeSpan.Zero ? uptime : null;
    }

    private static UnitState StateOf(string? activeState) => activeState switch
    {
        "active" => UnitState.Active,
        "activating" or "reloading" => UnitState.Starting,
        "inactive" or "deactivating" => UnitState.Inactive,
        "failed" => UnitState.Failed,
        _ => UnitState.Unknown,
    };

    private static string? Text(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// A numeric property, or null for the several ways systemd says "no".
    /// </summary>
    /// <remarks>
    /// <c>infinity</c> for an unset limit, <c>[not set]</c> for an unsupported one, and
    /// <c>ulong.MaxValue</c> for a counter the kernel is not accounting - all three would
    /// become absurd numbers if parsed naively, and MemoryMax is normally one of them.
    /// </remarks>
    private static long? Long(IReadOnlyDictionary<string, string> properties, string key) =>
        Text(properties, key) is { } raw &&
        long.TryParse(raw, CultureInfo.InvariantCulture, out var value) &&
        value >= 0 && value != long.MaxValue
            ? value
            : null;

    private async Task<IReadOnlyDictionary<string, string>> ShowAsync(
        string unit, CancellationToken ct, string? only = null)
    {
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!IsPlausibleUnitName(unit)) return empty;

        try
        {
            var properties = only ??
                "ActiveState,MemoryCurrent,MemoryPeak,MemoryMax,CPUUsageNSec,TasksCurrent,ActiveEnterTimestampMonotonic";

            var run = await ProcessRunner.RunAsync(
                "systemctl", ["show", unit, "--property=" + properties],
                TimeSpan.FromSeconds(5), logger, ct).ConfigureAwait(false);

            if (!run.Started || run.TimedOut) return empty;

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // Split on the FIRST '=' only: values contain them.
                var split = line.IndexOf('=', StringComparison.Ordinal);
                if (split > 0) parsed[line[..split]] = line[(split + 1)..].Trim();
            }
            return parsed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not read properties of {Unit}", unit);
            return empty;
        }
    }

    /// <summary>
    /// <c>systemctl restart &lt;unit&gt;</c>. Blocks until systemd says it is done.
    /// </summary>
    /// <remarks>
    /// Blocking rather than <c>--no-block</c> deliberately: the answer to "did the server
    /// come back" is the only part of this worth reporting, and <c>--no-block</c> returns
    /// success for having successfully queued a job that may then fail. The interaction is
    /// already deferred, so there is time to wait for the truth.
    /// </remarks>
    public Task<UnitResult> RestartAsync(string unit, CancellationToken ct = default) =>
        RunAsync(unit, UnitAction.Restart, ct);

    /// <summary>The systemctl verb for an action. The ONLY place a verb is turned into text.</summary>
    internal static string Verb(UnitAction action) => action switch
    {
        UnitAction.Start => "start",
        UnitAction.Stop => "stop",
        _ => "restart",
    };

    /// <summary>
    /// <c>systemctl &lt;verb&gt; &lt;unit&gt;</c>, as root. Blocks until systemd says it is done.
    /// </summary>
    public async Task<UnitResult> RunAsync(string unit, UnitAction action, CancellationToken ct = default)
    {
        if (!IsPlausibleUnitName(unit))
            return new UnitResult(false, unit, "not a usable systemd unit name - refused before running anything");

        var verb = Verb(action);

        var (file, argv) = _useSudo
            // -n: fail rather than block forever on a password prompt nobody can answer.
            ? ("sudo", new[] { "-n", "systemctl", verb, unit })
            : ("systemctl", new[] { verb, unit });

        /* At WARNING, always. Every one of these disconnects players or leaves a server
           down, and "who took server 2 offline and when" is a question that gets asked. */
        logger.LogWarning("systemctl {Verb} {Unit} (via {File}) - players on it will be disconnected",
            verb, unit, file);

        try
        {
            /* Generously bounded. A Pavlov server can take the better part of a minute to
               stop and come back, and a restart killed halfway leaves it down - which is
               strictly worse than waiting. */
            var run = await ProcessRunner.RunAsync(
                file, argv, TimeSpan.FromSeconds(90), logger, ct).ConfigureAwait(false);

            if (!run.Started) return new UnitResult(false, unit, $"could not start {file}");

            if (run.TimedOut)
            {
                logger.LogError("systemctl {Verb} {Unit} timed out after 90s - it may still be in progress", verb, unit);
                return new UnitResult(false, unit,
                    $"`{verb}` timed out after 90s. It may still be in progress - check `systemctl status {unit}`");
            }

            var output = run.Combined;
            var ok = run.ExitCode == 0;

            if (ok) logger.LogInformation("systemctl {Verb} {Unit} succeeded", verb, unit);
            else logger.LogError("systemctl {Verb} {Unit} FAILED (exit {Code}): {Output}", verb, unit, run.ExitCode, output);

            return new UnitResult(ok, unit,
                output.Length > 0 ? output : ok ? $"{verb} ok" : $"exit code {run.ExitCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not invoke {File} for {Unit}", file, unit);
            return new UnitResult(false, unit, ex.Message);
        }
    }

    /// <summary>
    /// What to do about a failure, in the words of the thing to change.
    /// </summary>
    /// <remarks>
    /// A permission error here is the expected case, not the exotic one - the bot runs as an
    /// unprivileged user and systemd is refusing it, which is correct behaviour from both.
    /// Saying so, with the sudoers line to paste, is the difference between a two-minute fix
    /// and an evening spent believing the command is broken.
    /// </remarks>
    public string? Advice(IReadOnlyCollection<UnitResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count == 0) return null;

        var text = string.Join(" ", failed.Select(f => f.Detail)).ToLowerInvariant();

        if (text.Contains("access denied", StringComparison.Ordinal) ||
            text.Contains("permission denied", StringComparison.Ordinal) ||
            text.Contains("interactive authentication required", StringComparison.Ordinal) ||
            text.Contains("password is required", StringComparison.Ordinal) ||
            text.Contains("a terminal is required", StringComparison.Ordinal))
        {
            var permitted = string.Join(", ", Units
                .SelectMany(u => new[] { "start", "stop", "restart" }.Select(v => $"/bin/systemctl {v} {u}")));

            return
                "systemctl has to run as **root**, and the bot is not allowed to. Grant it exactly these " +
                "units and verbs and nothing else — run `visudo` and add one line, using the user the bot " +
                "runs as:\n" +
                $"```\n{Environment.UserName} ALL=(root) NOPASSWD: {permitted}\n```\n" +
                "No bot restart is needed after that. If the bot itself runs as root, this is already " +
                "unnecessary and something else is wrong.";
        }

        if (text.Contains("not found", StringComparison.Ordinal) ||
            text.Contains("no such file", StringComparison.Ordinal))
        {
            return
                $"systemd does not know these unit names. Check them with `systemctl list-units 'pavlov*'` " +
                $"and set `PAVLOV_UNITS` in `.env` to the real ones (currently `{string.Join(", ", Units)}`).";
        }

        return null;
    }
}
