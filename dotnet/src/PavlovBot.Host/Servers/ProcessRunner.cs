using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Servers;

/// <summary>What a child process did.</summary>
/// <param name="Started">False when the executable could not be launched at all.</param>
/// <param name="TimedOut">True when the deadline hit. <see cref="ExitCode"/> is meaningless then.</param>
public sealed record ProcessOutcome(bool Started, bool TimedOut, int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => Started && !TimedOut && ExitCode == 0;

    /// <summary>Both streams, trimmed. Failures put their reason on stderr, successes on stdout.</summary>
    public string Combined => (Stdout + Stderr).Trim();

    public static ProcessOutcome NotStarted { get; } = new(false, false, -1, "", "");
}

/// <summary>
/// Running one external command with a deadline that actually ends it.
/// </summary>
/// <remarks>
/// THIS EXISTS BECAUSE OF TWO BUGS THAT WERE WRITTEN FOUR TIMES BETWEEN THEM.
///
/// THE CHILD OUTLIVED THE TIMEOUT. Every call site bounded its read with a linked
/// <c>CancellationTokenSource</c> and a <c>using var process</c>, which reads like a timeout
/// but is not one: cancelling a read abandons the READ, and disposing a <see cref="Process"/>
/// releases OUR HANDLES to the child - neither signals the child, which keeps running. That
/// was measured, not assumed: a <c>sleep 30</c> was still in <c>/proc</c> long after the
/// one-second deadline and the using scope had both ended. <c>StateAsync</c> is called per
/// server on a TIMER, so a systemctl that wedges leaks a process every tick until the box
/// runs out of them. This kills the process TREE, because <c>sudo</c> is a parent.
///
/// THE TWO PIPES WERE READ IN SEQUENCE. <c>await stdout; await stderr;</c> deadlocks whenever
/// the child fills its stderr buffer (~64 KB) while we are still draining stdout: the child
/// blocks writing, we block reading, and neither moves until the deadline. Two call sites did
/// this and two others redirected stderr and never read it at all, which is the same trap with
/// a smaller buffer. Both reads are started before either is awaited, and the token is
/// deliberately NOT passed to them - killing the child closes the pipes, which completes them
/// naturally and leaves whatever output arrived before the deadline still readable.
///
/// CANCELLATION BY THE CALLER IS NOT A TIMEOUT and is rethrown. The bot shutting down is not
/// a ufw failure to report, and the two used to be indistinguishable at the call site.
/// </remarks>
public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(
        string file,
        IReadOnlyList<string> argv,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(logger);

        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,   // no shell, so there is nothing to inject into
        };
        foreach (var argument in argv) info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return ProcessOutcome.NotStarted;

        // Started before either is awaited. See the remarks - this is the deadlock fix.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process, file, logger);

            /* The caller going away is not a timeout to report - it is the bot stopping, and
               swallowing it would turn shutdown into a spurious command failure. The child is
               killed either way, which is the part that was missing. */
            if (ct.IsCancellationRequested)
            {
                await Drain(stdout, stderr).ConfigureAwait(false);
                throw;
            }

            timedOut = true;
        }

        var (outText, errText) = await Drain(stdout, stderr).ConfigureAwait(false);

        // ExitCode throws if the process was never reaped; after a kill it has been.
        var code = timedOut ? -1 : SafeExitCode(process);
        return new ProcessOutcome(true, timedOut, code, outText, errText);
    }

    /// <summary>
    /// Both reads, after the process has ended one way or another.
    /// </summary>
    /// <remarks>
    /// Awaited even on the failure paths. A read task nobody observes is an unobserved task
    /// exception, and it also holds the pipe - the output collected before the deadline is
    /// usually the most useful part of a timeout report.
    /// </remarks>
    private static async Task<(string Stdout, string Stderr)> Drain(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Whatever either read managed is still worth having; neither is required.
        }

        return (Text(stdout), Text(stderr));

        static string Text(Task<string> task) =>
            task.IsCompletedSuccessfully ? task.Result : "";
    }

    /// <summary>
    /// End the child, and everything it started.
    /// </summary>
    /// <remarks>
    /// THE TREE, not the process: when the bot is unprivileged the child is <c>sudo</c> and
    /// the systemctl doing the work is its child, so killing only what we started leaves the
    /// thing we were actually waiting for running.
    ///
    /// Best effort by design. The race where it exits between the deadline firing and this
    /// call is normal and not worth a log line above debug.
    /// </remarks>
    private static void Kill(Process process, string file, ILogger logger)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Could not kill {File} after its deadline", file);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
