using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The richest-players table, which is a fixed-width layout and therefore fragile.
/// </summary>
/// <remarks>
/// Every property here is about ALIGNMENT, which no other test in this suite cares about and
/// which a reader notices before they notice anything else on the board. A width computed
/// from one row, a header sitting over the wrong column, or a block whose closing fence was
/// truncated away are all invisible in a code read and obvious in the channel.
/// </remarks>
public class CashTableTests
{
    private const int Budget = 4060;

    private static (string Block, int Shown) Table(params (string Player, long Balance)[] rows) =>
        Boards.CashTable(rows, Budget);

    private static string[] Lines(string block) =>
        block.Trim('`', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TheColumnsLineUpAcrossEveryRow()
    {
        /* THE WHOLE POINT of a fenced table. The money column has to start at the same
           character on every line, including the header. */
        var (block, _) = Table(
            ("notgg1229mm", 7_747_661),
            ("TheonlyCrazyGrape9689", 1_442_892),
            ("ardacc1337", 1_000_207));

        var lines = Lines(block);
        var moneyColumn = lines[0].IndexOf("Money", StringComparison.Ordinal);

        Assert.Equal("#    Username                     Money", lines[0]);
        Assert.Equal("1    notgg1229mm             7,747,661$", lines[1]);
        Assert.Equal("2    TheonlyCrazyGrape9689   1,442,892$", lines[2]);
        Assert.Equal("3    ardacc1337              1,000,207$", lines[3]);

        // The header's last character sits on the money column's last character.
        foreach (var line in lines) Assert.Equal(moneyColumn + "Money".Length, line.TrimEnd().Length);
    }

    [Fact]
    public void MoneyIsRightAlignedSoDigitsSitUnderDigits()
    {
        /* A leaderboard mixes magnitudes - this is what left-aligning would ruin, and the
           sample this format came from happened to be all seven-figure, so it could not
           have shown the difference. */
        var (block, _) = Table(("Rich", 7_747_661), ("Poor", 12));

        var lines = Lines(block);
        Assert.EndsWith("7,747,661$", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("       12$", lines[2], StringComparison.Ordinal);
        Assert.Equal(lines[1].Length, lines[2].Length);
    }

    [Fact]
    public void TheRankColumnStaysAlignedIntoThreeDigits()
    {
        // 100 rows is the point of the change; rank 100 must not push the columns over.
        var rows = Enumerable.Range(1, 100).Select(i => ($"Player{i}", (long)(1000 - i))).ToArray();

        var lines = Lines(Boards.CashTable(rows, Budget).Block);

        Assert.Equal(101, lines.Length);                       // header + 100
        Assert.StartsWith("100  Player100", lines[^1], StringComparison.Ordinal);
        Assert.Equal(lines[1].Length, lines[^1].Length);
    }

    [Fact]
    public void ABacktickInANameCannotBreakOutOfTheBlock()
    {
        /* Names are attacker-controlled and a ledger file can be called anything. Three
           backticks in one would close the block and render the rest of the board as
           whatever the name said next. */
        var (block, _) = Table(("ev`il```", 10));

        Assert.DoesNotContain("`", block[4..^3], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsurdlyLongNameIsTrimmedRatherThanSettingTheWidthForEverybody()
    {
        // One row must not push the money column off the side of the channel for all of them.
        var (block, _) = Table((new string('x', 200), 10), ("Short", 5));

        foreach (var line in Lines(block)) Assert.True(line.Length < 60, $"too wide: {line.Length}");
        Assert.Contains("…", block, StringComparison.Ordinal);
    }

    [Fact]
    public void RowsAreDroppedUntilTheBlockFitsItsBudget()
    {
        /* THE FAILURE THIS PREVENTS: the description is truncated rather than rejected when
           it is over the limit, so an over-long block loses its CLOSING FENCE and Discord
           renders the rest of the message as code. */
        var rows = Enumerable.Range(1, 100)
            .Select(i => (new string('n', 32), (long)i))
            .ToArray();

        var (block, shown) = Boards.CashTable(rows, 600);

        Assert.True(block.Length <= 600, $"block was {block.Length}");
        Assert.True(shown is > 0 and < 100, $"showed {shown}");
        Assert.EndsWith("```", block, StringComparison.Ordinal);
    }

    [Fact]
    public void AHundredOrdinaryNamesFitInOneBlock()
    {
        /* THE SIZE THE FORMAT WAS ASKED FOR, against the longest name in the sample it came
           from. It fits with about forty characters to spare, which is worth pinning: a
           wider gutter or a second space in the rank column would silently start dropping
           the tail of the board. */
        var rows = Enumerable.Range(1, 100)
            .Select(i => ("TheonlyCrazyGrape9689", (long)(9_999_999 - i)))
            .ToArray();

        var (block, shown) = Boards.CashTable(rows, Budget);

        Assert.Equal(100, shown);
        Assert.True(block.Length <= Budget, $"block was {block.Length}");
    }

    [Fact]
    public void AnEmptyTableIsStillAWellFormedBlock()
    {
        var (block, shown) = Boards.CashTable([], Budget);

        Assert.Equal(0, shown);
        Assert.Equal("```\n#    Username   Money\n```", block);
    }
}
