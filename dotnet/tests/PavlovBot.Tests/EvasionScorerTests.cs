using PavlovBot.Core.Evasion;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Ported from test/evasion.test.js. The assertions are deliberately the same ones, so a
/// behaviour change between the Node bot and this port shows up as a failing test rather
/// than as a moderator wondering why a ban stopped firing.
///
/// The property that matters throughout: weak signals must not be able to reach the
/// threshold together by accident. Entire ISPs share an ASN, and millions of people share
/// a VPN brand - flagging on those would accuse neighbours.
/// </summary>
public class EvasionScorerTests
{
    private static BanRecord Ban(string playerId, BanNetwork? network = null) => new()
    {
        PlayerId = playerId,
        Reason = "griefing",
        Moderator = "mod#1",
        Permanent = true,
        Network = network,
    };

    [Fact]
    public void NothingKnown_IsNotEvasion()
    {
        var result = EvasionScorer.Find(new JoinContext { Name = "Newcomer" }, []);
        Assert.False(result.IsEvasion);
        Assert.False(result.IsCertain);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void SameEosAccount_IsDefinitive_EvenUnderANewName()
    {
        // The whole point of tracking EOS ids: a name change must not defeat a ban.
        var ban = Ban("OldName", new BanNetwork { EosIds = ["0002abc"] });
        var result = EvasionScorer.Find(new JoinContext { Name = "BrandNewName", EosId = "0002abc" }, [ban]);

        Assert.True(result.IsEvasion);
        Assert.True(result.IsCertain);
        Assert.Equal(100, result.Score);
        Assert.Equal(SignalKind.Eos, result.Matches[0].Reasons[0].Kind);
    }

    [Fact]
    public void BannedIp_ScoresHighEnoughAlone()
    {
        var ban = Ban("Griefer", new BanNetwork { Ips = ["203.0.113.5"] });
        var result = EvasionScorer.Find(new JoinContext { Name = "Someone", Ip = "203.0.113.5" }, [ban]);

        Assert.True(result.IsEvasion);
        Assert.False(result.IsCertain);   // strong, but not proof - households share addresses
        Assert.Equal(70, result.Score);
    }

    [Fact]
    public void ExactUsernameMatch_ReachesTheThreshold()
    {
        var result = EvasionScorer.Find(new JoinContext { Name = "Griefer" }, [Ban("Griefer")]);
        Assert.True(result.IsEvasion);
        Assert.Equal(50, result.Score);
    }

    [Fact]
    public void KnownAlias_CountsAsAUsernameMatch()
    {
        var ban = Ban("Griefer", new BanNetwork { Names = ["GrieferAlt"] });
        var result = EvasionScorer.Find(new JoinContext { Name = "Unrelated", AltNames = ["GrieferAlt"] }, [ban]);

        Assert.True(result.IsEvasion);
        Assert.Contains(result.Matches[0].Reasons, r => r.Kind == SignalKind.Username);
    }

    [Fact]
    public void SharedAsnAlone_NeverReports()
    {
        // A single ASN can be an entire national ISP. Reporting on it would flag neighbours.
        var ban = Ban("Griefer", new BanNetwork { Asn = "AS7029" });
        var result = EvasionScorer.Find(new JoinContext { Name = "Innocent", Asn = "AS7029" }, [ban]);

        Assert.False(result.IsEvasion);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void SharedProviderPlusAsn_StaysBelowTheThreshold()
    {
        /* 25 + 10 = 35 < 50, deliberately. Millions of people share NordVPN and its ASN
           simultaneously; if this combination fired, every VPN user would be accused. */
        var ban = Ban("Griefer", new BanNetwork { Provider = "NordVPN", Asn = "AS9009" });
        var result = EvasionScorer.Find(
            new JoinContext { Name = "Innocent", Provider = "NordVPN", Asn = "AS9009" }, [ban]);

        Assert.False(result.IsEvasion);
    }

    [Fact]
    public void SharedAnonymisingAsnPlusProvider_DoesReport()
    {
        // 25 + 20 = 45... still short. Confirmed by arithmetic, not by hope.
        var ban = Ban("Griefer", new BanNetwork { Provider = "NordVPN", Asn = "AS9009", Vpn = true });
        var result = EvasionScorer.Find(
            new JoinContext { Name = "Innocent", Provider = "NordVPN", Asn = "AS9009", Vpn = true }, [ban]);

        Assert.False(result.IsEvasion);
        // ...but add ANY real signal and it crosses.
        var withIp = EvasionScorer.Find(
            new JoinContext { Name = "Innocent", Provider = "NordVPN", Asn = "AS9009", Vpn = true, Ip = "1.2.3.4" },
            [Ban("Griefer", new BanNetwork { Provider = "NordVPN", Asn = "AS9009", Vpn = true, Ips = ["1.2.3.4"] })]);
        Assert.True(withIp.IsEvasion);
    }

    [Fact]
    public void NullAnonymisingFlags_DoNotCountAsAnonymising()
    {
        /* This is the tri-state bug the port is meant to make impossible. `null` means no
           detector could answer; treating it as `true` would upgrade a plain shared-ISP hit
           to the stronger asn+vpn weight on no evidence at all. */
        var ban = Ban("Griefer", new BanNetwork { Asn = "AS7029", Vpn = null, Hosting = null });
        var join = new JoinContext { Name = "Innocent", Asn = "AS7029", Vpn = null, Hosting = null };

        var result = EvasionScorer.Find(join, [ban]);
        Assert.False(result.IsEvasion);   // weak ASN only, correctly discarded
    }

    [Fact]
    public void MatchingIsCaseAndWhitespaceInsensitive()
    {
        var ban = Ban("Griefer", new BanNetwork { Names = ["  GRIEFER  "] });
        var result = EvasionScorer.Find(new JoinContext { Name = "griefer" }, [ban]);
        Assert.True(result.IsEvasion);
    }

    [Fact]
    public void RegistryFlags_FireWithoutAnyBanRecord()
    {
        // An auto-ban can flag an id/IP/name without writing a per-player ban row.
        var flags = new RegistryFlags { EosIds = new HashSet<string> { "0002abc" } };
        var result = EvasionScorer.Find(new JoinContext { Name = "Whoever", EosId = "0002abc" }, [], flags);

        Assert.True(result.IsEvasion);
        Assert.True(result.IsCertain);
        Assert.Equal(100, result.Score);
        Assert.Single(result.Flags);
    }

    [Fact]
    public void AFlaggedIp_IsNotCertainOnItsOwn()
    {
        var flags = new RegistryFlags { Ips = new HashSet<string> { "203.0.113.5" } };
        var result = EvasionScorer.Find(new JoinContext { Name = "Whoever", Ip = "203.0.113.5" }, [], flags);

        Assert.True(result.IsEvasion);
        Assert.False(result.IsCertain);   // a shared address must not read as proof
    }

    [Fact]
    public void StrongestMatchIsReportedFirst()
    {
        var weak = Ban("WeakMatch", new BanNetwork { Names = ["Target"] });                 // 50
        var strong = Ban("StrongMatch", new BanNetwork { Ips = ["1.2.3.4"], Names = ["Target"] });   // 120
        var result = EvasionScorer.Find(new JoinContext { Name = "Target", Ip = "1.2.3.4" }, [weak, strong]);

        Assert.Equal("StrongMatch", result.Matches[0].PlayerId);
        Assert.Equal(120, result.Score);
        // Reasons within a match are strongest-first too.
        Assert.Equal(SignalKind.Ip, result.Matches[0].Reasons[0].Kind);
    }

    [Fact]
    public void CorruptBanRowsAreSkipped_NotThrown()
    {
        // The ban store is a JSON file that has been hand-edited before now.
        var bans = new List<BanRecord> { Ban(""), Ban("   "), Ban("Griefer") };
        var result = EvasionScorer.Find(new JoinContext { Name = "Griefer" }, bans);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void ABanWithNoNetworkSnapshot_StillMatchesOnName()
    {
        // Bans recorded before network snapshots existed must keep working.
        var result = EvasionScorer.Find(new JoinContext { Name = "Griefer" }, [Ban("Griefer", network: null)]);
        Assert.True(result.IsEvasion);
    }
}
