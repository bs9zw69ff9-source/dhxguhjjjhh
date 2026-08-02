using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

public class BoardsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-boards-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly string _ledgers;
    private readonly Boards _boards;

    public BoardsTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());

        var options = new BotOptions
        {
            DiscordToken = "t",
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 9100, Password = "x" }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        };
        var rcon = new RconRegistry(options, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

        _ledgers = Path.Combine(_directory, "modsave");
        Directory.CreateDirectory(_ledgers);
        _boards = new Boards(_store, rcon, _ledgers);
    }

    [Fact]
    public async Task ABoardWithNoDataYetStillPostsSomething()
    {
        /* The cash and most-wanted boards REPLACED a board the Node bot posts to the same
           channel under the same autopost key. Returning null there means "skip this cycle
           and leave what is already posted" - which on a cutover leaves the Node board
           frozen in the channel while ours never appears at all.

           BuildStaffBoard keeps returning null because nothing else has ever occupied its
           channel: skipping is right when the only thing at stake is a transient read
           failure overwriting real numbers with "no data". */
        Assert.NotNull(_boards.BuildArrestBoard());
        Assert.NotNull(_boards.BuildCashBoard());
        Assert.Null(_boards.BuildStaffBoard());
        await Task.CompletedTask;
    }

    [Fact]
    public void AnEmptyLedgerDirectoryStillPostsABoard()
    {
        /* NOT null. Null skips the cycle and leaves whatever is already in the channel -
           which is the PLAYTIME board this one replaced. Skipping would leave a playtime
           leaderboard in the cash channel indefinitely, looking like it was still live. */
        var board = _boards.BuildCashBoard();

        Assert.NotNull(board);
        Assert.Contains("Richest", board!.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Playtime", board.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCashBoardRanksByBalance()
    {
        /* LEADERBOARD_CHANNEL is the CASH board. The port wired the playtime board here,
           which put a playtime leaderboard in a channel called cash-leaderboard. */
        File.WriteAllText(Path.Combine(_ledgers, "Alice.txt"), "1250");
        File.WriteAllText(Path.Combine(_ledgers, "Bob.txt"), "40");

        var board = _boards.BuildCashBoard();

        Assert.NotNull(board);
        Assert.Contains("$1,250", board!.Description, StringComparison.Ordinal);
        Assert.Contains("Richest", board.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Playtime", board.Title, StringComparison.Ordinal);
        Assert.True(board.Description.IndexOf("Alice", StringComparison.Ordinal) <
                    board.Description.IndexOf("Bob", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCombinedTotalCoversEveryLedgerNotJustTheRowsShown()
    {
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_ledgers, $"p{i:00}.txt"), "100");

        var board = _boards.BuildCashBoard();

        // 20 x 100, even though only the top 15 are listed.
        Assert.Contains("$2,000", board!.Description, StringComparison.Ordinal);
        Assert.Contains("**20** ledger(s)", board.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBanlistIsNotALedger()
    {
        // It lives in the same directory and would otherwise appear as a player.
        File.WriteAllText(Path.Combine(_ledgers, "banlist.txt"), "0");
        File.WriteAllText(Path.Combine(_ledgers, "Alice.txt"), "10");

        Assert.DoesNotContain("banlist", _boards.BuildCashBoard()!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AHalfWrittenLedgerIsSkippedRatherThanReadAsZero()
    {
        /* Reading an unparseable balance as 0 would announce that somebody had lost all
           their money, in a channel everybody watches. */
        File.WriteAllText(Path.Combine(_ledgers, "Alice.txt"), "1000");
        File.WriteAllText(Path.Combine(_ledgers, "Bob.txt"), "");

        var board = _boards.BuildCashBoard();

        Assert.DoesNotContain("Bob", board!.Description, StringComparison.Ordinal);
        Assert.Contains("**1** ledger(s)", board.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableLedgerPathReportsTheProblemRatherThanSkipping()
    {
        /* A wrong MODSAVE_PATH must not present as a board that simply never updates -
           there would be nothing anywhere saying why. */
        var rcon = new RconRegistry(
            new BotOptions
            {
                DiscordToken = "t",
                Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 9100, Password = "x" }],
                Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
                DataDirectory = _directory,
            },
            new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

        var board = new Boards(_store, rcon, ledgerDirectory: null).BuildCashBoard();

        Assert.NotNull(board);
        Assert.Contains("MODSAVE_PATH", board!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMostWantedBoardRanksByJailTimeNotArrestCount()
    {
        // One long sentence outranks several short ones - that is what "most wanted" means.
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync(Datasets.Arrests, new Dictionary<string, List<Arrest>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = [new Arrest("Alice", ["PC 210"], 120, 0, "Dana", now)],
            ["Bob"] = [
                new Arrest("Bob", ["PC 100"], 2, 25, "Dana", now),
                new Arrest("Bob", ["PC 100"], 2, 25, "Dana", now),
                new Arrest("Bob", ["PC 100"], 2, 25, "Dana", now),
            ],
        });

        var board = _boards.BuildArrestBoard();
        Assert.NotNull(board);
        Assert.True(board!.Description.IndexOf("Alice", StringComparison.Ordinal) <
                    board.Description.IndexOf("Bob", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheStaffBoardCountsActionsPerModerator()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync<List<ModAction>>(Datasets.ModLog,
        [
            new ModAction("ban", "Dana", "Alice", null, now),
            new ModAction("kick", "Dana", "Bob", null, now),
            new ModAction("ban", "Sam", "Carol", null, now),
        ]);

        var board = _boards.BuildStaffBoard();
        Assert.NotNull(board);
        Assert.Contains("Dana", board!.Description, StringComparison.Ordinal);
        Assert.True(board.Description.IndexOf("Dana", StringComparison.Ordinal) <
                    board.Description.IndexOf("Sam", StringComparison.Ordinal));
    }

    [Fact]
    public void APlayerNameCannotInjectFormattingIntoABoard()
    {
        // Names are attacker-controlled, and a board is staff-facing.
        File.WriteAllText(Path.Combine(_ledgers, "ev`il.txt"), "10");

        Assert.DoesNotContain("ev`il", _boards.BuildCashBoard()!.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlaytimeTickingWithNobodyOnlineWritesNothing()
    {
        await _boards.TickPlaytimeAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(_boards.Playtime());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
