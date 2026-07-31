using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The record staff and players are SHOWN, as distinct from the application log. It has to
/// survive a restart, which the application log does not.
/// </summary>
public class AuditLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-audit-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly AuditLog _audit;

    public AuditLogTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _audit = new AuditLog(_store);
    }

    [Fact]
    public async Task ActionsAreRecordedAndReadBack()
    {
        await _audit.RecordAsync("tempban", "Dana", "Alice", "Cheating");
        var action = _audit.All().Single();

        Assert.Equal("tempban", action.Action);
        Assert.Equal("Dana", action.Moderator);
        Assert.Equal("Alice", action.Player);
        Assert.Equal("Cheating", action.Reason);
    }

    [Fact]
    public async Task ByModeratorIsMostRecentFirst()
    {
        await _audit.RecordAsync("kick", "Dana", "Alice");
        await _audit.RecordAsync("tempban", "Dana", "Bob");
        await _audit.RecordAsync("kick", "Sam", "Carol");

        var dana = _audit.By("Dana");
        Assert.Equal(2, dana.Count);
        Assert.Equal("Bob", dana[0].Player);
    }

    [Fact]
    public async Task ActionsInTheSameMillisecondStillReadNewestFirst()
    {
        /* The test above only covers this by accident. Timestamps are stored as whole
           milliseconds, so a staffer acting twice in one burst produces two entries with an
           IDENTICAL `At`, and a stable sort then leaves them in append order - oldest
           first, the exact opposite of what the ordering promises.

           Twenty back-to-back writes guarantee ties instead of hoping for them, and the
           assertion is the full order rather than just the head, so a partial ordering
           cannot pass. This failed reliably before `NewestFirst` reversed the source. */
        for (var i = 0; i < 20; i++)
            await _audit.RecordAsync("kick", "Dana", "Player" + i);

        var dana = _audit.By("Dana");

        /* Self-validating: each write touches the filesystem, and on a slow enough machine
           every one could land in its own millisecond - leaving this passing while covering
           nothing. Assert that ties are actually present, so the test fails loudly if it
           ever stops exercising the case it exists for. */
        Assert.True(dana.Select(a => a.At).Distinct().Count() < dana.Count,
            "no two entries shared a millisecond, so this run did not test tie-breaking");

        Assert.Equal(Enumerable.Range(0, 20).Reverse().Select(i => "Player" + i),
            dana.Select(a => a.Player));
    }

    [Fact]
    public async Task AgainstAPlayerIsAlsoNewestFirstWithinAMillisecond()
    {
        // Same defect, same fix, the other read path - `Against` had its own copy of the sort.
        for (var i = 0; i < 20; i++)
            await _audit.RecordAsync("kick", "Mod" + i, "Alice");

        var against = _audit.Against("Alice");

        Assert.True(against.Select(a => a.At).Distinct().Count() < against.Count,
            "no two entries shared a millisecond, so this run did not test tie-breaking");

        Assert.Equal(Enumerable.Range(0, 20).Reverse().Select(i => "Mod" + i),
            against.Select(a => a.Moderator));
    }

    [Fact]
    public async Task ByModeratorCanBeWindowed()
    {
        await _audit.RecordAsync("kick", "Dana", "Alice");

        Assert.Single(_audit.By("Dana", DateTimeOffset.UtcNow.AddMinutes(-5)));
        Assert.Empty(_audit.By("Dana", DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task AgainstAPlayerFindsEverythingDoneToThem()
    {
        await _audit.RecordAsync("kick", "Dana", "Alice");
        await _audit.RecordAsync("tempban", "Sam", "alice");   // case-insensitive

        Assert.Equal(2, _audit.Against("Alice").Count);
    }

    [Fact]
    public async Task TheLogIsBoundedAndTrimsTheOldestFirst()
    {
        /* The whole dataset is deserialised on every read, so an unbounded log eventually
           takes a second to parse. The entries anybody asks about are recent. */
        for (var i = 0; i < AuditLog.MaxEntries + 20; i++)
            await _audit.RecordAsync("kick", "Dana", $"Player{i}");

        var all = _audit.All();
        Assert.Equal(AuditLog.MaxEntries, all.Count);

        // The first 20 are gone; the newest survives.
        Assert.DoesNotContain(all, a => a.Player == "Player0");
        Assert.Contains(all, a => a.Player == $"Player{AuditLog.MaxEntries + 19}");
    }

    [Fact]
    public async Task AReasonCannotCarryANewlineIntoTheRecord()
    {
        await _audit.RecordAsync("tempban", "Dana", "Alice", "line one\nline two");
        Assert.Equal("line one line two", _audit.All().Single().Reason);
    }

    [Fact]
    public async Task TheStaffBoardReadsWhatTheAuditLogWrites()
    {
        /* These were built separately and nothing connected them - the board read a dataset
           no code path wrote, so it was permanently empty. */
        await _audit.RecordAsync("tempban", "Dana", "Alice");
        await _audit.RecordAsync("kick", "Dana", "Bob");

        var options = new PavlovBot.Host.Configuration.BotOptions
        {
            DiscordToken = "t",
            Servers = [],
            Monitoring = new PavlovBot.Host.Configuration.MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        };
        var rcon = new PavlovBot.Host.Rcon.RconRegistry(options,
            new PavlovBot.Host.Observability.MetricsRegistry(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PavlovBot.Host.Rcon.RconRegistry>.Instance);

        var board = new Boards(_store, rcon).BuildStaffBoard();

        Assert.NotNull(board);
        Assert.Contains("Dana", board!.Description, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
