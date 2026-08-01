using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Logs;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Reading Pavlov.log while the game server holds it open.
/// </summary>
/// <remarks>
/// THE FAILURE THIS PINS took the entire log pipeline down on the real server and said
/// nothing above Debug: every poll threw "the process cannot access the file because it is
/// being used by another process", so no join, kill, address or flagged join ever reached
/// the bot. The feeds and ban-evasion detection had no input at all, while the bot itself
/// looked healthy.
///
/// The cause is not the FileShare flags, which were already correct. On Unix, .NET's
/// FileStream requests an advisory flock REGARDLESS of FileShare, and that fails whenever
/// the writer holds an exclusive one. The fix is the System.IO.DisableFileLocking runtime
/// switch in PavlovBot.Host.csproj - which is a build setting, so this test exists to catch
/// its removal.
/// </remarks>
public class LogTailerLockingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-tail-" + Guid.NewGuid().ToString("N"));

    public LogTailerLockingTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ALockedLogIsStillReadable()
    {
        if (OperatingSystem.IsWindows()) return;   // flock(1) is a Unix tool; the server is Linux

        var path = Path.Combine(_directory, "Pavlov.log");
        File.WriteAllText(path, "[2026.08.01-06.00.00:000][  0]LogNet: hello\n");

        // Exactly what Pavlov does to its own log, reproduced with flock(1).
        using var holder = Process.Start(new ProcessStartInfo("flock", $"-x {path} -c \"sleep 3\"")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        });

        Assert.NotNull(holder);
        Thread.Sleep(300);   // let it actually acquire the lock before we read

        try
        {
            var tailer = new LogTailer(NullLogger.Instance);
            tailer.Poll(path);                       // primes at end-of-file
            File.AppendAllText(path, "[2026.08.01-06.00.01:000][  0]LogNet: second line\n");

            var lines = tailer.Poll(path);

            Assert.Single(lines);
            Assert.Contains("second line", lines[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            try { holder!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
