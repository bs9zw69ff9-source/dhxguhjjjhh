using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A command that changes state is sent once unless it provably never arrived.
/// </summary>
public class RconDeliveryTests
{
    private static RconOptions Options(FakeRconServer server, int attempts = 3) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Password = server.Password,
        Name = "test",
        CommandTimeout = TimeSpan.FromMilliseconds(300),
        MaxAttempts = attempts,
        CommandSpacing = TimeSpan.Zero,
        ReadCacheDuration = TimeSpan.Zero,
    };

    private static int Count(FakeRconServer server, string command) =>
        server.Commands.Count(c => string.Equals(c, command, StringComparison.Ordinal));

    [Fact]
    public async Task ASlowMutationIsNotSentAgain()
    {
        /* The server received the Kick and was slow to answer. Treating the timeout as "it did
           not happen" re-sent it on every attempt - a double Notify, a second Kick. */
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromMilliseconds(800) };
        await using var client = new RconClient(Options(server));

        var failure = await Assert.ThrowsAsync<RconException>(() => client.SendAsync("Kick Griefer"));

        Assert.Contains("may have been applied", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, Count(server, "Kick Griefer"));
    }

    [Fact]
    public async Task ASlowReadIsStillRetried()
    {
        // Reads are harmless to repeat, so they keep their retries.
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromMilliseconds(800) };
        await using var client = new RconClient(Options(server));

        await Assert.ThrowsAsync<RconException>(() => client.SendAsync("ServerInfo"));

        Assert.True(Count(server, "ServerInfo") > 1);
    }

    [Fact]
    public async Task AMutationOnADeadSessionIsResentOnAFreshOne()
    {
        /* The case the reconnect exists for: the server restarted, so the idle session is dead.
           The write "succeeds" into a closed socket and nothing comes back - a dead connection
           cannot have delivered it, so sending it again is safe and is what the caller wants. */
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        server.DropAfterNextCommand = true;
        await client.SendAsync("RefreshList");     // answered, then the server hangs up
        await Task.Delay(100);

        var reply = await client.SendAsync("Kick Griefer");

        Assert.Contains("Kick", reply, StringComparison.Ordinal);
        Assert.Equal(1, Count(server, "Kick Griefer"));
        Assert.Equal(2, server.Connections);
    }

    [Theory]
    [InlineData("Kick a\nBan b")]
    [InlineData("Notify All hi\r\nGiveCash x 100")]
    public async Task AMultiLineCommandIsRefusedBeforeItReachesTheWire(string command)
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(command));

        Assert.Empty(server.Commands);
    }

    [Fact]
    public async Task AHandshakeSplitAcrossPacketsStillAuthenticates()
    {
        // "Pass" + "word: " and "Auth" + "enticated=1" used to read as a wrong prompt / refusal.
        await using var server = new FakeRconServer { SplitHandshake = true };
        await using var client = new RconClient(Options(server));

        Assert.Contains("RefreshList", await client.SendAsync("RefreshList"), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingWrittenMeansNothingApplied()
    {
        Assert.False(RconClient.MayHaveBeenApplied(new TimeoutException(), new ExchangeProgress { Written = false }));
        Assert.True(RconClient.MayHaveBeenApplied(new OperationCanceledException(), new ExchangeProgress { Written = true }));
        Assert.False(RconClient.MayHaveBeenApplied(new IOException(), new ExchangeProgress { Written = true, ReplyBytes = 0 }));
        Assert.True(RconClient.MayHaveBeenApplied(new IOException(), new ExchangeProgress { Written = true, ReplyBytes = 12 }));
    }
}
