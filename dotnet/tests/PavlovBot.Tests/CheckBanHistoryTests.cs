using Discord;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/checkban</c>'s ban history: past bans come from the audit log, because a lifted or served
/// ban is gone from the ban store - and that is the part of a record an appeal needs.
/// </summary>
public class CheckBanHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ModAction Act(string action, string player, string? reason, int daysAgo, string by = "mod") =>
        new(action, by, player, reason, Now.AddDays(-daysAgo));

    [Fact]
    public void PastBansAndTheirEndsAreListedNewestFirst()
    {
        var log = new[]
        {
            Act("tempban", "Evader", "griefing", 30),
            Act("unban", "Evader", "griefing", 28),
            Act("permban", "Evader", "cheating", 2),
        };

        var history = CheckBanCommand.BanHistory(log, ["Evader"], "Evader")!;

        Assert.StartsWith("2 ban(s) on record.", history, StringComparison.Ordinal);
        Assert.True(history.IndexOf("cheating", StringComparison.Ordinal) < history.IndexOf("griefing", StringComparison.Ordinal));
        Assert.Contains("Unbanned", history, StringComparison.Ordinal);
        Assert.Equal(2, history.Split("griefing").Length);   // once, on the ban - not again on its unban
    }

    [Fact]
    public void AutomatedBansAndReleasesAreLeftOut()
    {
        // The machinery repeating itself - one evader can log dozens - buries what staff decided.
        var log = new[]
        {
            Act("autoban", "Evader", "Ban evasion", 3, by: "auto"),
            Act("auto-ipban", "Evader", null, 90, by: "auto"),
            Act("vpnban", "Evader", "VPN", 4, by: "auto"),
            Act("autoban-released", "Evader", null, 2, by: "auto"),
            Act("unban", "Evader", "Ban evasion", 1),
        };

        Assert.Null(CheckBanCommand.BanHistory(log, ["Evader"], "Evader"));
    }

    [Fact]
    public void BansUnderAnotherNameOfTheSameAccountAreShownAndLabelled()
    {
        var history = CheckBanCommand.BanHistory([Act("permban", "OldName", "cheating", 5)], ["NewName", "OldName"], "NewName")!;

        Assert.Contains("as **OldName**", history, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherPlayersAndOtherActionsAreLeftOut()
    {
        var log = new[] { Act("permban", "Somebody", "x", 1), Act("kick", "Evader", "afk", 1), Act("warn", "Evader", "rude", 1) };

        Assert.Null(CheckBanCommand.BanHistory(log, ["Evader"], "Evader"));
    }

    [Fact]
    public void AnAutoBanReasonDoesNotLeakTheAddress()
    {
        var history = CheckBanCommand.BanHistory([Act("permban", "Evader", "alt of banned player at 203.0.113.9", 1)], ["Evader"], "Evader")!;

        Assert.DoesNotContain("203.0.113.9", history, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongRecordFitsInOneDiscordField()
    {
        var log = Enumerable.Range(1, 40)
            .Select(i => Act("tempban", "Evader", new string('x', 300), i, by: new string('m', 32)))
            .ToList();

        var history = CheckBanCommand.BanHistory(log, ["Evader"], "Evader")!;

        Assert.True(history.Length <= EmbedFieldBuilder.MaxFieldValueLength);
        Assert.StartsWith("40 ban(s) on record.", history, StringComparison.Ordinal);
        Assert.Contains("older.", history, StringComparison.Ordinal);
    }
}
