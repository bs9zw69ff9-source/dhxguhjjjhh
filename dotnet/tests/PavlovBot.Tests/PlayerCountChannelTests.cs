using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Servers;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Live player counts as voice channel names.
/// </summary>
/// <remarks>
/// Replaced the player-list board. Two things are worth testing and neither needs a
/// gateway: the exact NAMES, because a channel name is the kind of thing a later edit
/// quietly reformats, and the decision NOT to write an unchanged one - which is the only
/// reason the feature stays inside Discord's two-renames-per-ten-minutes limit.
/// </remarks>
public class PlayerCountChannelTests
{
    /// <summary>Records renames, and reports whatever name was last written.</summary>
    private sealed class FakeChannels : IChannelRenamer
    {
        private readonly Dictionary<ulong, string> _names = [];

        public List<(ulong Channel, string Name)> Renames { get; } = [];
        public int NameReads { get; private set; }

        public FakeChannels(params (ulong Id, string Name)[] existing)
        {
            foreach (var (id, name) in existing) _names[id] = name;
        }

        public Task<string?> NameOfAsync(ulong channelId, CancellationToken ct)
        {
            NameReads++;
            return Task.FromResult(_names.TryGetValue(channelId, out var name) ? name : null);
        }

        public Task<bool> RenameAsync(ulong channelId, string name, CancellationToken ct)
        {
            Renames.Add((channelId, name));
            _names[channelId] = name;
            return Task.FromResult(true);
        }
    }

    // ---- the names ----

    [Fact]
    public void AServerChannelIsNamedCountOverCapacity()
    {
        Assert.Equal("Server 1: 3/24", PlayerCountChannels.ServerName(1, 3, 24));
        Assert.Equal("Server 2: 0/24", PlayerCountChannels.ServerName(2, 0, 24));
        Assert.Equal("Server 3: 24/24", PlayerCountChannels.ServerName(3, 24, 24));
    }

    [Fact]
    public void ADenominatorIsAlwaysShown()
    {
        /* "Server 1: 0" and "Server 1: 0/24" read as different things: the first looks like
           the feature is half working, and it is the version people saw exactly when a
           server was empty or unreachable - the moments they most want the channel to look
           deliberate. The capacity is resolved before the name is built (live, else last
           known, else the configured default), so there is never a missing one to render. */
        Assert.Equal("Server 1: 0/24", PlayerCountChannels.ServerName(1, 0, 24));
        Assert.Equal("Server 2: 3/32", PlayerCountChannels.ServerName(2, 3, 32));
    }

    [Fact]
    public void AnEmptyServerStillShowsItsCapacity()
    {
        /* The case that prompted this: "Server 1: 0" looks like a half-working feature, and
           it is exactly what people saw when a server was empty or had not answered yet -
           the moments the channel most needs to look deliberate. */
        Assert.Equal("Server 1: 0/24", PlayerCountChannels.ServerName(1, 0, 24));
    }

    [Fact]
    public void ANonStandardCapacityIsShownAsItself()
    {
        // The fallback is a last resort, not a fixed denominator. A server that reports 32
        // shows 32, so the default never misrepresents a server that has answered.
        Assert.Equal("Server 2: 5/32", PlayerCountChannels.ServerName(2, 5, 32));
        Assert.Equal("Server 3: 0/100", PlayerCountChannels.ServerName(3, 0, 100));
    }

    [Fact]
    public void TheTotalIsACountWithNoDenominator()
    {
        /* The only capacity available platform-wide is the sum of every listed server's max
           slots, which moves as servers come and go and which nobody is trying to fill.
           "287/7536" reads as a meaningful fraction and is not one. */
        Assert.Equal("Pavlov Shack: 287", PlayerCountChannels.TotalName(287));
        Assert.Equal("Pavlov Shack: 0", PlayerCountChannels.TotalName(0));
    }

    [Fact]
    public void NoNameCarriesAnEmoji()
    {
        // Asked for explicitly, and easy to reintroduce by accident later.
        foreach (var name in new[]
        {
            PlayerCountChannels.ServerName(1, 3, 24),
            PlayerCountChannels.ServerName(9, 0, 24),
            PlayerCountChannels.TotalName(287),
        })
        {
            Assert.All(name, c => Assert.True(char.IsAscii(c), $"\"{name}\" contains a non-ASCII character"));
        }
    }

    [Fact]
    public void TheCapacityComesFromPavlovsPlayerCountString()
    {
        /* Pavlov's ServerInfo has NO MaxPlayers field - the capacity is the denominator of
           PlayerCount, a string like "2/24". Reading only MaxPlayers meant every capacity in
           the bot came back null, which is why these channels read "Server 1 2". */
        Assert.Equal(24, PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity("2/24"));
        Assert.Equal(10, PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity("0/10"));
        Assert.Equal(24, PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity(" 2 / 24 "));
    }

    [Fact]
    public void AnUnreadableCapacityIsNullRatherThanInvented()
    {
        // A number the bot made up would show a server as full that nobody can join.
        Assert.Null(PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity(null));
        Assert.Null(PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity("2"));
        Assert.Null(PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity("2/"));
        Assert.Null(PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity("2/abc"));
        Assert.Null(PavlovBot.Rcon.Protocol.ServerInfoReply.Capacity("2/0"));
    }

    [Fact]
    public void TheTotalSumsEveryListedServer()
    {
        var servers = new[]
        {
            new ServerListing("a", "1.1.1.1", 1, 5, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
            new ServerListing("b", "1.1.1.2", 1, 0, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
            new ServerListing("c", "1.1.1.3", 1, 12, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
        };

        Assert.Equal(17, PlayerCountChannels.TotalPlayers(servers));
        Assert.Equal(0, PlayerCountChannels.TotalPlayers([]));
    }

    // ---- the rate limit ----

    private static PlayerCountChannels Build(IChannelRenamer channels)
    {
        var options = new BotOptions
        {
            DiscordToken = "t",
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 9100, Password = "x" }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        };

        var rcon = new RconRegistry(options, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);
        var master = new MasterServerList(new HttpClient(), NullLogger<MasterServerList>.Instance);

        /* A unit list with the same shape as the channel list, so the number-to-server
           mapping resolves. The units do not exist on this box, so StateAsync answers
           Unknown - which is the point: an unanswerable status check must fall through to
           the roster logic rather than relabelling every server offline. */
        var services = new ServiceControl(ServiceControl.DefaultUnits, useSudo: false,
            NullLogger<ServiceControl>.Instance);

        return new PlayerCountChannels(channels, rcon, master, services, NullLogger<PlayerCountChannels>.Instance);
    }

    [Fact]
    public async Task AStaleRosterDoesNotOverwriteTheName()
    {
        /* An unreachable server and an empty one are indistinguishable from a roster. A blip
           would otherwise rename the channel to "0/24" and back, spending BOTH renames in
           the ten-minute window on a number that was never true - so when the real count
           came back there would be no allowance left to publish it.

           The roster here has never been fetched, which is the state right after a restart. */
        var channels = new FakeChannels((100UL, "Server 1: 5/24"));

        await Build(channels).TickAsync(new PlayerCountChannels.Targets([100UL], null));

        Assert.Empty(channels.Renames);
    }

    [Fact]
    public async Task AChannelTheBotCannotSeeIsReportedRatherThanRetriedBlindly()
    {
        // No entry for this id, so NameOfAsync returns null.
        var channels = new FakeChannels();

        await Build(channels).TickAsync(new PlayerCountChannels.Targets([], TotalChannel: 999UL));

        Assert.Empty(channels.Renames);
    }

    [Fact]
    public async Task MoreChannelsThanServersIsNotAnError()
    {
        /* Three channel ids with one server configured. It must not throw, and it must not
           rename the extra channels to somebody else's count. */
        var channels = new FakeChannels((100UL, "Server 1: 0/24"), (101UL, "Server 2: 0/24"), (102UL, "Server 3: 0/24"));

        var thrown = await Record.ExceptionAsync(() =>
            Build(channels).TickAsync(new PlayerCountChannels.Targets([100UL, 101UL, 102UL], null)));

        Assert.Null(thrown);
        Assert.Empty(channels.Renames);
    }

    [Fact]
    public async Task NoChannelsConfiguredDoesNothing()
    {
        var channels = new FakeChannels();

        await Build(channels).TickAsync(new PlayerCountChannels.Targets([], null));

        Assert.Empty(channels.Renames);
        Assert.Equal(0, channels.NameReads);
    }

    [Fact]
    public async Task TheRunReportsOneLinePerChannel()
    {
        /* /counts renders exactly this, so it has to say something for every configured
           channel - a channel missing from the report reads as a channel that was skipped
           for an unknown reason, which is the state the whole feature keeps ending up in. */
        var channels = new FakeChannels((100UL, "Server 1: 0/24"));

        var report = await Build(channels).RunAsync(
            new PlayerCountChannels.Targets([100UL], TotalChannel: 999UL));

        Assert.Equal(2, report.Count);
        Assert.All(report, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }

    [Fact]
    public async Task AChannelTheBotCannotSeeSaysSoInTheReport()
    {
        // The words /counts keys its "most likely" hint off, so they have to be there.
        var report = await Build(new FakeChannels()).RunAsync(
            new PlayerCountChannels.Targets([], TotalChannel: 999UL));

        Assert.Contains(report, line => line.Contains("FAILED", StringComparison.Ordinal));
        Assert.Contains(report, line => line.Contains("not visible", StringComparison.Ordinal));
    }
}
