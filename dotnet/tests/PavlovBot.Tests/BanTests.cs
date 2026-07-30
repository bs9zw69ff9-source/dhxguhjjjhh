using PavlovBot.Core.Moderation;
using Xunit;

namespace PavlovBot.Tests;

public class BanRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static BanRecord Ban(string id, bool permanent = false, DateTimeOffset? expires = null,
        string? reason = null, string? moderator = null) =>
        new() { PlayerId = id, Permanent = permanent, Expires = expires, Reason = reason, Moderator = moderator };

    [Fact]
    public void ARecordThatCannotAnswerWhetherItIsInForceIsInvalid()
    {
        /* Neither permanent nor dated has no answer to "is this still in force?", and
           guessing either way is wrong: guessing permanent leaves somebody banned forever
           over a corrupt field, guessing expired quietly frees them. */
        Assert.False(Ban("alice").IsValid);
        Assert.False(Ban("").IsValid);
        Assert.False(Ban("   ", permanent: true).IsValid);

        Assert.True(Ban("alice", permanent: true).IsValid);
        Assert.True(Ban("alice", expires: Now).IsValid);
    }

    [Fact]
    public void OnlyValidInForceBansAreActive()
    {
        var active = BanRules.Active(
        [
            Ban("permanent", permanent: true),
            Ban("current", expires: Now.AddHours(1)),
            Ban("served", expires: Now.AddHours(-1)),
            Ban("broken"),
        ], Now);

        Assert.Equal(["permanent", "current"], active.Select(b => b.PlayerId));
    }

    [Fact]
    public void ABanExpiringExactlyNowIsServed()
    {
        // Strictly greater than: a ban whose second has arrived is over. The alternative
        // keeps somebody banned for one more sweep for no reason.
        Assert.Empty(BanRules.Active([Ban("alice", expires: Now)], Now));
    }

    [Fact]
    public void PlayerMatchingIgnoresCaseAndSurroundingSpace()
    {
        Assert.True(BanRules.SamePlayer("Alice", " alice "));
        Assert.False(BanRules.SamePlayer("Alice", "Alicia"));
        Assert.False(BanRules.SamePlayer(null, "Alice"));
    }
}

public class AutoBanDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnexpiredTempBanBlocksWithoutEscalating()
    {
        var existing = new BanRecord { PlayerId = "alice", Expires = Now.AddHours(2) };
        Assert.Equal(AutoBanAction.Block, BanRules.AutoBanDecision(existing, "blacklisted ip", Now));
    }

    [Fact]
    public void AServedTempBanIsLIFTED_NotEscalatedToPermanent()
    {
        /* THE case this function exists for. A player who served a two-day ban still
           carries the IP and account flags that ban created; the sweep that clears them
           runs on a timer, so there is a window where a served ban's own leftovers look
           exactly like evasion. Escalating there turns a served temp ban into a permanent
           one - the worst failure this system can produce. */
        var served = new BanRecord { PlayerId = "alice", Expires = Now.AddHours(-1) };
        Assert.Equal(AutoBanAction.Lift, BanRules.AutoBanDecision(served, "blacklisted ip", Now));
        Assert.Equal(AutoBanAction.Lift, BanRules.AutoBanDecision(served, "Blacklisted account 0002abc", Now));
    }

    [Fact]
    public void AUsernameFlagStillEscalatesAfterATempBanExpires()
    {
        /* A name flag is only ever set deliberately by an admin through /configure, never
           as a side effect of a ban - so it has no expiry to have served. */
        var served = new BanRecord { PlayerId = "alice", Expires = Now.AddHours(-1) };
        Assert.Equal(AutoBanAction.Ban, BanRules.AutoBanDecision(served, "blacklisted username", Now));
    }

    [Fact]
    public void NoCoveringBanMeansAutoBan()
    {
        Assert.Equal(AutoBanAction.Ban, BanRules.AutoBanDecision(null, "blacklisted ip", Now));
    }

    [Fact]
    public void APermanentBanNeverProducesALift()
    {
        var permanent = new BanRecord { PlayerId = "alice", Permanent = true };
        Assert.Equal(AutoBanAction.Ban, BanRules.AutoBanDecision(permanent, "blacklisted ip", Now));
    }
}

public class SourceBanTests
{
    private static BanRecord Ban(string id, string reason, string moderator) =>
        new() { PlayerId = id, Permanent = true, Reason = reason, Moderator = moderator };

    [Fact]
    public void AutoBansAndEvasionBansAreNotRealPunishments()
    {
        // Otherwise the answer to "what did they actually do" is circular.
        Assert.False(BanRules.IsRealBan(Ban("a", "Auto-ban: flagged IP", "IP-Guard")));
        Assert.False(BanRules.IsRealBan(Ban("a", "Ban evasion", "Ban evasion (auto)")));
        Assert.False(BanRules.IsRealBan(Ban("a", "Cheating", "IP-Guard")));
    }

    [Fact]
    public void BroadConfigureBlacklistsAreNotRealPunishmentsEither()
    {
        /* Not a punishment on ONE person - copying their moderator makes every later
           match look as though that admin personally banned the player. */
        Assert.False(BanRules.IsRealBan(Ban("a", "Blacklisted via /configure", "Owner")));
    }

    [Fact]
    public void AnOrdinaryBanIsARealPunishment()
    {
        Assert.True(BanRules.IsRealBan(Ban("a", "Cheating", "Dana")));
    }

    [Fact]
    public void ABanWithNoReasonIsNotUsableAsASource()
    {
        Assert.False(BanRules.IsRealBan(new BanRecord { PlayerId = "a", Permanent = true }));
        Assert.False(BanRules.IsRealBan(null));
    }

    [Fact]
    public void TheSameAccountIsMatchedBeforeTheSameName()
    {
        /* An evader who kept the account and changed name is the common case, and the EOS
           id is the only identifier they cannot change. */
        var bans = new[]
        {
            Ban("SomeoneElse", "Griefing", "Dana"),
            Ban("OldName", "Cheating", "Dana"),
        };
        var eos = new Dictionary<string, string> { ["OldName"] = "0002abc" };

        var source = BanRules.SourceBanFor(bans, "NewName", "0002abc", n => eos.GetValueOrDefault(n));
        Assert.Equal("OldName", source!.PlayerId);
        Assert.Equal("Cheating", source.Reason);
    }

    [Fact]
    public void AKnownAltNameIsMatchedWhenTheAccountIsUnknown()
    {
        var bans = new[] { Ban("OldName", "Cheating", "Dana") };
        var source = BanRules.SourceBanFor(bans, "NewName", null, _ => null, altNames: ["OldName"]);
        Assert.Equal("OldName", source!.PlayerId);
    }

    [Fact]
    public void NoRealBanAnywhereReturnsNull_RatherThanAMachineryBan()
    {
        // The caller falls back to a clean "Ban evasion", which is better than attributing
        // the auto-ban's own reason to the player.
        var bans = new[] { Ban("OldName", "Auto-ban: flagged IP", "IP-Guard") };
        Assert.Null(BanRules.SourceBanFor(bans, "OldName", null, _ => null));
    }
}
