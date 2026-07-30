using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Ported from test/rcon-cache.test.js and test/rcon-audit.test.js, plus the persistent
/// session the Node client never had.
///
/// The cost that matters is CONNECTIONS OPENED, not commands sent. Every RCON connection
/// is a TCP handshake plus an MD5 exchange serviced on Pavlov's game thread, so the
/// assertions are on the fake server's connection count.
/// </summary>
public class RconClientTests
{
    private static RconOptions Options(FakeRconServer server, TimeSpan? cache = null) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Password = server.Password,
        Name = "server1",
        CommandTimeout = TimeSpan.FromSeconds(3),
        // Spacing off by default: it is asserted explicitly in its own test, and paying
        // 100ms per command everywhere else would just make the suite slow.
        CommandSpacing = TimeSpan.Zero,
        ReadCacheDuration = cache ?? TimeSpan.FromMilliseconds(2500),
    };

    [Fact]
    public async Task AuthenticatesAndReturnsAReply()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        var reply = await client.SendAsync("RefreshList");

        Assert.Contains("\"Successful\":true", reply, StringComparison.Ordinal);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task ManyCommandsShareOneConnection()
    {
        // The headline change: the Node client opened a socket and re-authenticated per
        // command, which the Pavlov docs advise against.
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server, cache: TimeSpan.Zero));

        for (var i = 0; i < 10; i++) await client.SendAsync("Kick Player" + i);

        Assert.Equal(1, server.Connections);
        Assert.Equal(10, server.Commands.Count);
    }

    [Fact]
    public async Task WrongPasswordFailsFastAndIsNotRetried()
    {
        await using var server = new FakeRconServer { RejectAuth = true };
        await using var client = new RconClient(Options(server));

        await Assert.ThrowsAsync<RconAuthException>(() => client.SendAsync("RefreshList"));
        // Retrying a rejected password is just a slower way to stay broken.
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task ConcurrentIdenticalReadsShareOneExchange()
    {
        await using var server = new FakeRconServer { ReplyDelay = TimeSpan.FromMilliseconds(60) };
        await using var client = new RconClient(Options(server));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.SendAsync("RefreshList")));

        Assert.Single(server.Commands);
    }

    [Fact]
    public async Task ACompletedReadIsReusedWithinTheWindow()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server, cache: TimeSpan.FromMilliseconds(400)));

        await client.SendAsync("RefreshList");
        await client.SendAsync("RefreshList");
        Assert.Single(server.Commands);

        await Task.Delay(500);
        await client.SendAsync("RefreshList");
        Assert.Equal(2, server.Commands.Count);   // past the window it goes back to the server
    }

    [Fact]
    public async Task DifferentReadsAreCachedSeparately()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        await client.SendAsync("RefreshList");
        await client.SendAsync("ServerInfo");

        Assert.Equal(2, server.Commands.Count);
    }

    [Fact]
    public async Task MutationsAreNeverCachedAndTheyClearTheRoster()
    {
        // The dangerous case: kick someone, then read a roster that still lists them.
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        await client.SendAsync("RefreshList");
        await client.SendAsync("Kick Griefer");
        await client.SendAsync("RefreshList");
        Assert.Equal(3, server.Commands.Count);

        await client.SendAsync("Kick Griefer");
        await client.SendAsync("Kick Griefer");
        Assert.Equal(5, server.Commands.Count);
    }

    [Fact]
    public async Task AnUnknownCommandIsTreatedAsAMutation()
    {
        /* The read list is an allow-list on purpose. Caching a command we do not
           understand could serve a stale answer for something that changed state. */
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        await client.SendAsync("SomeFutureCommand");
        await client.SendAsync("SomeFutureCommand");

        Assert.Equal(2, server.Commands.Count);
    }

    [Fact]
    public async Task ADroppedSessionIsRebuiltTransparently()
    {
        // Server restarts and map changes drop the session; the caller should not care.
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server, cache: TimeSpan.Zero));

        await client.SendAsync("RefreshList");
        server.DropAfterNextCommand = true;
        await client.SendAsync("ServerInfo");          // this one kills the session

        var reply = await client.SendAsync("RefreshList");   // must reconnect silently
        Assert.Contains("\"Successful\":true", reply, StringComparison.Ordinal);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task AFailedReadIsNotCachedAsAnAnswer()
    {
        // Nothing listening on this port.
        await using var client = new RconClient(new RconOptions
        {
            Host = "127.0.0.1", Port = 1, Password = "x", Name = "dead",
            MaxAttempts = 1, CommandTimeout = TimeSpan.FromMilliseconds(400),
            CommandSpacing = TimeSpan.Zero,
        });

        await Assert.ThrowsAsync<RconException>(() => client.SendAsync("RefreshList"));
        await Assert.ThrowsAsync<RconException>(() => client.SendAsync("RefreshList"));
    }

    [Fact]
    public async Task CommandSpacingIsHonoured()
    {
        // The docs advise 100ms between successive commands to prevent drops.
        await using var server = new FakeRconServer();
        var options = Options(server, cache: TimeSpan.Zero) with { CommandSpacing = TimeSpan.FromMilliseconds(100) };
        await using var client = new RconClient(options);

        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < 4; i++) await client.SendAsync("Kick P" + i);
        var elapsed = DateTimeOffset.UtcNow - started;

        // Four commands means at least three gaps. Generous lower bound to stay stable on CI.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(250), $"expected pacing, took {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task IssuedCommandsAreRecognisedAsTheBotsOwn()
    {
        /* Pavlov logs the bot's own RCON traffic identically to everyone else's, so the
           audit needs this to avoid alerting on every /givemenu the bot itself ran. */
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Options(server));

        Assert.False(client.WasIssuedByBot("GiveMenu Stranger"));
        await client.SendAsync("GiveMenu Butter Life");

        Assert.True(client.WasIssuedByBot("GiveMenu Butter Life"));
        Assert.True(client.WasIssuedByBot("  givemenu butter life  "));   // case and padding insensitive
        Assert.False(client.WasIssuedByBot("GiveMenu Attacker"));          // a different target must still alert
    }

    [Fact]
    public void BackoffJitterStaysWithinItsWindow()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var ceiling = Math.Min(30_000, 500 * (1 << attempt));
            for (var i = 0; i < 50; i++)
            {
                var ms = Backoff.Delay(attempt).TotalMilliseconds;
                Assert.InRange(ms, ceiling * 0.5 - 1, ceiling);
            }
        }
    }
}
