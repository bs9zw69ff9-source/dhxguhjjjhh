using System.Diagnostics;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The deploy gate, run for real: the bot's whole object graph builds, with the optional parts on.</summary>
/// <remarks>
/// A dependency cycle does not fail to compile and no unit test sees it: the container simply
/// never finishes building. That shipped once - /antiddos needed a service that needed the
/// gateway that needed /antiddos - and hung the deploy at "Verifying configuration". This runs
/// the same <c>--selftest</c> the deploy runs, with a deadline.
/// </remarks>
public sealed class SelfTestGraphTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "selftest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("ATTACK_CHANNEL=1234567890123456789\nATTACK_MITIGATION=on\n")]
    public async Task TheWholeGraphBuildsWithinAMinute(string extra)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "data"));
        await File.WriteAllTextAsync(Path.Combine(_directory, ".env"),
            $"DISCORD_TOKEN=x\nCLIENT_ID=1\nGUILD_ID=1\nDATA_DIR={Path.Combine(_directory, "data")}\n{extra}");

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
        if (extra.Length > 0) Assert.Contains("/antiddos", text, StringComparison.Ordinal);
    }
}
