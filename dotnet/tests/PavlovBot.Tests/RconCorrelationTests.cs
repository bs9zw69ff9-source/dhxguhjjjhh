using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using PavlovBot.Rcon.Protocol;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Whether a reply can be handed to the wrong command.
/// </summary>
/// <remarks>
/// FROM PRODUCTION, TWICE. `/manual BanList` came back with
/// <c>{"Command":"ServerInfo",...}</c> at 15:55 and <c>{"Command":"RefreshList",...}</c> at
/// 16:34, both <c>"Successful": true</c>. Two DIFFERENT wrong answers to one command rules
/// out a fixed server-side fallback, and ServerInfo and RefreshList are exactly the two
/// verbs the background services send - so the reply belonged to other traffic on the same
/// connection.
///
/// These tests pin the two halves of that: the transport must not carry a reply across
/// commands, and the client must refuse a reply whose Command does not match what it sent.
/// The second is the one that matters, because the first cannot be guaranteed against a game
/// server whose reply behaviour is not ours to fix.
/// </remarks>
public class RconCorrelationTests
{
    private static RconOptions Options(FakeRconServer server) => new()
    {
        Name = "server1",
        Host = "127.0.0.1",
        Port = server.Port,
        Password = server.Password,
        CommandTimeout = TimeSpan.FromMilliseconds(400),
        CommandSpacing = TimeSpan.Zero,
        ReadCacheDuration = TimeSpan.Zero,   // the cache would mask the transport
        MaxAttempts = 1,                     // one attempt, so the sequence is legible
    };

    private static string? CommandOf(string reply) =>
        RconReply.TryParse(reply, out var document) && document is not null
            ? RconReply.Text(document.RootElement, "Command")
            : null;

    [Fact]
    public async Task ASlowReplyIsNotHandedToTheNextCommand()
    {
        /* THE REPORTED FAILURE, as a sequence. The first command times out with its reply
           still in flight; if the socket were kept, those bytes would be sitting unread when
           the next command writes, and that command would read them as its own answer -
           permanently off by one until the idle recycle. */
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromSeconds(2) };
        await using var client = new RconClient(Options(server));

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("ServerInfo"));

        server.ReplyDelay = TimeSpan.Zero;
        var reply = await client.SendAsync("RefreshList");

        Assert.Equal("RefreshList", CommandOf(reply));
    }

    [Fact]
    public async Task ACommandTheServerNeverAnswersDoesNotStealTheNextReply()
    {
        // The same shape, for the case that actually produced the report: a verb Pavlov does
        // not recognise. FakeRconServer.Swallow consumes it and never answers.
        await using var server = new FakeRconServer();
        server.Swallow.Add("BanList");
        await using var client = new RconClient(Options(server));

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("BanList"));

        var reply = await client.SendAsync("ServerInfo");

        Assert.Equal("ServerInfo", CommandOf(reply));
    }

    [Fact]
    public async Task ConcurrentCallersEachGetTheirOwnReply()
    {
        /* The gate serialises the socket, but "serialised" and "correctly attributed" are
           different claims and only the second one matters to a caller. /manual runs while
           the probe and the roster sweep are both ticking, which is how the two wrong replies
           in the report were background traffic. */
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        var verbs = new[] { "ServerInfo", "RefreshList", "ItemList", "MapList", "ModeratorList" };
        var replies = await Task.WhenAll(verbs.Select(v => Task.Run(() => client.SendAsync(v))));

        for (var i = 0; i < verbs.Length; i++)
            Assert.Equal(verbs[i], CommandOf(replies[i]));
    }

    [Fact]
    public async Task AMismatchedReplyIsRejectedRatherThanReturned()
    {
        /* THE GUARD ITSELF. Everything above tests OUR transport, and our transport may well
           be correct - the reply behaviour of a game server is not ours to fix, and a build
           that answers the wrong thing would defeat any amount of care on this side.

           So the last line of defence is to check: the reply says which Command it is
           answering, and if that is not the verb we sent, the answer is not ours. Returning
           it anyway is how a moderator reads a ban list that is actually a player roster. */
        await using var server = new FakeRconServer { AnswerEverythingAs = "ServerInfo" };
        await using var client = new RconClient(Options(server));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync("RefreshList"));

        Assert.Contains("RefreshList", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ServerInfo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepliedVerbThatIsSimplyAbsentIsAccepted()
    {
        // Not every Pavlov reply names a Command, and a bot that refused those would refuse
        // most of the command surface. Absent is unknown, and unknown is not a mismatch.
        await using var server = new FakeRconServer { OmitCommandField = true };
        await using var client = new RconClient(Options(server));

        var reply = await client.SendAsync("ServerInfo");

        Assert.Contains("Successful", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRegistrySurfacesAMismatchRatherThanTheWrongAnswer()
    {
        // End to end through the path /manual actually uses.
        await using var server = new FakeRconServer { AnswerEverythingAs = "ServerInfo" };

        var options = new BotOptions
        {
            DiscordToken = "t",
            Servers = [Options(server)],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        };
        await using var registry = new RconRegistry(options, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(() => registry.SendAsync("server1", "BanList"));
    }
}
