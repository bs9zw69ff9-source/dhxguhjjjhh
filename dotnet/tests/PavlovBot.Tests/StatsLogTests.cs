using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Reading Pavlov's Stats.log.
/// </summary>
/// <remarks>
/// THE SAMPLE IS REAL, copied from a live server, and the assertions are pinned to it rather
/// than to what the format looked like it ought to be. The repo has been caught out before
/// by the gap between what a log reads like and what is actually written.
/// </remarks>
public class StatsLogReaderTests
{
    /// <summary>Verbatim from a running server, tabs and all.</summary>
    private const string Sample =
        "[2026.09.11-19.45.16] StatManagerLog: {\n" +
        "\t\"RoundState\":\n" +
        "\t{\n" +
        "\t\t\"State\": \"Starting\",\n" +
        "\t\t\"Timestamp\": \"2026.09.11-15.45.16\"\n" +
        "\t}\n" +
        "}\n" +
        "[2026.09.11-20.01.15] StatManagerLog: {\n" +
        "\t\"KillData\":\n" +
        "\t{\n" +
        "\t\t\"Killer\": \"Holosight1\",\n" +
        "\t\t\"Killed\": \"LxPXHam\",\n" +
        "\t\t\"KilledBy\": \"10mmpistol\",\n" +
        "\t\t\"Headshot\": false\n" +
        "\t}\n" +
        "}\n" +
        "[2026.09.11-20.01.29] StatManagerLog: {\n" +
        "\t\"KillData\":\n" +
        "\t{\n" +
        "\t\t\"Killer\": \"Holosight1\",\n" +
        "\t\t\"Killed\": \"Holosight1\",\n" +
        "\t\t\"KilledBy\": \"10mmpistol\",\n" +
        "\t\t\"Headshot\": true\n" +
        "\t}\n" +
        "}\n" +
        "[2026.09.11-20.30.19] StatManagerLog: {\n" +
        "\t\"KillData\":\n" +
        "\t{\n" +
        "\t\t\"Killer\": \"LxPXHam\",\n" +
        "\t\t\"Killed\": \"LxPXHam\",\n" +
        "\t\t\"KilledBy\": \"None\",\n" +
        "\t\t\"Headshot\": false\n" +
        "\t}\n" +
        "}\n";

    private static List<object> ReadAll(string text, StatsLogReader? reader = null)
    {
        reader ??= new StatsLogReader();
        var records = new List<object>();
        foreach (var line in text.Split('\n')) records.AddRange(reader.Read(line));
        return records;
    }

    [Fact]
    public void TheSampleParsesIntoOneRoundStateAndThreeKills()
    {
        var records = ReadAll(Sample);

        Assert.Equal(4, records.Count);
        Assert.IsType<StatsRoundState>(records[0]);
        Assert.Equal(3, records.OfType<StatsKill>().Count());
    }

    [Fact]
    public void AKillCarriesTheWeaponTheHeadshotFlagAndItsOwnTimestamp()
    {
        var kill = ReadAll(Sample).OfType<StatsKill>().First();

        Assert.Equal("Holosight1", kill.Killer);
        Assert.Equal("LxPXHam", kill.Killed);
        Assert.Equal("10mmpistol", kill.Weapon);
        Assert.False(kill.Headshot);
        Assert.False(kill.Suicide);

        /* THE POINT OF USING THIS FILE AT ALL. The Pavlov.log route reassembles a kill from
           fragments that carry no time of their own, so it was stamped with the clock at the
           moment the poll noticed - a poll interval late, and much further while catching up
           a backlog after a restart. */
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 20, 1, 15, TimeSpan.Zero), kill.At);
    }

    [Fact]
    public void TheBracketStampIsReadAsUtc()
    {
        /* The RoundState block carries its own local "Timestamp", and in this sample the two
           differ by exactly four hours - 19.45.16 against 15.45.16, which is Eastern in
           September. The bracket is the UTC one, and it is the one used because it does not
           move twice a year. */
        var round = Assert.IsType<StatsRoundState>(ReadAll(Sample)[0]);

        Assert.Equal("Starting", round.State);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 19, 45, 16, TimeSpan.Zero), round.At);
        Assert.Equal(TimeSpan.Zero, round.At.Offset);
    }

    [Fact]
    public void ASelfKillIsMarkedASuicide()
    {
        var kills = ReadAll(Sample).OfType<StatsKill>().ToList();

        Assert.True(kills[1].Suicide);
        Assert.True(kills[1].Headshot);
        Assert.Equal("10mmpistol", kills[1].Weapon);
    }

    [Fact]
    public void KilledByNoneMeansNoWeapon()
    {
        // A fall, a drown, the map. "None" is the game's way of writing it and must not
        // reach a feed as a weapon called None.
        var last = ReadAll(Sample).OfType<StatsKill>().Last();

        Assert.True(last.Suicide);
        Assert.Null(last.Weapon);
    }

    [Fact]
    public void ABlockSplitAcrossTwoPollsIsStillRead()
    {
        /* The reason this is a state machine and not a line matcher. A block is several
           lines, the tail polls every 1.5 seconds, and a poll routinely lands in the middle
           of one. */
        var reader = new StatsLogReader();
        var lines = Sample.Split('\n');

        var first = new List<object>();
        foreach (var line in lines.Take(10)) first.AddRange(reader.Read(line));

        var second = new List<object>();
        foreach (var line in lines.Skip(10)) second.AddRange(reader.Read(line));

        Assert.Equal(4, first.Count + second.Count);
    }

    [Fact]
    public void AnUnclosedBlockIsDroppedWhenTheNextOneStarts()
    {
        /* Rotation mid-block. Stats.log rotates about hourly, so the tailer starts the new
           file at zero and the old block's closing brace never arrives. Carrying the
           fragment forward would corrupt the next block too. */
        var reader = new StatsLogReader();

        var records = ReadAll(
            "[2026.09.11-20.01.15] StatManagerLog: {\n" +
            "\t\"KillData\":\n" +
            "\t{\n" +
            "\t\t\"Killer\": \"Holosight1\",\n" +
            Sample, reader);

        Assert.Equal(1, reader.Discarded);
        Assert.Equal(4, records.Count);   // the sample still parses in full
    }

    [Fact]
    public void AMalformedBlockIsSkippedRatherThanThrowing()
    {
        // This runs inside log ingestion; one bad record must not cost the rest of the batch.
        var records = ReadAll(
            "[2026.09.11-20.01.15] StatManagerLog: {\n" +
            "\t\"KillData\": not json at all\n" +
            "}\n" +
            Sample);

        Assert.Equal(4, records.Count);
    }

    [Fact]
    public void AKillMissingANameIsNotReported()
    {
        // Both names are needed - a kill naming nobody cannot be shown as anything.
        var records = ReadAll(
            "[2026.09.11-20.01.15] StatManagerLog: {\n" +
            "\t\"KillData\":\n" +
            "\t{\n" +
            "\t\t\"Killed\": \"LxPXHam\",\n" +
            "\t\t\"KilledBy\": \"10mmpistol\"\n" +
            "\t}\n" +
            "}\n");

        Assert.Empty(records);
    }

    [Fact]
    public void AnUnknownRecordTypeIsIgnoredQuietly()
    {
        // The game writes more kinds than this reads, and will write more still.
        var records = ReadAll(
            "[2026.09.11-20.01.15] StatManagerLog: {\n" +
            "\t\"SomethingElse\":\n" +
            "\t{\n" +
            "\t\t\"Whatever\": 1\n" +
            "\t}\n" +
            "}\n");

        Assert.Empty(records);
    }

    [Fact]
    public void LinesOutsideABlockAreIgnored()
    {
        Assert.Empty(ReadAll("some other log line\n\t\"Killer\": \"Nobody\"\n}\n"));
    }
}

/// <summary>
/// Turning what Stats.log writes into a name somebody can read.
/// </summary>
/// <remarks>
/// The kill feed went out as a wall of seventeen-digit numbers shooting other seventeen-digit
/// numbers. Stats.log records the UNIQUE ID in the Killer and Killed fields, and nothing
/// resolved it - the same gap the ban-file importer already had.
/// </remarks>
public class StatsKillNameTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-statsname-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public StatsKillNameTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "Stats.log");
        File.WriteAllText(_path, "");
    }

    private static string Block(string killer, string killed) =>
        $"[2026.09.12-03.50.32] StatManagerLog: {{\n" +
        "\t\"KillData\":\n\t{\n" +
        $"\t\t\"Killer\": \"{killer}\",\n" +
        $"\t\t\"Killed\": \"{killed}\",\n" +
        "\t\t\"KilledBy\": \"lightmachinegun\",\n" +
        "\t\t\"Headshot\": true\n" +
        "\t}\n}\n";

    private async Task<List<StatsKill>> ReadAsync(string contents, Func<string, string?>? resolve)
    {
        var service = new StatsLogService([_path],
            new LogTailer(NullLogger.Instance), new MetricsRegistry(),
            NullLogger<StatsLogService>.Instance, resolve);

        var seen = new List<StatsKill>();
        service.Killed += kill => { seen.Add(kill); return Task.CompletedTask; };

        // The tailer positions at the END on its first pass, so the content has to arrive
        // after it - which is also how a live server writes it.
        await service.TickAsync();
        await File.AppendAllTextAsync(_path, contents);
        await service.TickAsync();

        return seen;
    }

    [Fact]
    public async Task AUniqueIdBecomesTheDisplayName()
    {
        /* THE BUG. Both fields carried a seventeen-digit id straight into the feed, so the
           channel read "27475864022060194 -> 9096592833787397" and told a moderator nothing
           about who had done what. */
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["27475864022060194"] = "Holosight1",
            ["9096592833787397"] = "LxPXHam",
        };

        var kill = Assert.Single(await ReadAsync(
            Block("27475864022060194", "9096592833787397"), id => names.GetValueOrDefault(id)));

        Assert.Equal("Holosight1", kill.Killer);
        Assert.Equal("LxPXHam", kill.Killed);
        Assert.True(kill.Headshot);
    }

    [Fact]
    public async Task AnIdNobodyCanNameIsLeftAsTheId()
    {
        /* NOT REPLACED WITH "unknown". An id nobody can name is still the only handle
           anybody has on that player, and a feed line naming nobody is worse than an ugly
           one naming a number. */
        var kill = Assert.Single(await ReadAsync(Block("27475864022060194", "9096592833787397"), _ => null));

        Assert.Equal("27475864022060194", kill.Killer);
        Assert.Equal("9096592833787397", kill.Killed);
    }

    [Fact]
    public async Task AFieldThatAlreadyHoldsANameIsPassedThrough()
    {
        // The field is not always an id, so resolution has to be a lookup that may miss
        // rather than a conversion that must succeed.
        var kill = Assert.Single(await ReadAsync(Block("Holosight1", "LxPXHam"), _ => null));

        Assert.Equal("Holosight1", kill.Killer);
        Assert.Equal("LxPXHam", kill.Killed);
    }

    [Fact]
    public async Task AResolverThatThrowsDoesNotCostTheKill()
    {
        var kill = Assert.Single(await ReadAsync(
            Block("27475864022060194", "9096592833787397"), _ => throw new InvalidOperationException("rcon down")));

        Assert.Equal("27475864022060194", kill.Killer);
    }

    [Fact]
    public async Task WithNoResolverTheRawFieldsSurvive()
    {
        var kill = Assert.Single(await ReadAsync(Block("27475864022060194", "9096592833787397"), resolve: null));

        Assert.Equal("27475864022060194", kill.Killer);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// Stats.log feeding K/D, the reason it was brought back.
/// </summary>
/// <remarks>
/// The kill FEED came from Stats.log the first time round; the scoreboard did not exist yet.
/// Bringing it back is for both, so this pins the path FeedBridge wires - a stats kill counted
/// exactly as the Pavlov.log one is, but stamped with the game's own time - end to end over a
/// real tailed file. The conversion mirrors FeedBridge.OnStatsKillAsync so a drift there is a
/// failure here.
/// </remarks>
public sealed class StatsKillScoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-statsscore-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public StatsKillScoreTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "Stats.log");
        File.WriteAllText(_path, "");
    }

    private static string Block(string killer, string killed, string weapon = "ak47") =>
        "[2026.09.12-03.50.32] StatManagerLog: {\n" +
        "\t\"KillData\":\n\t{\n" +
        $"\t\t\"Killer\": \"{killer}\",\n" +
        $"\t\t\"Killed\": \"{killed}\",\n" +
        $"\t\t\"KilledBy\": \"{weapon}\",\n" +
        "\t\t\"Headshot\": false\n" +
        "\t}\n}\n";

    private async Task<PavlovBot.Host.Stats.KillStats> ScoreAsync(params string[] blocks)
    {
        var service = new StatsLogService([_path],
            new LogTailer(NullLogger.Instance), new MetricsRegistry(),
            NullLogger<StatsLogService>.Instance);

        var killStats = new PavlovBot.Host.Stats.KillStats(
            new PavlovBot.Core.Data.SerializedStore(new MemoryBackend(), new SystemTextJsonCodec()),
            NullLogger<PavlovBot.Host.Stats.KillStats>.Instance);

        // Exactly what FeedBridge.OnStatsKillAsync does: convert to a KillEvent, record it
        // with the GAME'S timestamp rather than the clock at read time.
        service.Killed += kill =>
        {
            killStats.Record(new KillEvent(kill.Killer, kill.Killed, kill.Weapon), kill.At);
            return Task.CompletedTask;
        };

        await service.TickAsync();                         // positions at end of file
        await File.AppendAllTextAsync(_path, string.Concat(blocks));
        await service.TickAsync();

        return killStats;
    }

    [Fact]
    public async Task AStatsKillIsCountedForBothTheKillerAndTheVictim()
    {
        var score = await ScoreAsync(Block("Winner", "Loser"));

        Assert.Equal(1, score.Of("Winner").Kills);
        Assert.Equal(0, score.Of("Winner").Deaths);
        Assert.Equal(1, score.Of("Loser").Deaths);
        Assert.Equal(0, score.Of("Loser").Kills);
    }

    [Fact]
    public async Task TheKillCarriesTheGamesOwnTimestamp()
    {
        // Not the clock at read time - the whole point of Stats.log over the Pavlov.log scrape.
        var score = await ScoreAsync(Block("Winner", "Loser"));

        Assert.Equal(new DateTimeOffset(2026, 9, 12, 3, 50, 32, TimeSpan.Zero), score.Of("Winner").LastAt);
    }

    [Fact]
    public async Task ManyKillsAccumulate()
    {
        var score = await ScoreAsync(
            Block("Winner", "A"), Block("Winner", "B"), Block("Winner", "C"), Block("Rival", "Winner"));

        Assert.Equal(3, score.Of("Winner").Kills);
        Assert.Equal(1, score.Of("Winner").Deaths);
        Assert.Equal(3.0, score.Of("Winner").Ratio);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
