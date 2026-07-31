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
        _boards = new Boards(_store, rcon);
    }

    [Fact]
    public async Task AnEmptyBoardReturnsNull_SoTheCycleIsSkippedRatherThanClearingIt()
    {
        /* Null means "skip this cycle". An empty board would overwrite yesterday's real one
           with "no data" during a restart or a transient read failure. */
        Assert.Null(_boards.BuildPlaytimeBoard());
        Assert.Null(_boards.BuildArrestBoard());
        Assert.Null(_boards.BuildStaffBoard());
        await Task.CompletedTask;
    }

    [Fact]
    public void ARosterThatHasNeverBeenFetchedIsNotAnEmptyServer()
    {
        /* Saying "nobody is on" because the sweep has not run yet is a lie the board would
           tell every single time the bot restarts. */
        Assert.Null(_boards.BuildPlayerList());
    }

    [Fact]
    public async Task PlaytimeAccumulates()
    {
        await _store.WriteAsync(Datasets.Playtime, new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alice"] = new("Alice", 125, DateTimeOffset.UtcNow),
            ["Bob"] = new("Bob", 40, DateTimeOffset.UtcNow),
        });

        var board = _boards.BuildPlaytimeBoard();
        Assert.NotNull(board);
        Assert.Contains("Alice", board!.Description, StringComparison.Ordinal);
        Assert.Contains("2h 5m", board.Description, StringComparison.Ordinal);
        // Highest first.
        Assert.True(board.Description.IndexOf("Alice", StringComparison.Ordinal) <
                    board.Description.IndexOf("Bob", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlayersWithNoRecordedTimeAreNotListed()
    {
        await _store.WriteAsync(Datasets.Playtime, new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ghost"] = new("Ghost", 0, null),
        });
        Assert.Null(_boards.BuildPlaytimeBoard());
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
    public async Task APlayerNameCannotInjectFormattingIntoABoard()
    {
        // Names are attacker-controlled, and a board is staff-facing.
        await _store.WriteAsync(Datasets.Playtime, new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["ev`il"] = new("ev`il", 10, DateTimeOffset.UtcNow),
        });

        Assert.DoesNotContain("ev`il", _boards.BuildPlaytimeBoard()!.Description, StringComparison.Ordinal);
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
