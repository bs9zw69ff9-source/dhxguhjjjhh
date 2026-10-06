using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Stats;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The K/D leaderboard: who ranks, in what order, and what each row says.</summary>
public class KdBoardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly FactionDefinition Enclave = FactionRegistry.Default.Get("Enclave")!;

    private static PlayerKills K(string player, long kills, long deaths) => new(player, kills, deaths, 0, Now);

    private static string Board(
        IEnumerable<PlayerKills> kills,
        Dictionary<string, PlaytimeEntry>? playtime = null,
        Dictionary<string, Membership>? factions = null) =>
        Boards.BuildKdBoard(kills,
            playtime ?? new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase),
            factions ?? new Dictionary<string, Membership>(StringComparer.OrdinalIgnoreCase),
            Now).Description;

    [Fact]
    public void RankedByRatioWithMoreKillsBreakingATie()
    {
        var board = Board([K("Average", 20, 20), K("Sharp", 30, 10), K("AlsoSharp", 60, 20)]);

        // 3.00 twice: the one with more kills goes first; 1.00 last.
        Assert.True(board.IndexOf("AlsoSharp", StringComparison.Ordinal) < board.IndexOf("**Sharp**", StringComparison.Ordinal));
        Assert.True(board.IndexOf("**Sharp**", StringComparison.Ordinal) < board.IndexOf("Average", StringComparison.Ordinal));
        Assert.Contains("**3.00** K/D (60/20)", board, StringComparison.Ordinal);
    }

    [Fact]
    public void TooFewKillsDoNotRank()
    {
        // One lucky kill and no deaths must not top the board.
        var board = Board([K("Lucky", Boards.KdMinimumKills - 1, 0), K("Regular", 40, 20)]);

        Assert.DoesNotContain("Lucky", board, StringComparison.Ordinal);
        Assert.Contains("Regular", board, StringComparison.Ordinal);
    }

    [Fact]
    public void EachRowCarriesFactionRankAndPlaytime()
    {
        var rank = Enclave.Order[^1];
        var board = Board(
            [K("Soldier", 50, 10), K("Drifter", 30, 15)],
            new(StringComparer.OrdinalIgnoreCase) { ["soldier"] = new PlaytimeEntry("Soldier", 860, Now) },
            new(StringComparer.OrdinalIgnoreCase) { ["Soldier"] = new Membership(Enclave, "Soldier", rank) });

        Assert.Contains($"Enclave {rank} · 14h 20m", board, StringComparison.Ordinal);
        Assert.Contains("No faction · no playtime", board, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheTopRowsAreShown()
    {
        var board = Board(Enumerable.Range(1, Boards.KdRows + 5).Select(i => K($"P{i:00}", 10 + i, 10)));

        Assert.Equal(Boards.KdRows, board.Split('\n').Count(l => l.Contains("K/D (", StringComparison.Ordinal)));
    }

    [Fact]
    public void NobodyRankedIsAnEmptyBoardNotNothing()
    {
        Assert.Contains("Nobody has", Board([]), StringComparison.Ordinal);
    }
}
