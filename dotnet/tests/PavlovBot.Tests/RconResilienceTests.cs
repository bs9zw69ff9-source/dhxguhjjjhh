using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The RCON transport under failure: timeouts, stream state and coalescing.
/// </summary>
/// <remarks>
/// Against a real socket, because the failures worth catching here only exist at that level.
/// A desynchronised stream cannot be expressed in a mock of the client's own abstraction -
/// the mock returns whatever it was told to, correctly paired, by construction.
/// </remarks>
public class RconResilienceTests
{
    private static RconOptions Options(FakeRconServer server) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Password = server.Password,
        Name = "test",
        CommandTimeout = TimeSpan.FromMilliseconds(300),
        MaxAttempts = 1,
        CommandSpacing = TimeSpan.Zero,
        ReadCacheDuration = TimeSpan.Zero,
    };

    [Fact]
    public async Task ATimedOutCommandDoesNotDesynchroniseTheStream()
    {
        /* THE BUG THIS EXISTS FOR, and it is silent rather than loud.

           RconClient wraps every send in a linked CTS with CancelAfter(CommandTimeout). When
           that fired mid-exchange the command had ALREADY been written and its reply was
           never read - and OperationCanceledException is not IOException, so the reconnect
           path did not catch it and the socket stayed open with an unconsumed reply in it.

           The next command then wrote, read, and got the PREVIOUS command's answer. Every
           command after that was off by one, permanently, until the idle recycle five minutes
           later. Nothing throws: RefreshList returns a well-formed roster that belongs to an
           older request, which is indistinguishable from a roster that is merely stale. */
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        /* A LATE reply, not a missing one, and the distinction is the entire test. A command
           the server never answers leaves nothing behind, so the next command reads its own
           reply and everything looks fine. A command answered AFTER the client gave up puts
           an orphaned reply in the socket, and that is what the next reader picks up. */
        server.ReplyDelay = TimeSpan.FromMilliseconds(800);   // the timeout is 300ms

        await Assert.ThrowsAsync<RconException>(() => client.SendAsync("ServerInfo"));

        server.ReplyDelay = TimeSpan.Zero;
        await Task.Delay(TimeSpan.FromSeconds(1));            // let the orphan land

        var reply = await client.SendAsync("RefreshList");

        Assert.Contains("RefreshList", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerInfo", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimedOutCommandForcesAFreshSession()
    {
        // The stream state is unknowable after an abandoned exchange, so the only safe move
        // is a new socket - observable as a second connection.
        await using var server = new FakeRconServer();
        server.Swallow.Add("ServerInfo");

        await using var client = new RconClient(Options(server));

        await Assert.ThrowsAsync<RconException>(() => client.SendAsync("ServerInfo"));
        await client.SendAsync("RefreshList");

        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task ASuccessfulCommandKeepsTheSessionOpen()
    {
        /* The other half of the property. Dropping the socket on every failure must not turn
           into dropping it on every command - a persistent session is the whole reason this
           client exists, because Pavlov services RCON on the game thread. */
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        for (var i = 0; i < 5; i++) await client.SendAsync("RefreshList");

        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task EveryReplyMatchesItsOwnCommand()
    {
        // The invariant, stated directly rather than through a failure mode.
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        foreach (var command in new[] { "RefreshList", "ServerInfo", "MapList", "Banlist" })
            Assert.Contains(command, await client.SendAsync(command), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneCallerCancellingDoesNotCancelACoalescedReadForEverybodyElse()
    {
        /* The in-flight map stored a task built from whichever caller won the race, so every
           joiner inherited that caller's cancellation token. A slash command being cancelled
           took down the roster read that several background services were waiting on, and
           they saw a cancellation nobody asked for. */
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromMilliseconds(200) };

        await using var client = new RconClient(Options(server) with
        {
            ReadCacheDuration = TimeSpan.FromSeconds(10),
            CommandTimeout = TimeSpan.FromSeconds(5),
        });

        using var leaving = new CancellationTokenSource();

        var first = client.SendAsync("RefreshList", leaving.Token);
        var second = client.SendAsync("RefreshList");

        await leaving.CancelAsync();

        var reply = await second;
        Assert.Contains("RefreshList", reply, StringComparison.Ordinal);

        try { await first; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ACoalescedReadHitsTheServerOnce()
    {
        // The point of coalescing: several callers, one round trip on the game thread.
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromMilliseconds(150) };

        await using var client = new RconClient(Options(server) with
        {
            ReadCacheDuration = TimeSpan.FromSeconds(10),
            CommandTimeout = TimeSpan.FromSeconds(5),
        });

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SendAsync("RefreshList")));

        Assert.Single(server.Commands, c => c.StartsWith("RefreshList", StringComparison.Ordinal));
    }
}
