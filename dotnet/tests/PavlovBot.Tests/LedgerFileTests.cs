using PavlovBot.Core.Sync;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The content guards: recognising a caps ledger, and refusing to overwrite money with nothing.
/// </summary>
public class LedgerFileTests
{
    [Theory]
    [InlineData("0", true)]
    [InlineData("1250", true)]
    [InlineData("  42\n", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("-5", false)]        // a balance is never negative
    [InlineData("12.5", false)]
    [InlineData("1,250", false)]
    [InlineData("abc", false)]
    [InlineData("EOS:0002abc", false)]
    public void ALedgerIsOneBareNonNegativeInteger(string content, bool expected)
    {
        Assert.Equal(expected, LedgerFile.LooksLikeLedger(content));
    }

    [Fact]
    public void TheBalanceIsParsedWhenItLooksLikeOne()
    {
        Assert.Equal(1250, LedgerFile.Balance(" 1250 "));
        Assert.Null(LedgerFile.Balance("not a number"));
    }

    [Theory]
    // source empty/zero, destination a positive balance -> refuse, it would wipe caps
    [InlineData("", "500", true)]
    [InlineData("0", "500", true)]
    [InlineData("  ", "1", true)]
    // source carries a real number -> a normal copy, whatever the destination is
    [InlineData("750", "500", false)]
    [InlineData("0", "0", false)]        // destination has nothing to lose
    [InlineData("", "0", false)]
    // destination is not a balance at all -> not this guard's business
    [InlineData("", "some config text", false)]
    [InlineData("0", "", false)]
    public void WipingAPositiveBalanceWithNothingIsRefused(string source, string destination, bool expected)
    {
        Assert.Equal(expected, LedgerFile.WouldWipeBalance(source, destination));
    }

    [Fact]
    public void AMissingDestinationIsNeverAWipe()
    {
        // Copying into an install that has no file yet is exactly what the sync is for.
        Assert.False(LedgerFile.WouldWipeBalance("0", null));
        Assert.False(LedgerFile.WouldWipeBalance("", null));
    }
}
