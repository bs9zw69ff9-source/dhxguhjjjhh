using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Intelligence;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Stats;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Counting kills off Pavlov.log, and the card <c>/stats</c> builds from them.
/// </summary>
/// <remarks>
/// THE KILL LINES WERE PARSED AND THROWN AWAY. The feed posted each one and nothing counted
/// it, so there was no K/D to show anybody - this is the storage half of <c>/stats</c>, and
/// most of what can go wrong is arithmetic that looks right: a suicide credited as a kill, a
/// ratio that divides by zero, a buffered count lost because a write failed.
/// </remarks>
public class KillStatsTests
{
    private readonly MemoryBackend _backend = new();
    private readonly KillStats _stats;

    public KillStatsTests() =>
        _stats = new KillStats(new SerializedStore(_backend, new SystemTextJsonCodec()),
            NullLogger<KillStats>.Instance);

    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void AKillIsOneKillAndOneDeath()
    {
        _stats.Record(new KillEvent("Winner", "Loser", "AK47"), Now);

        Assert.Equal(1, _stats.Of("Winner").Kills);
        Assert.Equal(0, _stats.Of("Winner").Deaths);
        Assert.Equal(1, _stats.Of("Loser").Deaths);
        Assert.Equal(0, _stats.Of("Loser").Kills);
    }

    [Fact]
    public void ASuicideIsADeathAndNeverAKill()
    {
        /* Pavlov files a self-kill with the same name on both sides. Counting it as a kill
           means a ratio anybody can farm with a grenade. */
        _stats.Record(new KillEvent("Clumsy", "Clumsy", "grenade"), Now);

        var clumsy = _stats.Of("Clumsy");
        Assert.Equal(0, clumsy.Kills);
        Assert.Equal(1, clumsy.Deaths);
        Assert.Equal(1, clumsy.Suicides);
    }

    [Fact]
    public void AFallIsADeathWithNobodyCredited()
    {
        // A fall or a drowning arrives with no killer at all.
        _stats.Record(new KillEvent(null, "Clumsy", null), Now);

        Assert.Equal(1, _stats.Of("Clumsy").Deaths);
        Assert.Equal(0, _stats.Of("Clumsy").Kills);
    }

    [Fact]
    public void NamesAreMatchedTheWayEveryOtherNameLookupIs()
    {
        _stats.Record(new KillEvent("Winner", "Loser", "AK47"), Now);

        // Pavlov is not consistent about case across log lines, and two records for one
        // player is a scoreboard that is simply wrong.
        Assert.Equal(1, _stats.Of("winner").Kills);
    }

    [Fact]
    public void ARatioWithNoDeathsIsTheKillCountRatherThanInfinity()
    {
        /* Every wrong answer to this is visible in the embed: NaN, ∞, or a silent zero that
           reads as "terrible" for somebody who has never died. */
        _stats.Record(new KillEvent("Untouchable", "A", "AK47"), Now);
        _stats.Record(new KillEvent("Untouchable", "B", "AK47"), Now);
        _stats.Record(new KillEvent("Untouchable", "C", "AK47"), Now);

        Assert.Equal(3.0, _stats.Of("Untouchable").Ratio);
    }

    [Fact]
    public async Task TheBufferIsWrittenOnceRatherThanPerKill()
    {
        /* THE REASON THIS CLASS BUFFERS AT ALL. SerializedStore rewrites the whole dataset
           per update, and a busy server produces hundreds of kills a minute - which is the
           shape of the problem the money log had before it was deleted. */
        for (var i = 0; i < 50; i++)
            _stats.Record(new KillEvent("Winner", $"Loser{i}", "AK47"), Now);

        Assert.Equal(0, _backend.Writes);

        await _stats.FlushAsync();

        Assert.Equal(1, _backend.Writes);
        Assert.Equal(50, _stats.Of("Winner").Kills);
    }

    [Fact]
    public async Task AFlushedTotalSurvivesAndKeepsAccumulating()
    {
        _stats.Record(new KillEvent("Winner", "Loser", "AK47"), Now);
        await _stats.FlushAsync();

        _stats.Record(new KillEvent("Winner", "Loser", "AK47"), Now.AddMinutes(1));

        // The unflushed kill is included, so a player checking straight after a firefight
        // does not see a number a minute out of date.
        Assert.Equal(2, _stats.Of("Winner").Kills);

        await _stats.FlushAsync();
        Assert.Equal(2, _stats.Of("Winner").Kills);
        Assert.Equal(Now.AddMinutes(1), _stats.Of("Winner").LastAt);
    }

    [Fact]
    public async Task AFlushWithNothingBufferedWritesNothing()
    {
        await _stats.FlushAsync();

        Assert.Equal(0, _backend.Writes);
    }

    [Fact]
    public async Task AFailedWriteHoldsTheKillsForTheNextFlush()
    {
        /* The buffer is cleared before the write. Dropping it on failure loses those kills
           permanently and silently, which is the worst of the available outcomes. */
        var backend = new ThrowingBackend();
        var stats = new KillStats(new SerializedStore(backend, new SystemTextJsonCodec()),
            NullLogger<KillStats>.Instance);

        stats.Record(new KillEvent("Winner", "Loser", "AK47"), Now);
        await stats.FlushAsync();

        Assert.Equal(1, stats.Of("Winner").Kills);

        backend.Throw = false;
        await stats.FlushAsync();

        Assert.Equal(1, stats.Of("Winner").Kills);
        Assert.Equal(1, backend.Writes);
    }

    private sealed class ThrowingBackend : IKeyValueBackend
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public bool Throw { get; set; } = true;
        public int Writes { get; private set; }

        public string? Read(string key) => _data.GetValueOrDefault(key);

        public void Write(string key, string json)
        {
            if (Throw) throw new IOException("the disk is full");
            Writes++;
            _data[key] = json;
        }
    }

    // ---- the card ----

    private static PlayerProfile Profile(
        ProfileCombat? combat = null, string? faction = "NCR", string? rank = "Ranger",
        long playtime = 125, long? balance = 1250, bool online = false) =>
        new(
            new ProfileIdentity("Courier6", "0002abc", []),
            new ProfileActivity(DateTimeOffset.UnixEpoch.AddYears(55), DateTimeOffset.UnixEpoch.AddYears(56),
                playtime, online, online ? ["server1"] : []),
            ProfileNetwork.Empty,
            ProfileModeration.Empty,
            new ProfileFaction(faction, rank, null),
            new ProfileEconomy(balance, 0, 0),
            [],
            RiskAssessment.Clean,
            Combat: combat);

    [Fact]
    public void TheCardShowsTheFactionRankPlaytimeAndRatio()
    {
        var card = StatsCommand.Card(Profile(new ProfileCombat(10, 5, 1, Now))).Build();
        var text = card.Description + "\n" + string.Join("\n", card.Fields.Select(f => $"{f.Name}: {f.Value}"));

        Assert.Contains("NCR", text, StringComparison.Ordinal);
        Assert.Contains("Ranger", text, StringComparison.Ordinal);
        Assert.Contains("2h 5m", text, StringComparison.Ordinal);
        Assert.Contains("2.00", text, StringComparison.Ordinal);
        Assert.Contains("10 / 5", text, StringComparison.Ordinal);
        Assert.Contains("(1 self)", text, StringComparison.Ordinal);
        Assert.Contains("1,250 caps", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerWithNoKillsIsNotShownAZeroRatio()
    {
        /* "0.00" reads as a judgement on somebody who simply has not been counted yet - the
           bot only started counting when this feature shipped. */
        var card = StatsCommand.Card(Profile(ProfileCombat.None)).Build();
        var kd = Assert.Single(card.Fields, f => f.Name == "K/D");

        Assert.Contains("nothing recorded", kd.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnaffiliatedPlayerSaysSoRatherThanShowingNothing()
    {
        var card = StatsCommand.Card(Profile(combat: null, faction: null, rank: null)).Build();

        Assert.Contains("unaffiliated", card.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void APlayerTheBotHasNeverSeenIsToldSo()
    {
        /* A wall of zeroes reads as "this player is terrible" rather than "we have never
           heard of them", and a misspelled name produces exactly that. */
        var stranger = new PlayerProfile(
            new ProfileIdentity("WhoIsThis", null, []),
            new ProfileActivity(null, null, 0, false, []),
            ProfileNetwork.Empty, ProfileModeration.Empty, ProfileFaction.None, ProfileEconomy.Empty,
            [], RiskAssessment.Clean);

        var card = StatsCommand.Card(stranger).Build();

        Assert.Contains("Never seen", card.Description, StringComparison.Ordinal);
        Assert.Empty(card.Fields);
    }

    [Fact]
    public void PlaytimeReadsAsHoursAndMinutes()
    {
        Assert.Equal("45m", StatsCommand.Hours(45));
        Assert.Equal("2h 5m", StatsCommand.Hours(125));
        Assert.Equal("1,000h 0m", StatsCommand.Hours(60_000));
    }
}
