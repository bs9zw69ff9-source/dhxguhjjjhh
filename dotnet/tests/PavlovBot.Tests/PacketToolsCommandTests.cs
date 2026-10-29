using Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary><c>/packettools</c>: exactly two fixed packages, installed without a prompt.</summary>
public class PacketToolsCommandTests
{
    [Fact]
    public void ItInstallsExactlyTcpdumpAndTsharkWithoutAPrompt()
    {
        Assert.Equal(["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "--no-install-recommends", "tcpdump", "tshark"],
            PacketToolsCommand.InstallArgv());
        Assert.Equal(["DEBIAN_FRONTEND=noninteractive", "apt-get", "update"], PacketToolsCommand.UpdateArgv());
    }

    [Fact]
    public void TheDesktopWiresharkIsNotPulledOntoAServer() =>
        Assert.DoesNotContain("wireshark", PacketToolsCommand.Packages);

    [Fact]
    public void AStepThatWorkedIsNoProblem() =>
        Assert.Null(PacketToolsCommand.Problem("apt-get install", new ProcessOutcome(true, false, 0, "ok", ""), TimeSpan.FromMinutes(8)));

    [Fact]
    public void ABoxWithoutAptSaysSo() =>
        Assert.Contains("no apt", PacketToolsCommand.Problem("apt-get update", ProcessOutcome.NotStarted, TimeSpan.FromMinutes(4)), StringComparison.Ordinal);

    [Fact]
    public void ATimeoutSaysHowLongItWaited() =>
        Assert.Contains("8 minutes", PacketToolsCommand.Problem("apt-get install", new ProcessOutcome(true, true, -1, "", ""), TimeSpan.FromMinutes(8)), StringComparison.Ordinal);

    [Fact]
    public void AFailureShowsAptsOwnWordsButNotPages()
    {
        var problem = PacketToolsCommand.Problem("apt-get install",
            new ProcessOutcome(true, false, 100, new string('x', 5000), "E: Could not get lock /var/lib/dpkg/lock-frontend"),
            TimeSpan.FromMinutes(8))!;

        Assert.Contains("exited 100", problem, StringComparison.Ordinal);
        Assert.Contains("Could not get lock", problem, StringComparison.Ordinal);
        Assert.True(problem.Length < 1100, $"{problem.Length} characters");
    }

    [Theory]
    [InlineData("TShark (Wireshark) 4.2.2 (Git v4.2.2 packaged as 4.2.2-1.1build3).\n\nCopyright 1998-2024", "TShark (Wireshark) 4.2.2 (Git v4.2.2 packaged as 4.2.2-1.1build3).")]
    [InlineData("tcpdump version 4.99.4\nlibpcap version 1.10.4 (with TPACKET_V3)", "tcpdump version 4.99.4")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheVersionIsTheFirstLine(string? output, string? line) => Assert.Equal(line, PacketToolsCommand.FirstLine(output));

    [Fact]
    public void ItHasStatusAndInstall()
    {
        var command = new PacketToolsCommand(null!, null!, null!, null!);
        var built = (SlashCommandProperties)command.Build();

        Assert.Equal("packettools", built.Name.Value);
        Assert.Equal(["status", "install"], built.Options.Value.Select(o => o.Name));
        Assert.True(command.Ephemeral);
    }
}
