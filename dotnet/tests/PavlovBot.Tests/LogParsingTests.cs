using PavlovBot.Core.Evasion;
using PavlovBot.Core.Logs;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Every pattern here was validated against real Pavlov logs. A parser that stops matching
/// after a game update does not fail loudly - the log still reads, it just stops finding
/// anyone, and the ban-evasion system silently does nothing.
/// </summary>
public class PavlovLogTests
{
    [Fact]
    public void TheLinesOwnTimestampIsRead()
    {
        /* Using the line's timestamp rather than the read time is what makes the
           correlation window meaningful when the tailer catches up on a backlog: read time
           would put a thousand lines in the same instant. */
        var at = PavlovLog.TimestampOf("[2026.07.15-18.30.45:123]LogNet: something");
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 18, 30, 45, 123, TimeSpan.Zero), at);
    }

    [Fact]
    public void ALineWithNoOrACorruptTimestampDoesNotStopTheParse()
    {
        Assert.Null(PavlovLog.TimestampOf("LogNet: no timestamp here"));
        Assert.Null(PavlovLog.TimestampOf("[2026.13.45-99.99.99:999]LogNet: corrupt"));
    }

    [Fact]
    public void AnAcceptedConnectionYieldsItsAddress()
    {
        Assert.Equal("203.0.113.5", PavlovLog.AcceptedAddress(
            "[2026.07.15-18.30.45:123]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777"));
        Assert.Equal("203.0.113.6", PavlovLog.AcceptedAddress(
            "LogNet: AddClientConnection: Added client connection RemoteAddr: 203.0.113.6:7777"));
    }

    [Fact]
    public void AConfirmedPairingNeedsBothFieldsOnOneLine()
    {
        // This pairing is CERTAIN and is the only thing allowed to flag an address.
        var pairing = PavlovLog.Confirmed(
            "LogNet: UChannel::Close: UniqueId: EOS:0002abc123 RemoteAddr: 203.0.113.5:7777");

        Assert.NotNull(pairing);
        Assert.Equal("203.0.113.5", pairing!.Ip);
        Assert.Equal("0002abc123", pairing.Id);
    }

    [Fact]
    public void ConfirmationIsFieldOrderIndependent()
    {
        /* Pavlov's field order is not stable across builds, and a positional parse breaks
           silently on an update. */
        var pairing = PavlovLog.Confirmed("LogNet: RemoteAddr: 203.0.113.5:7777, UniqueId: EOS:0002abc123");
        Assert.Equal("0002abc123", pairing!.Id);
    }

    [Fact]
    public void OneFieldAloneIsNotAConfirmation()
    {
        Assert.Null(PavlovLog.Confirmed("LogNet: RemoteAddr: 203.0.113.5:7777"));
        Assert.Null(PavlovLog.Confirmed("LogNet: UniqueId: EOS:0002abc123"));
    }

    [Fact]
    public void ALoginRequestYieldsTheNameAndTheId()
    {
        var login = PavlovLog.Login(
            "[2026.07.15-18.30.45:123]LogNet: Login request: ?Name=Alice userId: EOS:0002abc123 platform: EOS");

        Assert.NotNull(login);
        Assert.Equal("Alice", login!.Name);
        Assert.Equal("0002abc123", login.Id);
    }

    [Fact]
    public void ANameEndsBeforeTheFollowingFieldEvenWithNoSeparator()
    {
        /* Real lines are `?Name=Bob userId: EOS:...` with no ? or & before userId, so a
           lazy match to the next option alone would swallow the id into the name. */
        var login = PavlovLog.Login("LogNet: Join request: ?Name=Bob Smith userId: EOS:0002abc");
        Assert.Equal("Bob Smith", login!.Name);
        Assert.Equal("0002abc", login.Id);
    }

    [Fact]
    public void ALoginWithoutANameStillLinksTheAccount()
    {
        var login = PavlovLog.Login("LogNet: Login request: userId: EOS:0002abc123");
        Assert.Null(login!.Name);
        Assert.Equal("0002abc123", login.Id);
    }

    [Fact]
    public void ALineThatIsNotALoginIsIgnoredEvenWithAnIdOnIt()
    {
        Assert.Null(PavlovLog.Login("LogNet: some other line userId: EOS:0002abc"));
    }

    [Theory]
    [InlineData("EOS:0002abc123", "0002abc123")]
    [InlineData("NULL:abc123", "abc123")]
    [InlineData("0002abc123", "0002abc123")]
    [InlineData("EOS:0002abc123'", "0002abc123")]
    public void IdsAreNormalised(string raw, string expected)
    {
        /* The trailing-punctuation strip is not cosmetic: NetworkFailure lines quote the
           id, and an id with a stray apostrophe does not match the same account seen
           anywhere else - so an evader's flag silently misses. */
        Assert.Equal(expected, PavlovLog.CleanId(raw));
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("localhost-abc")]
    [InlineData("")]
    [InlineData(null)]
    public void PlaceholderIdsIdentifyNobody(string? id)
    {
        // Tracking one attributes real addresses to a phantom account.
        Assert.True(PavlovLog.IsPlaceholderId(id));
    }

    [Fact]
    public void RconAndRconPlusCommandsAreRecognised()
    {
        Assert.Equal("Alice", PavlovLog.BannedByRcon("LogTemp: Rcon: BanPlayer Alice"));
        Assert.Equal("Alice", PavlovLog.UnbannedByRcon("LogTemp: Rcon: UnbanPlayer Alice"));
        Assert.Equal("GiveMenu SomePlayer",
            PavlovLog.RconPlusCommand("LogTemp: Warning: Rcon Plus Command Executed: GiveMenu SomePlayer"));
        // The Error level is just how the mod prints, not a failure.
        Assert.Equal("Ban Bob",
            PavlovLog.RconPlusCommand("LogTemp: Error: Rcon Plus Command Executed: Ban Bob"));
    }

    [Fact]
    public void AKillRecordIsReadWhetherWholeOrSplit()
    {
        // Pavlov logs it as one JSON line or split across several, so each field matches
        // independently and the caller assembles partial records.
        var whole = PavlovLog.Kill("""LogTemp: KillData {"Killer": "Alice", "Killed": "Bob", "KilledBy": "AK47"}""");
        Assert.Equal("Alice", whole!.Killer);
        Assert.Equal("Bob", whole.Killed);
        Assert.Equal("AK47", whole.KilledBy);

        var partial = PavlovLog.Kill("""    "Killer": "Alice",""");
        Assert.Equal("Alice", partial!.Killer);
        Assert.Null(partial.Killed);
    }

    [Theory]
    [InlineData("203.0.113.5", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("999.1.1.1", false)]
    [InlineData("203.0.113", false)]
    [InlineData("not an ip", false)]
    public void AddressesAreOctetBounded(string value, bool valid)
    {
        Assert.Equal(valid, PavlovLog.IsIpv4(value));
    }
}

public class JoinCorrelatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private const string File = "server1.log";

    [Fact]
    public void OneConnectionThenOneLoginIsAConfidentGuess()
    {
        var correlator = new JoinCorrelator();
        correlator.Accepted(File, "203.0.113.5", T0);

        var guess = correlator.Login(File, T0.AddSeconds(2));
        Assert.Equal("203.0.113.5", guess.Ip);
        Assert.True(guess.Confident);
    }

    [Fact]
    public void TwoConnectionsAtOnceMeanWeDoNotKnow()
    {
        /* The whole reason confidence is tracked. With two people connecting at once there
           is no way to tell which address belongs to which login, and a coin flip here
           attributes a stranger's address to an account - which, if it were allowed to
           flag, would ban them. */
        var correlator = new JoinCorrelator();
        correlator.Accepted(File, "203.0.113.5", T0);
        correlator.Accepted(File, "203.0.113.6", T0.AddSeconds(1));

        var guess = correlator.Login(File, T0.AddSeconds(2));
        Assert.False(guess.Confident);
    }

    [Fact]
    public void TheSameAddressTwiceIsStillOneConnection()
    {
        // A retrying client is not two people.
        var correlator = new JoinCorrelator();
        correlator.Accepted(File, "203.0.113.5", T0);
        correlator.Accepted(File, "203.0.113.5", T0.AddSeconds(1));

        Assert.True(correlator.Login(File, T0.AddSeconds(2)).Confident);
    }

    [Fact]
    public void AnAcceptTooLongBeforeTheLoginIsUnrelated()
    {
        // Without the window, a quiet server correlates a login to an accept from minutes ago.
        var correlator = new JoinCorrelator(TimeSpan.FromSeconds(10));
        correlator.Accepted(File, "203.0.113.5", T0);

        var guess = correlator.Login(File, T0.AddSeconds(30));
        Assert.Null(guess.Ip);
        Assert.False(guess.Confident);
    }

    [Fact]
    public void StaleAmbiguityExpiresRatherThanPoisoningEveryLaterJoin()
    {
        /* Without expiry the pending set stays above one forever and every join reports as
           ambiguous - which looks like the feature is broken rather than cautious. */
        var correlator = new JoinCorrelator(TimeSpan.FromSeconds(10));
        correlator.Accepted(File, "203.0.113.5", T0);
        correlator.Accepted(File, "203.0.113.6", T0.AddSeconds(1));

        correlator.Accepted(File, "203.0.113.7", T0.AddMinutes(5));
        Assert.True(correlator.Login(File, T0.AddMinutes(5).AddSeconds(1)).Confident);
    }

    [Fact]
    public void APlaceholderLoginDoesNotConsumeThePendingConnection()
    {
        /* The pre-authentication line arrives first for the SAME connection; the real login
           is still coming, and clearing here would leave it with nothing to correlate. */
        var correlator = new JoinCorrelator();
        correlator.Accepted(File, "203.0.113.5", T0);

        correlator.Login(File, T0.AddSeconds(1), isPlaceholderId: true);
        var real = correlator.Login(File, T0.AddSeconds(2));

        Assert.Equal("203.0.113.5", real.Ip);
        Assert.True(real.Confident);
    }

    [Fact]
    public void ARealLoginConsumesIt_SoTheNextJoinDoesNotInheritTheAddress()
    {
        var correlator = new JoinCorrelator();
        correlator.Accepted(File, "203.0.113.5", T0);
        correlator.Login(File, T0.AddSeconds(1));

        Assert.Null(correlator.Login(File, T0.AddSeconds(2)).Ip);
    }

    [Fact]
    public void TwoServersDoNotCorrelateAgainstEachOther()
    {
        // Independent connection streams; sharing state would attribute one server's
        // address to the other's login.
        var correlator = new JoinCorrelator();
        correlator.Accepted("server1.log", "203.0.113.5", T0);

        Assert.Null(correlator.Login("server2.log", T0.AddSeconds(1)).Ip);
        Assert.Equal("203.0.113.5", correlator.Login("server1.log", T0.AddSeconds(1)).Ip);
    }
}

public class FlagMatchingTests
{
    private static FlagSet Flags(string[]? ips = null, string[]? names = null, string[]? ids = null, string[]? manual = null) =>
        new(new HashSet<string>(ips ?? [], StringComparer.Ordinal),
            new HashSet<string>(names ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(ids ?? [], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(manual ?? [], StringComparer.Ordinal));

    [Fact]
    public void AFlaggedAddressIsAHit()
    {
        var verdict = FlagMatching.Check(Flags(ips: ["203.0.113.5"]), "203.0.113.5", "Alice", "0002abc");
        Assert.Equal(FlagMatch.Ip, verdict.Match);
        Assert.Contains("blacklisted ip", verdict.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingIsExactAndNeverBySubnet()
    {
        /* A /24 covers 254 addresses, and on a residential ISP those belong to 254
           unrelated households - one evader would ban a neighbourhood. */
        Assert.False(FlagMatching.Check(Flags(ips: ["203.0.113.5"]), "203.0.113.6", null, null).Hit);
    }

    [Fact]
    public void AnAccountFlagBeatsANameFlagWhenBothApply()
    {
        /* The detail wording drives the ban decision: an account flag carries the ban's own
           expiry semantics, a name flag does not, so the one that expires must win. */
        var verdict = FlagMatching.Check(Flags(names: ["Alice"], ids: ["0002abc"]), null, "Alice", "0002abc");
        Assert.Equal(FlagMatch.AccountId, verdict.Match);
    }

    [Fact]
    public void AManualAddressFlagCountsTheSameAsABanDerivedOne()
    {
        Assert.Equal(FlagMatch.Ip, FlagMatching.Check(Flags(manual: ["203.0.113.5"]), "203.0.113.5", null, null).Match);
    }

    [Fact]
    public void NamesAndIdsMatchCaseInsensitively_AddressesDoNot()
    {
        Assert.True(FlagMatching.Check(Flags(names: ["alice"]), null, "ALICE", null).Hit);
        Assert.True(FlagMatching.Check(Flags(ids: ["0002ABC"]), null, null, "0002abc").Hit);
    }

    [Fact]
    public void NothingKnownIsClean()
    {
        Assert.False(FlagMatching.Check(FlagSet.Empty, "203.0.113.5", "Alice", "0002abc").Hit);
        Assert.False(FlagMatching.Check(Flags(ips: ["203.0.113.5"]), null, null, null).Hit);
    }
}

public class PendingFlagTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APendingFlagExpires()
    {
        /* The case it exists for: banning somebody currently connected. Their address is
           confirmed by the DISCONNECT the ban is about to cause. After a few minutes any
           disconnect is somebody else's. */
        var pending = new PendingFlag("0002abc", T0, FlagAccountId: true);
        Assert.True(pending.IsLive(T0.AddMinutes(2), TimeSpan.FromMinutes(5)));
        Assert.False(pending.IsLive(T0.AddMinutes(10), TimeSpan.FromMinutes(5)));
    }
}
