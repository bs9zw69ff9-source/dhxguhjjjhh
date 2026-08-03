using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Servers;

/// <param name="Ok">The unit restarted. False means it did not, for the reason given.</param>
/// <param name="Unit">The systemd unit this concerns.</param>
/// <param name="Detail">stderr/stdout, or the reason nothing ran. Never empty.</param>
public sealed record UnitResult(bool Ok, string Unit, string Detail);

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
    bool useSudo,
    ILogger<ServiceControl> logger)
{
    /// <summary>The configured units, in server order: index 0 is "Server 1".</summary>
    public IReadOnlyList<string> Units { get; } = units;

    /// <summary>
    /// The default unit names, matching the default install layout.
    /// </summary>
    /// <remarks>
    /// <c>pavlovserver</c>, <c>pavlovserver1</c>, <c>pavlovserver2</c> - the same sequence
    /// <see cref="Storage.PavlovInstalls"/> discovers and the same order the feeds number
    /// servers in, so "Server 2" means the same thing in a join line and here.
    /// </remarks>
    public static readonly string[] DefaultUnits = ["pavlovserver", "pavlovserver1", "pavlovserver2"];

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
    /// <c>systemctl restart &lt;unit&gt;</c>. Blocks until systemd says it is done.
    /// </summary>
    /// <remarks>
    /// Blocking rather than <c>--no-block</c> deliberately: the answer to "did the server
    /// come back" is the only part of this worth reporting, and <c>--no-block</c> returns
    /// success for having successfully queued a job that may then fail. The interaction is
    /// already deferred, so there is time to wait for the truth.
    /// </remarks>
    public async Task<UnitResult> RestartAsync(string unit, CancellationToken ct = default)
    {
        if (!IsPlausibleUnitName(unit))
            return new UnitResult(false, unit, "not a usable systemd unit name - refused before running anything");

        var (file, argv) = useSudo
            // -n: fail rather than block forever on a password prompt nobody can answer.
            ? ("sudo", new[] { "-n", "systemctl", "restart", unit })
            : ("systemctl", new[] { "restart", unit });

        logger.LogWarning("RESTARTING {Unit} via {File} - every player on it will be disconnected", unit, file);

        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,   // no shell, nothing to inject into
            };
            foreach (var argument in argv) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return new UnitResult(false, unit, $"could not start {file}");

            /* Generously bounded. A Pavlov server can take the better part of a minute to
               stop and come back, and a restart killed halfway leaves it down - which is
               strictly worse than waiting. */
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var output = (stdout + stderr).Trim();
            var ok = process.ExitCode == 0;

            if (ok) logger.LogInformation("{Unit} restarted", unit);
            else logger.LogError("{Unit} did NOT restart (exit {Code}): {Output}", unit, process.ExitCode, output);

            return new UnitResult(ok, unit, output.Length > 0 ? output : ok ? "restarted" : $"exit code {process.ExitCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("{Unit} restart timed out after 90s - it may still be starting", unit);
            return new UnitResult(false, unit, "timed out after 90s. The unit may still be coming up - check `systemctl status`");
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
            return
                "The bot is not allowed to restart these units. Grant it just this, and nothing else — " +
                "run `visudo` and add one line, with the user the bot runs as:\n" +
                $"```\n{Environment.UserName} ALL=(root) NOPASSWD: /bin/systemctl restart {string.Join(", /bin/systemctl restart ", Units)}\n```\n" +
                "Then set `PAVLOV_SYSTEMCTL_SUDO=true` in the bot's `.env` and restart it.";
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
