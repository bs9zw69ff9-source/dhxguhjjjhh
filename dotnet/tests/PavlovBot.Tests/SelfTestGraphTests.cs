using System.Diagnostics;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The deploy gate, run for real: the bot's whole object graph builds.</summary>
/// <remarks>
/// A dependency cycle does not fail to compile and no unit test sees it: the container simply
/// never finishes building. That shipped once - a command needed a service that needed the
/// gateway that needed the command - and hung the deploy at "Verifying configuration". This runs
/// the same <c>--selftest</c> the deploy runs, with a deadline.
/// </remarks>
public sealed class SelfTestGraphTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "selftest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task TheWholeGraphBuildsWithinAMinute()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "data"));
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"),
            $"DISCORD_TOKEN=x\nCLIENT_ID=1\nGUILD_ID=1\nDATA_DIR={Path.Combine(_directory, "data")}\n");

        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PavlovBot.dll"));
        info.ArgumentList.Add("--selftest");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("--selftest did not finish within 60s: the service graph never finished building (a dependency cycle?)");
        }

        var text = await output + await errors;
        Assert.True(process.ExitCode == 0, text);
        Assert.Contains("selftest: graph built", text, StringComparison.Ordinal);
    }
}
