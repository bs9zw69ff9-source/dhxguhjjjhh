using PavlovBot.Core.Economy;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Stats;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Core.Verification;
using Xunit;

namespace PavlovBot.Tests;

public class SanitizeTests
{
    [Fact]
    public void AnIdCannotCarryASpaceOrANewline()
    {
        /* RCON commands are space-delimited with NO quoting, so a name containing a space
           is not an escaping problem - it is command injection. */
        Assert.Equal("Kickeveryone", Sanitize.Id("Kick everyone"));
        Assert.Equal("aBan", Sanitize.Id("a\nBan"));
    }

    [Fact]
    public void OnlyTheAllowedAlphabetSurvives()
    {
        // An allow-list, not a block-list: there is nothing left to think of.
        Assert.Equal("Player_1-2.3", Sanitize.Id("Player_1-2.3"));
        // Digits inside a mention survive because digits are legal in a name; what matters
        // is that every character that could START a new command or argument is gone.
        Assert.Equal("123drop", Sanitize.Id("<@&123>drop;\"'`$()"));
    }

    [Fact]
    public void TheBotsOwnAutocompleteLabelsAreStrippedBack()
    {
        Assert.Equal("Alice", Sanitize.Id("Alice (manual entry)"));
        Assert.Equal("Bob", Sanitize.Id("Bob (offline)"));
        Assert.Equal("Carol", Sanitize.Id("Carol [s1+s2]"));
    }

    [Fact]
    public void EveryLabelTheBotCanAddIsOneItCanTakeBackOff()
    {
        /* THE TEST THAT STOPS THIS DRIFTING AGAIN, and it is the whole fix. These were two
           hand-written lists in two files: Sanitize stripped "manual entry" and "offline"
           while the autocomplete had moved on to "online" and "recent". The two it stripped
           were no longer produced and the two produced were not stripped - so "(online)"
           survived, met the filter that removes brackets and spaces, and became a ban on
           "Aliceonline", a player who does not exist. Which looks exactly like a ban that
           worked. */
        foreach (var label in NameLabels.All)
            Assert.Equal("Alice", Sanitize.Id(NameLabels.Decorate("Alice", label)));
    }

    [Fact]
    public void ANameWearingTwoLabelsLosesBoth()
    {
        // A name can be decorated, read off the screen, and typed into a field that
        // decorates it again. One pass has to take all of them off.
        Assert.Equal("Alice", Sanitize.Id("Alice (online) (manual entry)"));
    }

    [Fact]
    public void ANameWithNoLabelIsUntouched()
    {
        Assert.Equal("Alice", Sanitize.Id(NameLabels.Decorate("Alice", null)));
        Assert.Equal("Alice", Sanitize.Id(NameLabels.Decorate("Alice", "")));
    }

    [Fact]
    public void IdsAreLengthCapped()
    {
        Assert.Equal(64, Sanitize.Id(new string('a', 200)).Length);
    }

    [Fact]
    public void AMessageKeepsItsWordsButLosesItsLineBreaks()
    {
        // The protocol is line-oriented, so an embedded newline would split one command
        // into two and the second half would be whatever the player wrote.
        Assert.Equal("hello there", Sanitize.Message("hello\nthere"));
        Assert.Equal("a b c", Sanitize.Message("a\tb\rc"));
    }

    [Fact]
    public void NonPrintablesAreDroppedFromMessages()
    {
        Assert.Equal("hi", Sanitize.Message("h\0i​"));
    }

    [Theory]
    [InlineData("connect from 192.168.1.50 ok", "connect from [ip redacted] ok")]
    [InlineData("peer 2001:db8::1 closed", "peer [ip redacted] closed")]
    [InlineData("user 123456789012345678 joined", "user [id redacted] joined")]
    [InlineData("read /home/pavlov/Saved/x", "read [path redacted]/Saved/x")]
    [InlineData("read /root/secrets", "read [path redacted]/secrets")]
    // The auto-ban reason shapes /checkban and the ban lists surface. A ban reason must never
    // publish the address or account id that triggered the ban.
    [InlineData("Ban evasion - blacklisted ip 73.164.223.3", "Ban evasion - blacklisted ip [ip redacted]")]
    [InlineData("Ban evasion - blacklisted account 00020322e7bc4c6a9b5f83ae6e6b1ed5",
        "Ban evasion - blacklisted account [id redacted]")]
    public void PrivateDetailIsScrubbedFromPublicText(string input, string expected)
    {
        Assert.Equal(expected, Sanitize.RedactPrivate(input));
    }

    [Fact]
    public void HarmlessTextSurvivesRedaction()
    {
        // Short git hashes and version numbers must pass through, or every changelog entry
        // becomes unreadable.
        Assert.Equal("fixed in a3fdaab (v1.2.3)", Sanitize.RedactPrivate("fixed in a3fdaab (v1.2.3)"));
    }

    [Fact]
    public void ABacktickInANameCannotEscapeACodeSpan()
    {
        // Escaping inside a code span does not work - the span just ends - so the character
        // is replaced rather than escaped.
        Assert.Equal("ev'il", Sanitize.Code("ev`il"));
    }
}

public class EasternTimeTests
{
    private static readonly DateTimeOffset Winter = new(2026, 1, 15, 17, 30, 0, TimeSpan.Zero);   // EST, UTC-5
    private static readonly DateTimeOffset Summer = new(2026, 7, 15, 17, 30, 0, TimeSpan.Zero);   // EDT, UTC-4

    [Fact]
    public void TimestampsAreEasternAndDstAware()
    {
        /* The whole reason this type exists. Printing UTC is what made the connection feed
           look four to five hours off, and the offset is not constant across the year. */
        Assert.Equal("2026-01-15 12:30:00", EasternTime.Stamp(Winter));
        Assert.Equal("2026-07-15 13:30:00", EasternTime.Stamp(Summer));
    }

    [Fact]
    public void AnAbsentTimestampIsUnknown_NotTheEpoch()
    {
        // Zero means NEVER SEEN. Rendering 1970-01-01 is a lie that reads as real data.
        Assert.Equal("unknown", EasternTime.Stamp(null));
        Assert.Equal("unknown", EasternTime.Stamp(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void TheDateIsTheEasternCalendarDay_NotTheUtcOne()
    {
        // 01:30 UTC on the 16th is still the 15th in New York, and the daily peak bucket
        // depends on getting this right.
        Assert.Equal("2026-01-15", EasternTime.Date(new DateTimeOffset(2026, 1, 16, 1, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TimeLeftUsesTwoUnitsAndNeverThree()
    {
        var now = new FakeClock(Winter);
        Assert.Equal("3d 4h", EasternTime.TimeLeft(Winter.AddDays(3).AddHours(4).AddMinutes(17), now));
        Assert.Equal("5h 20m", EasternTime.TimeLeft(Winter.AddHours(5).AddMinutes(20), now));
        Assert.Equal("12m", EasternTime.TimeLeft(Winter.AddMinutes(12), now));
        Assert.Equal("expired", EasternTime.TimeLeft(Winter.AddMinutes(-1), now));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("10m", 600)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("45", 2700)]   // a bare number is MINUTES
    public void SimpleDurationsParse(string input, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), EasternTime.ParseDuration(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("soon")]
    [InlineData("2 weeks")]
    public void UnparseableDurationsAreNull(string input)
    {
        Assert.Null(EasternTime.ParseDuration(input));
    }

    [Fact]
    public void MonthIsMatchedBeforeMinute()
    {
        // "1mo" must not read as one MINUTE, which is what a naive single-letter match does
        // and would turn a month-long ban into a sixty-second one.
        Assert.Equal(TimeSpan.FromDays(30), EasternTime.ParseBanSpan("1mo"));
        Assert.Equal(TimeSpan.FromMinutes(1), EasternTime.ParseBanSpan("1m"));
    }

    [Fact]
    public void CompoundBanSpansSum()
    {
        Assert.Equal(TimeSpan.FromDays(3) + TimeSpan.FromHours(4), EasternTime.ParseBanSpan("3d 4h"));
        Assert.Equal(TimeSpan.FromDays(7), EasternTime.ParseBanSpan("1w"));
        Assert.Equal(TimeSpan.FromDays(365), EasternTime.ParseBanSpan("1y"));
    }

    [Theory]
    [InlineData("until friday")]
    [InlineData("2026-08-01")]
    [InlineData("next week")]
    [InlineData("3d until monday")]
    public void CalendarDatesAndWordsAreRefused_NeverGuessedAt(string input)
    {
        /* A moderator who types a date and silently gets a LENGTH is worse off than one
           who gets an error. Anything left over after removing the span parts means the
           input was not purely a span. */
        Assert.Null(EasternTime.ParseBanSpan(input));
    }

    [Theory]
    [InlineData("18:30", "18:30")]
    [InlineData("6:30pm", "18:30")]
    [InlineData("3pm", "15:00")]
    [InlineData("12am", "00:00")]
    [InlineData("12pm", "12:00")]
    [InlineData("0:00", "00:00")]
    public void ClockTimesNormaliseTo24Hour(string input, string expected)
    {
        Assert.Equal(expected, EasternTime.ParseClockTime(input));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("6:75")]
    [InlineData("13pm")]
    [InlineData("0pm")]
    public void ImpossibleClockTimesAreNull(string input)
    {
        Assert.Null(EasternTime.ParseClockTime(input));
    }

    [Fact]
    public void EveryPickerDurationResolves()
    {
        Assert.All(BanDurations.All, d => Assert.Equal(d.Length, BanDurations.Length(d.Value)));
        Assert.Equal(TimeSpan.FromDays(1), BanDurations.Length("1d"));
    }

    [Fact]
    public void PermanentAndNonsenseBothResolveToNull_AndTheCallerMustTellThemApart()
    {
        Assert.Null(BanDurations.Length(BanDurations.Permanent));
        Assert.Null(BanDurations.Length("whenever"));
    }

    [Fact]
    public void ATypedSpanWorksWhereAPickDoes()
    {
        Assert.Equal(TimeSpan.FromDays(3) + TimeSpan.FromHours(4), BanDurations.Length("3d 4h"));
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public class StaffHierarchyTests
{
    [Fact]
    public void HighestMatchingRoleWins()
    {
        // A super owner who also holds the mod role must not fall through to the lower answer.
        Assert.Equal(StaffTier.SuperOwner,
            StaffHierarchy.TierOf(isSuperOwner: true, isOwner: true, isAdmin: true, isMod: true));
        Assert.Equal(StaffTier.Mod,
            StaffHierarchy.TierOf(isSuperOwner: false, isOwner: false, isAdmin: false, isMod: true));
        Assert.Equal(StaffTier.None,
            StaffHierarchy.TierOf(isSuperOwner: false, isOwner: false, isAdmin: false, isMod: false));
    }

    [Fact]
    public void ALowerTierCannotUndoAHigherTiersAction()
    {
        /* Moderation authority is the one thing a moderator can attack with permissions
           they legitimately have. Without this, any mod can quietly unban whoever the
           owner banned and the audit log records it as routine. */
        Assert.False(StaffHierarchy.CanOverride(StaffTier.Mod, StaffTier.Admin));
        Assert.False(StaffHierarchy.CanOverride(StaffTier.Admin, StaffTier.Owner));
        Assert.False(StaffHierarchy.CanOverride(StaffTier.Owner, StaffTier.SuperOwner));
    }

    [Fact]
    public void ASuperOwnerCanUndoAnything()
    {
        Assert.All(Enum.GetValues<StaffTier>(), t => Assert.True(StaffHierarchy.CanOverride(StaffTier.SuperOwner, t)));
    }

    [Fact]
    public void EqualTiersCanUndoEachOther()
    {
        // Peers unable to undo each other would mean any mod going inactive leaves their
        // bans permanent.
        Assert.True(StaffHierarchy.CanOverride(StaffTier.Mod, StaffTier.Mod));
        Assert.True(StaffHierarchy.CanOverride(StaffTier.Admin, StaffTier.Admin));
    }

    [Fact]
    public void ARecordWithNoTierCanBeLiftedByAnyone()
    {
        // Records predating the hierarchy. Locking them retroactively would strand every
        // ban issued before the feature existed.
        Assert.True(StaffHierarchy.CanOverride(StaffTier.Mod, null));
        Assert.True(StaffHierarchy.CanOverride(StaffTier.Mod, StaffTier.None));
    }
}

public class MenuLinkTests
{
    [Fact]
    public void AFirstClaimIsGranted()
    {
        var decision = MenuLink.Decide(boundName: null, requestedName: "Alice", ownerId: null, selfId: "1", holdsMenu: false);
        Assert.Equal(MenuClaimAction.Grant, decision.Action);
    }

    [Fact]
    public void ABoundMemberCannotClaimADifferentName()
    {
        var decision = MenuLink.Decide("Alice", "Bob", null, "1", false);
        Assert.Equal(MenuClaimAction.Locked, decision.Action);
        Assert.Equal("Alice", decision.BoundName);
    }

    [Fact]
    public void TheLockHoldsEvenWithNoMenuHeld()
    {
        /* The exact state somebody would engineer to slip past a check that only ran while
           holding a menu: release the menu, then re-claim as an alt. */
        var decision = MenuLink.Decide("Alice", "Bob", null, "1", holdsMenu: false);
        Assert.Equal(MenuClaimAction.Locked, decision.Action);
    }

    [Fact]
    public void TwoAccountsCannotConvergeOnOneName()
    {
        var decision = MenuLink.Decide(null, "Alice", ownerId: "999", selfId: "1", holdsMenu: false);
        Assert.Equal(MenuClaimAction.Taken, decision.Action);
        Assert.Equal("999", decision.OwnerId);
    }

    [Fact]
    public void ReEnteringYourOwnNameWhileHoldingAMenuReleasesIt()
    {
        var decision = MenuLink.Decide("Alice", "alice", ownerId: "1", selfId: "1", holdsMenu: true);
        Assert.Equal(MenuClaimAction.Release, decision.Action);
    }

    [Fact]
    public void ReEnteringYourOwnNameWithoutAMenuGrantsItBack()
    {
        // Releasing frees the ACCESS, never the binding - so re-claiming your own name works.
        var decision = MenuLink.Decide("Alice", "Alice", ownerId: "1", selfId: "1", holdsMenu: false);
        Assert.Equal(MenuClaimAction.Grant, decision.Action);
    }

    [Fact]
    public void NameMatchingIgnoresCaseAndSurroundingSpace()
    {
        Assert.Equal(MenuClaimAction.Grant, MenuLink.Decide("Alice", "  ALICE ", "1", "1", false).Action);
    }
}

public class VerificationRulesTests
{
    private static Dictionary<string, VerificationRecord> Store(params (string Id, string Name, string[] Ips)[] rows) =>
        rows.ToDictionary(r => r.Id, r => new VerificationRecord(r.Name, r.Ips), StringComparer.Ordinal);

    [Fact]
    public void AFreshAccountWithAFreeNameIsAllowed()
    {
        Assert.True(VerificationRules.Check(Store(), "1", "Alice", ["1.2.3.4"]).IsAllowed);
    }

    [Fact]
    public void AnAlreadyVerifiedAccountIsRefusedWithItsOwnName()
    {
        var verdict = VerificationRules.Check(Store(("1", "Alice", [])), "1", "Bob", []);
        Assert.Equal(VerificationConflict.AlreadyVerified, verdict.Conflict);
        Assert.Contains("Alice", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameVerifiedToSomebodyElseIsRefused()
    {
        var verdict = VerificationRules.Check(Store(("999", "Alice", [])), "1", "alice", []);
        Assert.Equal(VerificationConflict.NameTaken, verdict.Conflict);
        Assert.Equal("999", verdict.OtherId);
    }

    [Fact]
    public void ASharedIpReadsAsAnAlt()
    {
        var verdict = VerificationRules.Check(Store(("999", "Bob", ["1.2.3.4"])), "1", "Alice", ["1.2.3.4"]);
        Assert.Equal(VerificationConflict.Alt, verdict.Conflict);
        Assert.Equal("999", verdict.OtherId);
    }

    [Fact]
    public void NoSharedIpMeansNoAltFinding()
    {
        Assert.True(VerificationRules.Check(Store(("999", "Bob", ["5.6.7.8"])), "1", "Alice", ["1.2.3.4"]).IsAllowed);
    }

    [Fact]
    public void VerifyingWithNoIpsAtAllStillChecksTheName()
    {
        // The IP rule simply has nothing to compare; the name rule must still apply.
        Assert.Equal(VerificationConflict.NameTaken,
            VerificationRules.Check(Store(("999", "Alice", ["1.2.3.4"])), "1", "Alice", []).Conflict);
    }
}

public class PeakTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AllTimePeaksOnlyEverRise()
    {
        var first = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 20 }, null, Now, "2026-07-15");
        Assert.True(first.Changed);
        Assert.Equal(20, first.Stats.PerServer["server1"].Peak);

        var lower = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 5 }, first.Stats, Now, "2026-07-15");
        Assert.False(lower.Changed);
        Assert.Equal(20, lower.Stats.PerServer["server1"].Peak);
    }

    [Fact]
    public void TheCombinedPeakIsAcrossServers_NotTheMaxOfThem()
    {
        var update = PeakTracker.Reduce(
            new Dictionary<string, int> { ["server1"] = 12, ["server2"] = 9 }, null, Now, null);
        Assert.Equal(21, update.Stats.Combined!.Peak);
    }

    [Fact]
    public void ADayRollOverResetsTheDailyBucketButNotTheAllTimeOne()
    {
        var monday = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 30 }, null, Now, "2026-07-15");
        var tuesday = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 4 }, monday.Stats, Now, "2026-07-16");

        Assert.Equal(4, tuesday.Stats.Daily!.Combined.Peak);
        Assert.Equal("2026-07-16", tuesday.Stats.Daily.Date);
        Assert.Equal(30, tuesday.Stats.PerServer["server1"].Peak);
    }

    [Fact]
    public void TheDaysResetIsPersistedEvenAtZeroPlayers()
    {
        /* Otherwise a quiet midnight leaves yesterday's numbers on display as though they
           were today's, which is worse than showing zero. */
        var monday = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 30 }, null, Now, "2026-07-15");
        var tuesday = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 0 }, monday.Stats, Now, "2026-07-16");

        Assert.True(tuesday.Changed);
        Assert.Equal(0, tuesday.Stats.Daily!.Combined.Peak);
    }

    [Fact]
    public void NothingBeatingARecordReportsNoChange_SoTheCallerCanSkipTheWrite()
    {
        // Peaks are sampled every minute and almost never move; writing unconditionally
        // would be a disk write a minute forever.
        var first = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 10 }, null, Now, "2026-07-15");
        Assert.False(PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 10 }, first.Stats, Now, "2026-07-15").Changed);
    }

    [Fact]
    public void PassingNoDateKeySkipsDailyTrackingEntirely()
    {
        var update = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 10 }, null, Now, null);
        Assert.Null(update.Stats.Daily);
    }

    [Fact]
    public void ReadingTodaysPeakFromAnotherDaysBucketGivesZero()
    {
        var stats = PeakTracker.Reduce(new Dictionary<string, int> { ["server1"] = 10 }, null, Now, "2026-07-15").Stats;
        Assert.Equal(10, PeakTracker.DailyPeak(stats, "2026-07-15"));
        Assert.Equal(0, PeakTracker.DailyPeak(stats, "2026-07-16"));
        Assert.Equal(0, PeakTracker.DailyPeak(null, "2026-07-15"));
    }
}

public class LedgerTests
{
    private sealed class MemoryBalances : IBalanceStore
    {
        private readonly Dictionary<string, long> _balances = new(StringComparer.OrdinalIgnoreCase);
        public bool FailWrites { get; set; }
        public long? Read(string playerId) => _balances.TryGetValue(playerId, out var v) ? v : null;
        public bool Write(string playerId, long balance)
        {
            if (FailWrites) return false;
            _balances[playerId] = balance;
            return true;
        }
    }

    [Fact]
    public async Task ACreditAppliesAndReportsTheDelta()
    {
        var ledger = new Ledger(new MemoryBalances());
        var change = await ledger.CreditAsync("alice", 500);

        Assert.True(change.Ok);
        Assert.Equal(0, change.Before);
        Assert.Equal(500, change.After);
        Assert.Equal(500, change.Delta);
    }

    [Fact]
    public async Task ConcurrentCreditsDoNotDoubleSpend()
    {
        /* The whole reason this class exists. The store is a plain read-then-write: two
           concurrent payouts both read 500, both write 600, and one payout vanishes with
           nothing erroring anywhere. */
        var ledger = new Ledger(new MemoryBalances());
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => ledger.CreditAsync("alice", 10)));

        var final = await ledger.CreditAsync("alice", 0);
        Assert.Equal(1000, final.After);
    }

    [Fact]
    public async Task DifferentPlayersDoNotWaitOnEachOther()
    {
        // A busy server pays out constantly; one global lock would queue every payout
        // behind every other.
        var store = new MemoryBalances();
        var ledger = new Ledger(store);

        var blocked = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        /* Task.Run, not a direct call: the mutator runs synchronously inside MutateAsync,
           so invoking it on this thread would block the test itself before the second call
           was ever made - a deadlock in the test, not in the code. */
        var slow = Task.Run(() => ledger.MutateAsync("alice", before =>
        {
            entered.TrySetResult();
            blocked.Task.Wait();
            return before + 1;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // This must complete while alice's mutator is still held.
        var fast = await ledger.CreditAsync("bob", 5).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fast.Ok);

        blocked.SetResult();
        await slow;
    }

    [Fact]
    public async Task AVetoLeavesTheBalanceUntouched()
    {
        // A veto is how "insufficient funds" is expressed - an ordinary outcome, not an error.
        var ledger = new Ledger(new MemoryBalances());
        await ledger.CreditAsync("alice", 100);

        var change = await ledger.DebitAsync("alice", 500);
        Assert.False(change.Ok);
        Assert.Equal(100, change.Before);
        Assert.Equal(100, change.After);
    }

    [Fact]
    public async Task ADebitToExactlyZeroIsAllowed()
    {
        var ledger = new Ledger(new MemoryBalances());
        await ledger.CreditAsync("alice", 100);
        Assert.True((await ledger.DebitAsync("alice", 100)).Ok);
    }

    [Fact]
    public async Task AThrowingMutatorPropagatesButLeavesTheBalanceUntouched()
    {
        /* CONTRACT CHANGED DELIBERATELY. This used to assert that a throwing mutator came
           back as an ordinary failed change - the same result as "insufficient funds", which
           is the common, expected, entirely uninteresting outcome. A defect reported in the
           vocabulary of a routine veto is a defect nobody ever looks at.

           What has NOT changed, and is still asserted: the balance is untouched (the throw
           happens before the write) and the lock is released (the next caller gets through).
           Core cannot log - it is dependency-free and AOT-safe - so propagating is the only
           way the failure reaches somewhere it can be seen. */
        var ledger = new Ledger(new MemoryBalances());
        await ledger.CreditAsync("alice", 100);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ledger.MutateAsync("alice", _ => throw new InvalidOperationException("boom")));

        // Untouched, and the next caller must still get through.
        var next = await ledger.CreditAsync("alice", 1);
        Assert.True(next.Ok);
        Assert.Equal(100, next.Before);
        Assert.Equal(101, next.After);
    }

    [Fact]
    public async Task AFailedWriteReportsTheOldBalance_NotTheIntendedOne()
    {
        // Reporting the intended balance after a failed write is how a bot tells a player
        // they were paid when they were not.
        var store = new MemoryBalances { FailWrites = true };
        var ledger = new Ledger(store);

        var change = await ledger.CreditAsync("alice", 500);
        Assert.False(change.Ok);
        Assert.Equal(0, change.After);
    }

    [Fact]
    public async Task ManyDistinctPlayersEachKeepTheirOwnBalance()
    {
        /* WAS "QueuesAreDroppedOnceIdle", which asserted that the per-player lock map
           drained. That reclamation WAS the bug - it could remove a lock out from under a
           caller that had read it but not yet awaited it, letting two writers into one
           balance (see LedgerConcurrencyTests). The locks are striped now: a fixed set, so
           there is nothing to drain and no growth to assert against.

           Striping does mean distinct players share a lock, so the property worth pinning is
           that sharing a LOCK never means sharing a BALANCE. */
        var ledger = new Ledger(new MemoryBalances());
        for (var i = 0; i < 500; i++) await ledger.CreditAsync($"player{i}", i);

        // Read back without mutating: a zero credit reports Before.
        Assert.Equal(0, (await ledger.CreditAsync("player0", 0)).Before);
        Assert.Equal(250, (await ledger.CreditAsync("player250", 0)).Before);
        Assert.Equal(499, (await ledger.CreditAsync("player499", 0)).Before);
    }
}

public class MenuRoleMapTests
{
    [Fact]
    public void TheHighestTierWins()
    {
        /* A member holding both roles gets the high-staff menu. Checking the other way
           round silently downgrades every senior member who also kept the ordinary staff
           role - which is most of them. */
        var map = new PavlovBot.Host.Discord.Commands.MenuRoleMap(HighStaff: 1, Staff: 2);

        Assert.Equal("highstaff", map.TierFor([1, 2]));
        Assert.Equal("highstaff", map.TierFor([1]));
        Assert.Equal("staff", map.TierFor([2]));
    }

    [Fact]
    public void AnUnmappedMemberGetsNoTier()
    {
        var map = new PavlovBot.Host.Discord.Commands.MenuRoleMap(HighStaff: 1, Staff: 2);
        Assert.Null(map.TierFor([99]));
        Assert.Null(map.TierFor([]));
    }

    [Fact]
    public void AnUnconfiguredMapGrantsNothing()
    {
        // Not a default of "everyone is staff", which is the dangerous way to be empty.
        Assert.Null(PavlovBot.Host.Discord.Commands.MenuRoleMap.Empty.TierFor([1, 2, 3]));
    }

    // ---- what the command stored, over what the environment says ----

    [Fact]
    public void WhatTheCommandStoredBeatsTheEnvironment()
    {
        /* THE WHOLE POINT OF /setrconroles. An admin who just ran it expects it to take
           effect - that is why the command exists instead of a restart. */
        var stored = new PavlovBot.Host.Discord.Commands.MenuRoleMap(HighStaff: 10, Staff: 20);

        var inForce = stored.Over(environmentHighStaff: 1, environmentStaff: 2, environmentBlacklist: 3);

        Assert.Equal("highstaff", inForce.TierFor([10]));
        Assert.Equal("staff", inForce.TierFor([20]));
        Assert.Null(inForce.TierFor([1]));      // the environment's role no longer applies
    }

    [Fact]
    public void TheEnvironmentStillAppliesToTiersTheCommandNeverSet()
    {
        /* PER TIER, NOT ALL OR NOTHING. Setting only the staff role must not silently drop a
           high-staff role that only the environment knows about - that is the kind of half
           configuration that reads as "the command broke my permissions". */
        var stored = new PavlovBot.Host.Discord.Commands.MenuRoleMap(Staff: 20);

        var inForce = stored.Over(environmentHighStaff: 1, environmentStaff: 2, environmentBlacklist: 3);

        Assert.Equal("highstaff", inForce.TierFor([1]));
        Assert.Equal("staff", inForce.TierFor([20]));
        Assert.Equal(3ul, inForce.Blacklist);
    }

    [Fact]
    public void AnEmptyStoreLeavesTheEnvironmentInCharge()
    {
        // The bootstrap case: an install that has never run the command keeps working.
        var inForce = PavlovBot.Host.Discord.Commands.MenuRoleMap.Empty
            .Over(environmentHighStaff: 1, environmentStaff: 2, environmentBlacklist: 3);

        Assert.Equal("highstaff", inForce.TierFor([1]));
        Assert.Equal("staff", inForce.TierFor([2]));
        Assert.True(inForce.Any);
    }

    [Fact]
    public void NeitherSourceSetMeansNobodyQualifies()
    {
        var inForce = PavlovBot.Host.Discord.Commands.MenuRoleMap.Empty.Over(null, null, null);

        Assert.False(inForce.Any);
        Assert.Null(inForce.TierFor([1, 2, 3]));
    }
}
