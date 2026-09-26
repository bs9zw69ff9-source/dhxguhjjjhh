using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using PavlovBot.Rcon.Protocol;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A state-changing command is answered by the RCON reply or by the server's own Pavlov.log,
/// whichever comes first.
/// </summary>
public class RconLogConfirmationTests
{
    private const string Log1 = "/srv/pavlov1/Pavlov/Saved/Logs/Pavlov.log";
    private const string Log2 = "/srv/pavlov2/Pavlov/Saved/Logs/Pavlov.log";

    private static RconRegistry Registry(FakeRconServer server, RconConfirmations confirmations, TimeSpan timeout)
    {
        var registry = new RconRegistry(new BotOptions
        {
            DiscordToken = "t",
            Servers =
            [
                new RconOptions
                {
                    Name = "server1",
                    Host = "127.0.0.1",
                    Port = server.Port,
                    Password = server.Password,
                    CommandTimeout = timeout,
                    CommandSpacing = TimeSpan.Zero,
                    ReadCacheDuration = TimeSpan.Zero,
                    MaxAttempts = 1,
                },
            ],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        }, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

        registry.UseLogConfirmation(confirmations, name => ServerLabels.LogFor([Log1, Log2], name));
        return registry;
    }

    /// <summary>Record the command in a log once the server has received it, as the tail would.</summary>
    private static async Task EchoWhenReceived(FakeRconServer server, RconConfirmations confirmations, string command, string log)
    {
        for (var i = 0; i < 200 && !server.Commands.Contains(command); i++) await Task.Delay(10);
        confirmations.Note(command, log);
    }

    [Fact]
    public async Task ALoggedCommandIsAnsweredBeforeASlowReply()
    {
        /* The server ran it (it is in its log) but the game thread is slow to answer RCON. The
           caller used to wait out the reply; now the log line is the answer. */
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromSeconds(2) };
        var confirmations = new RconConfirmations();
        var registry = Registry(server, confirmations, TimeSpan.FromSeconds(5));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var echo = EchoWhenReceived(server, confirmations, "Kick Griefer", Log1);
        var reply = await registry.SendVerifiedAsync("server1", "Kick Griefer");
        await echo;

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
        Assert.Contains("Pavlov.log", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandWhoseReplyNeverComesIsConfirmedByTheLog()
    {
        // The reply is lost entirely: this used to be a failure, reported for a command that ran.
        await using var server = new FakeRconServer();
        server.Swallow.Add("Notify");
        var confirmations = new RconConfirmations();
        var registry = Registry(server, confirmations, TimeSpan.FromMilliseconds(300));

        var echo = EchoWhenReceived(server, confirmations, "Notify All hello", Log1);
        var reply = await registry.SendVerifiedAsync("server1", "Notify All hello");
        await echo;

        Assert.True(RconReply.TryParse(reply, out var document));
        using (document) Assert.True(RconReply.Successful(document!.RootElement));
    }

    [Fact]
    public async Task WithNoLogLineTheRconFailureStands()
    {
        await using var server = new FakeRconServer();
        server.Swallow.Add("Kick");
        var registry = Registry(server, new RconConfirmations(), TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<RconException>(() => registry.SendAsync("server1", "Kick Nobody"));
    }

    [Fact]
    public async Task ARefusalThatArrivesFirstIsStillARefusal()
    {
        // "Successful": false is the server saying no; a log echo does not overrule it.
        await using var server = new FakeRconServer { RefuseEverything = true };
        var confirmations = new RconConfirmations();
        var registry = Registry(server, confirmations, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<RconRejectedException>(() => registry.SendVerifiedAsync("server1", "Ban Griefer"));
    }

    [Fact]
    public async Task ACommandSeenOnlyInAnotherServersLogDoesNotCount()
    {
        // The same Kick fanned out to every server; server 2 running it proves nothing about 1.
        await using var server = new FakeRconServer();
        server.Swallow.Add("Kick");
        var confirmations = new RconConfirmations();
        var registry = Registry(server, confirmations, TimeSpan.FromMilliseconds(300));

        var echo = EchoWhenReceived(server, confirmations, "Kick Griefer", Log2);
        await Assert.ThrowsAsync<RconException>(() => registry.SendAsync("server1", "Kick Griefer"));
        await echo;
    }

    [Fact]
    public async Task AReadIsNeverAnsweredFromTheLog()
    {
        // A read wants the reply's data; "it ran" is not an answer to RefreshList.
        await using var server = new FakeRconServer();
        var confirmations = new RconConfirmations();
        confirmations.Note("RefreshList", Log1);
        var registry = Registry(server, confirmations, TimeSpan.FromSeconds(2));

        var reply = await registry.SendAsync("server1", "RefreshList");

        Assert.Contains("PlayerList", reply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server1", Log1)]
    [InlineData("server2", Log2)]
    [InlineData("server3", null)]
    [InlineData("Server 1", null)]
    public void AnRconServerMapsToTheLogWithItsNumber(string server, string? log)
    {
        Assert.Equal(log, ServerLabels.LogFor([Log1, Log2], server));
    }
}
