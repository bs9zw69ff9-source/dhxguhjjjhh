using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A command the server refuses must not be reported as done.
/// </summary>
/// <remarks>
/// FROM PRODUCTION. `/manual BanList` was handed
/// <c>{"Command":"Ban","Ban":false,"UniqueID":"Holosight1","Successful":false}</c> - the
/// reply to a ban the server REFUSED. Two separate faults met in that one embed: the reply
/// reached the wrong caller (see RconCorrelationTests), and nothing anywhere was reading it.
///
/// Every mutating call site discarded its reply. BanService counted a ban as reconciled
/// whatever came back, and the unban path logged "unban RCON accepted" without checking
/// whether it had been. RconReply.Successful already existed and was used in exactly two
/// places, both reads. So a moderator could ban somebody, be told it worked, and be wrong.
/// </remarks>
public class RejectedCommandTests
{
    private static RconRegistry Registry(FakeRconServer server) =>
        new(new BotOptions
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
                    CommandTimeout = TimeSpan.FromMilliseconds(500),
                    CommandSpacing = TimeSpan.Zero,
                    ReadCacheDuration = TimeSpan.Zero,
                    MaxAttempts = 1,
                },
            ],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        }, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

    [Fact]
    public async Task ARefusedCommandThrowsRatherThanReturningQuietly()
    {
        /* THE REPORTED REPLY, near enough: a Ban the server declined. Before this, the reply
           was discarded and the ban counted, so the player stayed unbanned and the log said
           they were not. */
        await using var server = new FakeRconServer { RefuseEverything = true };
        await using var registry = Registry(server);

        var ex = await Assert.ThrowsAsync<RconRejectedException>(
            () => registry.SendVerifiedAsync("server1", "Ban Holosight1"));

        Assert.Contains("Ban Holosight1", ex.Command, StringComparison.Ordinal);
        Assert.Contains("Successful", ex.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAcceptedCommandPassesThrough()
    {
        await using var server = new FakeRconServer();
        await using var registry = Registry(server);

        var reply = await registry.SendVerifiedAsync("server1", "Ban Holosight1");

        Assert.Contains("Successful", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyThatDoesNotSayIsTreatedAsAccepted()
    {
        /* TRI-STATE, and only false is a refusal. Plenty of Pavlov replies omit Successful,
           and treating unknown as failure would turn most of the command surface into errors
           in order to catch a fault those replies cannot express. */
        await using var server = new FakeRconServer { OmitSuccessfulField = true };
        await using var registry = Registry(server);

        var reply = await registry.SendVerifiedAsync("server1", "Ban Holosight1");

        Assert.NotNull(reply);
    }

    [Fact]
    public async Task ANonJsonReplyIsTreatedAsAccepted()
    {
        // Failures come back as bare text on some builds. Refusing those would be the same
        // over-reach as refusing an absent field.
        await using var server = new FakeRconServer { PlainTextReply = "OK" };
        await using var registry = Registry(server);

        var reply = await registry.SendVerifiedAsync("server1", "Ban Holosight1");

        Assert.Contains("OK", reply, StringComparison.Ordinal);
    }
}
