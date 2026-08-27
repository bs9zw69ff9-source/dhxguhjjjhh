using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Two bugs that a <c>using</c> block and a cancellation token LOOKED like they had covered.
/// </summary>
/// <remarks>
/// Every external command the bot runs - systemctl three ways, ufw - used the same shape:
/// a linked CancellationTokenSource with a CancelAfter, and <c>using var process</c>. That
/// reads as a timeout with cleanup and is neither. Cancelling a read abandons the READ, and
/// disposing a <see cref="Process"/> releases OUR HANDLES - neither touches the child, which
/// keeps running. <c>ServiceControl.StateAsync</c> is called per server on a TIMER, so a
/// systemctl that wedges leaked one process per tick, indefinitely.
///
/// These tests use <c>/bin/sh</c> rather than mocks because the bug is in the process
/// plumbing itself; a fake would have reproduced the intent, which was never wrong.
/// </remarks>
public class ProcessRunnerTests
{
    // The bot is deployed on Linux and CI runs there. Skipped elsewhere rather than failing
    // for the wrong reason - and never silently, hence the constant being named.
    private static bool ShellAvailable => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static Task<ProcessOutcome> Sh(string script, TimeSpan timeout, CancellationToken ct = default) =>
        ProcessRunner.RunAsync("/bin/sh", ["-c", script], timeout, NullLogger.Instance, ct);

    [Fact]
    public async Task ADeadlineActuallyKillsTheChild()
    {
        if (!ShellAvailable) return;

        /* THE REGRESSION. The marker is written two seconds in; the deadline is a quarter of
           that. If the child survives the deadline - which it did, measured, before the fix -
           the file appears while we wait, and the leak is a file on disk rather than an
           argument about process semantics. */
        var marker = Path.Combine(Path.GetTempPath(), $"pavlov-runner-{Guid.NewGuid():N}");

        try
        {
            var outcome = await Sh($"sleep 2; touch '{marker}'", TimeSpan.FromMilliseconds(400));

            Assert.True(outcome.Started);
            Assert.True(outcome.TimedOut);

            // Comfortably past when the child would have written it had it survived.
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(marker), "the child outlived its deadline and kept running");
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    [Fact]
    public async Task TheProcessTreeGoesWithIt()
    {
        if (!ShellAvailable) return;

        /* sh spawns a child and exits nothing - which is exactly the sudo case. Killing only
           what we started leaves the systemctl we were waiting for running. */
        var marker = Path.Combine(Path.GetTempPath(), $"pavlov-runner-tree-{Guid.NewGuid():N}");

        try
        {
            var outcome = await Sh($"sh -c \"sleep 2; touch '{marker}'\" & wait", TimeSpan.FromMilliseconds(400));

            Assert.True(outcome.TimedOut);

            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(marker), "a grandchild survived the kill");
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    [Fact]
    public async Task ALargeStderrDoesNotDeadlock()
    {
        if (!ShellAvailable) return;

        /* THE OTHER REGRESSION. Two call sites awaited stdout to completion and only then
           read stderr; two more redirected stderr and never read it. Either way the child
           blocks once its stderr pipe fills - about 64 KB - and we block reading a stdout
           that cannot finish until the child exits. 200 KB is comfortably past the buffer,
           and the generous deadline means a failure here reads as a hang, not a slow box. */
        const int size = 200_000;

        var outcome = await Sh(
            $"head -c {size} /dev/zero | tr '\\000' 'x' >&2; echo done",
            TimeSpan.FromSeconds(20));

        Assert.False(outcome.TimedOut, "the pipes deadlocked");
        Assert.True(outcome.Ok);
        Assert.Equal(size, outcome.Stderr.Length);
        Assert.Equal("done", outcome.Stdout.Trim());
    }

    [Fact]
    public async Task BothStreamsComeBackSeparately()
    {
        if (!ShellAvailable) return;

        // Combined is what the callers report; the split is what lets a caller tell the
        // systemctl answer (stdout) from the reason it refused (stderr).
        var outcome = await Sh("echo out; echo err >&2", TimeSpan.FromSeconds(10));

        Assert.Equal("out", outcome.Stdout.Trim());
        Assert.Equal("err", outcome.Stderr.Trim());
        Assert.Contains("out", outcome.Combined, StringComparison.Ordinal);
        Assert.Contains("err", outcome.Combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonZeroExitIsReportedRatherThanThrown()
    {
        if (!ShellAvailable) return;

        // systemctl is-active exits non-zero for a perfectly good answer, so a failed exit
        // must stay a value the caller inspects.
        var outcome = await Sh("echo nope >&2; exit 3", TimeSpan.FromSeconds(10));

        Assert.True(outcome.Started);
        Assert.False(outcome.TimedOut);
        Assert.False(outcome.Ok);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal("nope", outcome.Stderr.Trim());
    }

    [Fact]
    public async Task OutputWrittenBeforeTheDeadlineSurvivesIt()
    {
        if (!ShellAvailable) return;

        /* Why the reads are not given the deadline token. Killing the child closes the pipes,
           which completes the reads naturally; cancelling them instead would have thrown away
           the output - which on a timeout is the most useful thing there is. */
        var outcome = await Sh("echo early; sleep 5", TimeSpan.FromMilliseconds(500));

        Assert.True(outcome.TimedOut);
        Assert.Equal("early", outcome.Stdout.Trim());
    }

    [Fact]
    public async Task CallerCancellationIsRethrownRatherThanReportedAsATimeout()
    {
        if (!ShellAvailable) return;

        /* The bot shutting down is not a ufw failure to show somebody. These were
           indistinguishable at the call site, and one call site had the test the wrong way
           round - so a real 10s timeout threw out of /firewall while a shutdown was logged
           as "ufw invocation failed". */
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sh("sleep 5", TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task CancellingTheCallerStillKillsTheChild()
    {
        if (!ShellAvailable) return;

        var marker = Path.Combine(Path.GetTempPath(), $"pavlov-runner-cancel-{Guid.NewGuid():N}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Sh($"sleep 2; touch '{marker}'", TimeSpan.FromSeconds(30), cts.Token));

            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(File.Exists(marker), "cancelling the caller left the child running");
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    [Fact]
    public async Task StdinIsWrittenAndClosedSoTheChildSeesItAndExits()
    {
        if (!ShellAvailable) return;

        /* cat with no file argument reads until EOF on stdin and echoes it back. If the write
           did not happen, or stdin was never CLOSED, this would time out instead of returning -
           exactly the shape of bug this parameter exists to avoid for chpasswd, which reads
           until EOF the same way. */
        var outcome = await ProcessRunner.RunAsync(
            "/bin/cat", [], TimeSpan.FromSeconds(5), NullLogger.Instance, stdin: "steam:hunter2\n");

        Assert.True(outcome.Ok);
        Assert.Equal("steam:hunter2\n", outcome.Stdout);
    }

    [Fact]
    public async Task NullStdinLeavesExistingCallSitesUnaffected()
    {
        if (!ShellAvailable) return;

        // No stdin parameter at all - the exact call shape every existing caller uses.
        var outcome = await Sh("echo unaffected", TimeSpan.FromSeconds(5));

        Assert.True(outcome.Ok);
        Assert.Equal("unaffected\n", outcome.Stdout);
    }
}
