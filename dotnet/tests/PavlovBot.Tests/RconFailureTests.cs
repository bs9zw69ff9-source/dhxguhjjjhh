using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// What the bot says when a command reached no server.
/// </summary>
/// <remarks>
/// "No server accepted the command" names the symptom and nothing else, and it is what
/// somebody reads while standing in front of a server they cannot administer. A refused
/// connection, a wrong password, a stopped server and a verb RCON+ does not implement need
/// four different actions - and the bot knew which it was at the moment it gave up, then
/// logged it at Warning where nobody was looking.
/// </remarks>
public class RconFailureTests
{
    private static RconRegistry Registry(params string[] servers) => new(
        new BotOptions
        {
            DiscordToken = "t",
            Servers = [.. servers.Select(s => new RconOptions
            {
                Name = s, Host = "127.0.0.1", Port = 1, Password = "x",
            })],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        }, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

    private static string Render(Discord.EmbedBuilder embed)
    {
        var built = embed.Build();
        return built.Title + "\n" + built.Description + "\n" +
               string.Join("\n", built.Fields.Select(f => $"{f.Name}: {f.Value}"));
    }

    [Fact]
    public void TheReasonEachServerGaveIsOnTheReply()
    {
        var text = Render(RconFailure.Explain("Not granted", "Nothing was recorded.", Registry("server1", "server2"),
        [
            ("server1", "Connection refused (127.0.0.1:9102)"),
            ("server2", "Authentication failed"),
        ]));

        Assert.Contains("server1", text, StringComparison.Ordinal);
        Assert.Contains("Connection refused (127.0.0.1:9102)", text, StringComparison.Ordinal);
        Assert.Contains("server2", text, StringComparison.Ordinal);
        Assert.Contains("Authentication failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalWithNoErrorSaysThatRatherThanNothing()
    {
        /* The server answered and declined. That is a different fault from not reaching it,
           and it points at the verb rather than at the connection. */
        var text = Render(RconFailure.Explain("Not granted", "Nothing was recorded.", Registry("server1"),
            [("server1", null)]));

        Assert.Contains("without an error", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoServersConfiguredIsItsOwnAnswer()
    {
        /* Nobody diagnoses "no RCON servers are configured" from a message about servers
           refusing a command. */
        var text = Render(RconFailure.Explain("Not granted", "Nothing was recorded.", Registry(), []));

        Assert.Contains("No RCON servers are configured", text, StringComparison.Ordinal);
        Assert.Contains("RCON_HOST_1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommonCausesAreNamedAndEachOneIsActionable()
    {
        var text = Render(RconFailure.Explain("Not granted", "Nothing was recorded.", Registry("server1"),
            [("server1", "Connection refused")]));

        Assert.Contains("RCON_PASSWORD", text, StringComparison.Ordinal);
        Assert.Contains("/serverinfo", text, StringComparison.Ordinal);
    }
}
