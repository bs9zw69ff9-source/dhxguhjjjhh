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

        Assert.Contains("`Alice` — NYPD", text, StringComparison.Ordinal);
        Assert.Contains("`Bob`", text, StringComparison.Ordinal);
        // The civilian gets no tag at all rather than an invented one.
        Assert.DoesNotContain("`Bob` —", text, StringComparison.Ordinal);
        Assert.Contains("**2** online", board!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullServerSaysHowManyNamesItCouldNotShow()
    {
        /* THE FAILURE THIS RULES OUT: Discord caps a field at 1024 and THROWS from Build()
           past it, so a naive board either dies on a busy server or - once something catches
           the throw - quietly lists the alphabetical first twenty as though they were
           everybody. "Is so-and-so on" would then answer no for half the server. */
        var crowd = Enumerable.Range(1, 200)
            .Select(i => new BoardPlayer($"Player{i:000}", "NYPD"))
            .ToArray();

        var board = PlayerBoard.Build(
            [Server("server1", TimeSpan.FromSeconds(5), players: crowd)], DateTimeOffset.UtcNow);

        Assert.NotNull(board);
        Assert.All(board!.Fields, f => Assert.True(f.Value.Length <= 1024, $"field was {f.Value.Length} characters"));
        Assert.Contains("more", Values(board!), StringComparison.Ordinal);
        Assert.Contains("200 online", Values(board!), StringComparison.Ordinal);
        Assert.True(board!.Length < 6000, $"embed was {board!.Length} characters");
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
        Assert.Contains("`Alice`", text, StringComparison.Ordinal);
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
        Assert.Contains("**1** online across 1 server(s)", board!.Description, StringComparison.Ordinal);
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
                Path.Combine(_directory, "_bak")));

        var board = await boards.BuildPlayerBoardAsync(CancellationToken.None);

        Assert.NotNull(board);
        var text = Values(board!);

        Assert.Contains($"`Alice` — {Nypd.Name}", text, StringComparison.Ordinal);
        Assert.Contains("`Bob`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("`Bob` —", text, StringComparison.Ordinal);

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
        Assert.Contains("`Alice`", Values(board!), StringComparison.Ordinal);

        await rcon.DisposeAsync();
    }

    [Fact]
    public async Task AffiliationsAgreeWithTheOneAtATimeLookup()
    {
        /* The bulk lookup exists so the board does not re-read every rank file per player.
           It is only worth having while it answers what FindAsync answers. */
        var rosters = new RosterService(_directory, NullLogger<RosterService>.Instance,
            Path.Combine(_directory, "_bak"));

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
