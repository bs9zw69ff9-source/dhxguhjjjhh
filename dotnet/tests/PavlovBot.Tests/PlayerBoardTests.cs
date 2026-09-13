using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The player board the port dropped, and the faction tag it is posted for.
/// </summary>
/// <remarks>
/// Two classes of failure are covered here, and the second is the one that would actually
/// reach a channel: a board that says "nobody online" when it has no data, and a board whose
/// list is silently cut short by Discord's 1024-character field limit on a busy server. Both
/// read as the truth to anyone looking at them.
/// </remarks>
public class PlayerBoardTests : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pavlovbot-playerboard-" + Guid.NewGuid().ToString("N"));

    private readonly FakeRconServer _server = new();

    private static readonly FactionDefinition Nypd = FactionRegistry.Get("NYPD")!;

    public PlayerBoardTests() => Directory.CreateDirectory(_directory);

    // ---- the renderer, over inputs the live path cannot easily be talked into ----

    private static BoardRoster Server(
        string name, TimeSpan? age, bool stale = false, string? problem = null, params BoardPlayer[] players) =>
        new(name, players, age, stale, problem);

    private static string Values(Discord.Embed embed) =>
        string.Join("\n", embed.Fields.Select(f => $"{f.Name}\n{f.Value}"));

    [Fact]
    public void EveryNameCarriesItsFactionAndCiviliansCarryNone()
    {
        var board = PlayerBoard.Build(
        [
            Server("server1", TimeSpan.FromSeconds(10),
                players: [new BoardPlayer("Alice", "NYPD"), new BoardPlayer("Bob", null)]),
        ], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains("• Alice — NYPD", text, StringComparison.Ordinal);
        Assert.Contains("• Bob", text, StringComparison.Ordinal);
        // The civilian gets no tag at all rather than an invented one.
        Assert.DoesNotContain("• Bob —", text, StringComparison.Ordinal);
        Assert.Equal("Live Player List", board!.Title);
        Assert.Contains("**2**", board!.Description, StringComparison.Ordinal);
        Assert.Contains("Server 1 (2)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerIsNamedTheWayThePlayerCountChannelsNameIt()
    {
        // server2 is a config key from the RCON_HOST_n index, not a name anybody chose, and
        // the voice counters beside this board already say "Server 2" in public.
        var board = PlayerBoard.Build(
        [
            Server("server2", TimeSpan.FromSeconds(5), players: [new BoardPlayer("Alice", null)]),
            Server("Mojave Outpost", TimeSpan.FromSeconds(5), players: [new BoardPlayer("Bob", null)]),
        ], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains("Server 2 (1)", text, StringComparison.Ordinal);
        // A name somebody actually chose is left alone.
        Assert.Contains("Mojave Outpost (1)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServersSitSideBySide()
    {
        /* THE LAYOUT. Discord lays inline fields out three to a row, and three columns of
           names is the whole shape of this board - one stacked field per server is a
           different thing entirely on a phone. */
        var board = PlayerBoard.Build(
        [
            Server("server1", TimeSpan.FromSeconds(5), players: [new BoardPlayer("Alice", "NYPD")]),
            Server("server2", TimeSpan.FromSeconds(5), players: [new BoardPlayer("Bob", "NCR")]),
            Server("server3", TimeSpan.FromSeconds(5), players: [new BoardPlayer("Carol", null)]),
        ], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        Assert.Equal(3, board!.Fields.Length);
        Assert.All(board!.Fields, f => Assert.True(f.Inline, $"{f.Name} was not inline"));
    }

    [Fact]
    public void ANameCannotFormatTheNamesBelowIt()
    {
        /* The cost of printing names plain instead of in code spans: an unclosed marker in
           one name restyles the rest of the column. Escaped, not stripped - the board has to
           agree with what an admin types back into a command. */
        var board = PlayerBoard.Build(
        [
            Server("server1", TimeSpan.FromSeconds(5),
                players: [new BoardPlayer("*Ghost", "NCR"), new BoardPlayer("Butter_Life", null)]),
        ], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains(@"\*Ghost", text, StringComparison.Ordinal);
        Assert.Contains(@"Butter\_Life", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullServerSaysHowManyNamesItCouldNotShow()
    {
        /* THE FAILURE THIS RULES OUT: Discord caps a field at 1024 and THROWS from Build()
           past it, so a naive board either dies on a busy server or - once something catches
           the throw - quietly lists the alphabetical first twenty as though they were
           everybody. "Is so-and-so on" would then answer no for half the server. */
        // Past what four columns hold, so the cut is exercised rather than assumed. A real
        // Pavlov server caps well under one column's worth.
        var crowd = Enumerable.Range(1, 400)
            .Select(i => new BoardPlayer($"Player{i:000}", "Brotherhood of Steel"))
            .ToArray();

        var board = PlayerBoard.Build(
            [Server("server1", TimeSpan.FromSeconds(5), players: crowd)], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        Assert.All(board!.Fields, f => Assert.True(f.Value.Length <= 1024, $"field was {f.Value.Length} characters"));
        Assert.Contains("Server 1 (400)", Values(board!), StringComparison.Ordinal);
        Assert.True(board!.Length < 6000, $"embed was {board!.Length} characters");

        // It CONTINUES rather than stopping at one field, and what it still cannot fit is
        // counted rather than dropped in silence.
        Assert.True(board!.Fields.Length > 1, "the list did not continue into another column");
        Assert.Contains("more", Values(board!), StringComparison.Ordinal);

        // Every name shown is shown once: the continuation is a split, not a re-listing.
        Assert.Equal(1, Values(board!).Split("• Player001").Length - 1);
    }

    [Fact]
    public void AStaleRosterIsMarkedAndStillListsTheNames()
    {
        /* An old list is worth something as long as you know that is what you are looking at.
           /players learnt this one live: an hour-old roster read as live. */
        var board = PlayerBoard.Build(
        [
            Server("server1", TimeSpan.FromMinutes(42), stale: true,
                players: [new BoardPlayer("Alice", "NYPD")]),
        ], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains("42 minutes ago", text, StringComparison.Ordinal);
        Assert.Contains("• Alice", text, StringComparison.Ordinal);
        Assert.Equal(Theme.Amber, board!.Color);
    }

    [Fact]
    public void AServerThatHasNotAnsweredIsNotReportedAsEmpty()
    {
        var board = PlayerBoard.Build(
        [
            Server("server1", age: null, problem: "the reply could not be parsed"),
            Server("server2", TimeSpan.FromSeconds(5), players: [new BoardPlayer("Alice", null)]),
        ], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains("no roster yet", text, StringComparison.Ordinal);
        Assert.Contains("the reply could not be parsed", text, StringComparison.Ordinal);
        // One online on the server that answered, NOT "nobody online" on the one that did not.
        Assert.Contains("**1** *courier ", board!.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody online", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyServerSaysNobodyRatherThanNothing()
    {
        var board = PlayerBoard.Build(
            [Server("server1", TimeSpan.FromSeconds(5))], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        Assert.Contains("nobody online", Values(board!), StringComparison.Ordinal);
        Assert.Equal(Theme.Green, board!.Color);
    }

    [Fact]
    public void TheRestartWindowPostsNothingAtAll()
    {
        /* Services start before the first sweep lands. Posting then overwrites a good list
           with "no roster yet" on every restart, which is exactly what null is for. */
        Assert.Null(PlayerBoard.Build([Server("server1", age: null), Server("server2", age: null)],
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ASweepThatIsFailingPostsAnywayAndSaysWhy()
    {
        /* NOT the restart window: this never recovers on its own, so staying silent means a
           board channel that is empty forever with nothing saying why. */
        var board = PlayerBoard.Build(
            [Server("server1", age: null, problem: "RCON refused the command")], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        Assert.Contains("RCON refused the command", Values(board!), StringComparison.Ordinal);
    }

    // ---- end to end, from the wire and the roster files ----

    [Fact]
    public async Task TheTagComesOffTheRosterFilesByWayOfTheRconSweep()
    {
        File.WriteAllText(Path.Combine(_directory, Nypd.RankFiles[Nypd.Lowest]), "Alice\n");

        _server.ExactReply =
            "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[" +
            "{\"Username\":\"Alice\",\"UniqueId\":\"1\"}," +
            "{\"Username\":\"Bob\",\"UniqueId\":\"2\"}]}\r\n";

        var rcon = Registry();
        await rcon.RefreshRostersAsync(CancellationToken.None);

        var boards = new Boards(
            new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec()),
            rcon,
            ledgerDirectory: null,
            new RosterService(_directory, NullLogger<RosterService>.Instance,
                Path.Combine(_directory, "_bak"), factions: FactionRegistry.Police));

        var board = await boards.BuildPlayerBoardAsync(CancellationToken.None);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains($"• Alice — {Nypd.Name}", text, StringComparison.Ordinal);
        Assert.Contains("• Bob", text, StringComparison.Ordinal);
        Assert.DoesNotContain("• Bob —", text, StringComparison.Ordinal);

        await rcon.DisposeAsync();
    }

    [Fact]
    public async Task NoRosterDirectoryLeavesTheNamesUntaggedRatherThanOffTheBoard()
    {
        // An install with no FACTION_ROLES_PATH still gets a player board.
        _server.ExactReply =
            "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[" +
            "{\"Username\":\"Alice\",\"UniqueId\":\"1\"}]}\r\n";

        var rcon = Registry();
        await rcon.RefreshRostersAsync(CancellationToken.None);

        var boards = new Boards(
            new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec()), rcon);

        var board = await boards.BuildPlayerBoardAsync(CancellationToken.None);

        Assert.NotNull(board);
        Assert.Contains("• Alice", Values(board!), StringComparison.Ordinal);

        await rcon.DisposeAsync();
    }

    [Fact]
    public async Task AffiliationsAgreeWithTheOneAtATimeLookup()
    {
        /* The bulk lookup exists so the board does not re-read every rank file per player.
           It is only worth having while it answers what FindAsync answers. */
        var rosters = new RosterService(_directory, NullLogger<RosterService>.Instance,
            Path.Combine(_directory, "_bak"), factions: FactionRegistry.Police);

        await rosters.JoinAsync(Nypd, "Alice");
        await rosters.ChangeRankAsync(Nypd, "Alice", +1);

        var found = await rosters.FindAsync("Alice");
        var all = await rosters.AffiliationsAsync();

        Assert.NotNull(found);
        Assert.Equal(found!.Faction.Name, all["Alice"].Faction.Name);
        Assert.Equal(found!.Rank, all["Alice"].Rank);
        // Keyed the way every other name lookup in the bot is.
        Assert.True(all.ContainsKey("alice"));
        Assert.False(all.ContainsKey("Bob"));
    }

    private RconRegistry Registry() => new(
        new BotOptions
        {
            DiscordToken = "t",
            Servers =
            [
                new RconOptions
                {
                    Name = "server1",
                    Host = "127.0.0.1",
                    Port = _server.Port,
                    Password = _server.Password,
                    CommandSpacing = TimeSpan.Zero,
                    ReadCacheDuration = TimeSpan.Zero,
                    CommandTimeout = TimeSpan.FromSeconds(5),
                    MaxAttempts = 1,
                },
            ],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        },
        new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
