using System.Text.Json;
using PavlovBot.Core.Vpn;
using Xunit;

namespace PavlovBot.Tests;

public class VpnConsensusTests
{
    private static TierOutcome Tier(int hits, int answered, int failed = 0) =>
        new(hits, answered, failed, []);

    [Fact]
    public void DetectionDisabledIsNeverActionable()
    {
        var decision = VpnConsensus.Decide(null, null, 0);
        Assert.False(decision.Flagged);
        Assert.False(decision.Actionable);
        Assert.Equal("CLEAN", decision.Label);
    }

    [Fact]
    public void ATotalOutageIsNotCLEAN_ItIsNoVerdict()
    {
        /* The caller treats this as "do not cache, retry next connection". Caching it as
           clean would mean a five-minute provider blip permanently whitelists every
           address that connected during it. */
        var decision = VpnConsensus.Decide(Tier(0, 0, failed: 5), null, 1);
        Assert.False(decision.Actionable);
        Assert.Contains("every regular check failed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanScreenEndsTheCheck()
    {
        var decision = VpnConsensus.Decide(Tier(0, 4), null, 1);
        Assert.False(decision.Flagged);
        Assert.Null(decision.Confirmed);
        Assert.False(decision.Actionable);
    }

    [Fact]
    public void ConfirmationAgreeingMakesItActionable()
    {
        var decision = VpnConsensus.Decide(Tier(2, 4), Tier(1, 1), 1);
        Assert.True(decision.Flagged);
        Assert.True(decision.Confirmed);
        Assert.True(decision.Actionable);
        Assert.Equal("CONFIRMED", decision.Label);
    }

    [Fact]
    public void ConfirmationDisputingItBlocksTheBan()
    {
        // The screen said VPN, the accurate detector said no. The accurate one wins -
        // that is what tier 2 is for.
        var decision = VpnConsensus.Decide(Tier(3, 4), Tier(0, 1), 1);
        Assert.True(decision.Flagged);
        Assert.False(decision.Confirmed);
        Assert.False(decision.Actionable);
        Assert.Equal("DISPUTED", decision.Label);
    }

    [Fact]
    public void OneScreenHitAloneNeverBansWhenThereIsNoConfirmer()
    {
        /* The regression this separation was built for. `confirmed` and `actionable` used
           to be one tri-state, and a null read as permission to ban - so a single
           screening hit banned people and the consensus threshold was dead code.
           ipapi.is reports Cloudflare's 1.1.1.1 as "vpn+abuser"; one opinion is not enough. */
        var decision = VpnConsensus.Decide(Tier(1, 4), Tier(0, 0), configuredConfirmers: 0);
        Assert.True(decision.Flagged);
        Assert.Null(decision.Confirmed);
        Assert.False(decision.Actionable);
        Assert.Equal("FLAGGED (inconclusive)", decision.Label);
    }

    [Fact]
    public void RealScreenConsensusBansWhenThereIsNoConfirmer()
    {
        var decision = VpnConsensus.Decide(Tier(2, 4), Tier(0, 0), configuredConfirmers: 0);
        Assert.True(decision.Actionable);
        Assert.Null(decision.Confirmed);   // still inconclusive as a VERDICT
    }

    [Fact]
    public void TheThresholdIsNotLoweredJustBecauseFewDetectorsAreConfigured()
    {
        // Two screeners, three required. Auto-ban is simply off, and the reason says so
        // rather than quietly banning on one opinion.
        var decision = VpnConsensus.Decide(Tier(2, 2), Tier(0, 0), 0, new VpnThresholds(ScreenBanMin: 3));
        Assert.False(decision.Actionable);
        Assert.Contains("required 3", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AllConfirmersFailingIsReportedDifferentlyFromNoneConfigured()
    {
        // They need different fixes, so they need different messages.
        Assert.Contains("all confirmers failed",
            VpnConsensus.Decide(Tier(2, 4), Tier(0, 0, failed: 1), configuredConfirmers: 1).Reason, StringComparison.Ordinal);
        Assert.Contains("none configured",
            VpnConsensus.Decide(Tier(2, 4), Tier(0, 0), configuredConfirmers: 0).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AThresholdOfZeroIsClampedToOne()
    {
        // Zero would flag every address on the internet.
        var decision = VpnConsensus.Decide(Tier(0, 4), null, 0, new VpnThresholds(ScreenMin: 0));
        Assert.False(decision.Flagged);
    }

    [Fact]
    public void AConfigurationThatCanNeverBanIsDetectable()
    {
        /* Worth saying out loud at startup: two screeners with a ban threshold of three
           never bans anybody, and looks identical to one that simply never sees a VPN. */
        Assert.False(VpnConsensus.CanEverAutoBan(configuredScreeners: 2, configuredConfirmers: 0,
            new VpnThresholds(ScreenBanMin: 3)));
        Assert.True(VpnConsensus.CanEverAutoBan(configuredScreeners: 3, configuredConfirmers: 0,
            new VpnThresholds(ScreenBanMin: 3)));
        Assert.True(VpnConsensus.CanEverAutoBan(configuredScreeners: 1, configuredConfirmers: 1));
    }
}

public class VpnMergeTests
{
    private static DetectorReading Reading(string name, bool flagged = false, bool? hosting = null,
        bool? residential = null, bool? vpn = null, bool? tor = null, double? threat = null,
        string? provider = null, string? organization = null, int tier = 1) =>
        new()
        {
            Name = name, Tier = tier, Flagged = flagged, Hosting = hosting, Residential = residential,
            Vpn = vpn, Tor = tor, ThreatScore = threat, Provider = provider, Organization = organization,
        };

    private static VpnRecord Merge(params DetectorReading[] readings) =>
        VpnMerge.Merge("1.2.3.4", new VpnDecision(true, true, true, "test"),
            new TierOutcome(readings.Count(r => r.Flagged), readings.Length, 0, readings), null, null,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void OneProviderKnowingItIsTorIsEnough()
    {
        // The others are not contradicting it - they have no data.
        Assert.True(Merge(Reading("a", tor: false), Reading("b", tor: true)).Tor);
    }

    [Fact]
    public void NobodyKnowingLeavesItNull_NotFalse()
    {
        Assert.Null(Merge(Reading("a"), Reading("b")).Tor);
    }

    [Fact]
    public void ADirectClassifierOutranksACoarseOne()
    {
        /* IPHub's block==1 only means "non-residential", which is coarse and noisy. ORing
           would let it override IPQS saying the address is plainly residential. */
        var merged = Merge(
            Reading("iphub", hosting: true, residential: false),
            Reading("ipqs", hosting: false, residential: true, tier: 2));

        Assert.False(merged.Hosting);
        Assert.True(merged.Residential);
    }

    [Fact]
    public void SentinelDoesNotWinTheResidentialQuestionItCannotAnswer()
    {
        // It only ever asserts residential=false and never residential=true, so ahead of
        // a real classifier its one-sided answer would decide a question it cannot.
        var merged = Merge(
            Reading("sentinel", residential: false),
            Reading("proxycheck", residential: true));

        Assert.True(merged.Residential);
    }

    [Fact]
    public void AHostingAddressIsNeverReportedResidential()
    {
        // True by definition, even when no provider said so explicitly.
        Assert.False(Merge(Reading("ipqs", hosting: true, tier: 2)).Residential);
    }

    [Fact]
    public void TheConsumerBrandBeatsTheUnderlyingNetworkName()
    {
        // "NordVPN" is what staff want to read; "M247" is what the other detectors know.
        var merged = Merge(Reading("ipapi.is", provider: "M247"), Reading("proxycheck", provider: "NordVPN"));
        Assert.Equal("NordVPN", merged.Provider);
    }

    [Fact]
    public void TheCalibratedRiskScoreWins()
    {
        // IPQS's fraud_score is calibrated 0-100; proxycheck's risk is not the same scale.
        var merged = Merge(Reading("proxycheck", threat: 33), Reading("ipqs", threat: 91, tier: 2));
        Assert.Equal(91, merged.ThreatScore);
    }

    [Fact]
    public void AFlaggedAddressWithNoNamedSignalIsStillReportedAsVpn()
    {
        // Something anonymising was detected even if nobody named which.
        var flagged = VpnMerge.Merge("1.2.3.4", new VpnDecision(true, null, false, "x"),
            new TierOutcome(1, 1, 0, [Reading("proxycheck", flagged: true)]), null, null, DateTimeOffset.UnixEpoch);
        Assert.True(flagged.Vpn);
    }

    [Fact]
    public void WhoisGeolocationBeatsADetectorsCountry()
    {
        // Whois is city-level; a detector's country field is not.
        var geo = new GeoLocation(City: "Toronto", Country: "Canada", Isp: "Bell");
        var merged = VpnMerge.Merge("1.2.3.4", new VpnDecision(false, null, false, "x"),
            new TierOutcome(0, 1, 0, [Reading("iphub") with { Country = "United States", City = "Ashburn" }]),
            null, geo, DateTimeOffset.UnixEpoch);

        Assert.Equal("Toronto", merged.City);
        Assert.Equal("Canada", merged.Country);
    }

    [Theory]
    [InlineData("AS1234", "AS1234")]
    [InlineData("1234", "AS1234")]
    [InlineData("as1234", "AS1234")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AsnsAreNormalisedWithOrWithoutThePrefix(string? input, string? expected)
    {
        Assert.Equal(expected, VpnMerge.NormaliseAsn(input));
    }

    [Fact]
    public void AsnsArriveAsNumbersToo()
    {
        Assert.Equal("AS13335", VpnMerge.NormaliseAsn(13335));
    }

    [Fact]
    public void TheSchemaIsCarriedOnEveryRecord()
    {
        /* Load-bearing: results are cached per address effectively forever, so a cache
           written by an older merge is never re-screened by the new one. Adding Sentinel
           without bumping this left every already-known address on a verdict that had
           never consulted it. */
        Assert.Equal(VpnMerge.Schema, Merge(Reading("a")).Schema);
    }

    [Fact]
    public void TheFullLocationReadsLikeAPlace()
    {
        var geo = new GeoLocation("Toronto", "Ontario", "Canada", "CA", "M5V", "Bell Canada", Timezone: "America/Toronto");
        Assert.Equal("Toronto, Ontario, Canada M5V  -  Bell Canada  -  TZ America/Toronto", geo.FullDescription());
    }

    [Fact]
    public void AnEmptyLocationHasNothingToSay()
    {
        Assert.Null(new GeoLocation().FullDescription());
    }
}

public class IpAddressTests
{
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    public void PrivateAndReservedAddressesAreUnroutable(string ip)
    {
        /* Players on the same LAN connect from one of these. No provider can say anything
           about them, and asking costs a lookup per detector per join - on proxycheck's
           keyless 100/day tier, one LAN player reconnecting twenty times is the whole day. */
        Assert.True(IpAddresses.IsUnroutable(ip));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]      // just outside the private range
    [InlineData("172.15.255.255")]  // just below it
    [InlineData("2606:4700::1")]
    public void PublicAddressesAreRoutable(string ip)
    {
        Assert.False(IpAddresses.IsUnroutable(ip));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not an address")]
    [InlineData("999.999.999.999")]
    public void GarbageIsTreatedAsUnroutableRatherThanLookedUp(string? ip)
    {
        Assert.True(IpAddresses.IsUnroutable(ip));
    }

    [Fact]
    public void TheLocalRecordIsCleanAndNotActionable()
    {
        var record = IpAddresses.LocalRecord("10.0.0.5", DateTimeOffset.UnixEpoch);
        Assert.True(record.Local);
        Assert.False(record.Decision.Flagged);
        Assert.False(record.Decision.Actionable);
    }
}

public class SentinelParserTests
{
    private static DetectorReading? Parse(string json, out string? warning)
    {
        using var document = JsonDocument.Parse(json);
        return SentinelParser.Parse(document.RootElement, out warning);
    }

    [Fact]
    public void TheLookupShapeIsRead()
    {
        var reading = Parse("""
            {"known":true,"verdict":"block","risk_score":88,
             "signals":{"vpn":true,"proxied":false,"tor":false,"dch":true,"anon":false},
             "network":{"asn":9009,"org":"M247 Europe","country":"NL","city":"Amsterdam"}}
            """, out _);

        Assert.NotNull(reading);
        Assert.True(reading!.Flagged);
        Assert.True(reading.Vpn);
        Assert.True(reading.Hosting);
        Assert.Equal("AS9009", reading.Asn);
        Assert.Equal("M247 Europe", reading.Organization);
        Assert.Equal("NL", reading.CountryCode);
        Assert.Equal(88, reading.ThreatScore);
    }

    [Fact]
    public void TheEvaluateShapePutsTheVerdictInNetwork_AndIsDetected()
    {
        /* Both shapes use `network` for different things. Reading only one returned
           nothing for every address - the first "responded but gave no verdict". */
        var reading = Parse("""
            {"network":{"vpn":true,"proxy":false,"datacenter":true,"residential":false,"service":"PROTON_VPN"}}
            """, out _);

        Assert.NotNull(reading);
        Assert.True(reading!.Vpn);
        Assert.True(reading.Hosting);
        Assert.False(reading.Residential);
        Assert.Equal("PROTON_VPN", reading.Provider);
        // ASN/org do not exist in this shape and must not be invented from the verdict keys.
        Assert.Null(reading.Asn);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"true\"")]
    [InlineData("\"yes\"")]
    [InlineData("\"Y\"")]
    public void BooleansEncodedLooselyAreStillBooleans(string encoded)
    {
        // Accepting only a real boolean made a body full of 0/1 look like it had no
        // verdict in it - the second "responded but gave no verdict".
        var reading = Parse($$$"""{"signals":{"vpn":{{{encoded}}}}}""", out _);
        Assert.True(reading!.Vpn);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("\"no\"")]
    public void FalseEncodedLooselyIsStillFalse(string encoded)
    {
        var reading = Parse($$"""{"signals":{"vpn":{{encoded}},"tor":true} }""", out _);
        Assert.False(reading!.Vpn);
    }

    [Fact]
    public void NullIsUnknown_NeverFalse()
    {
        // The rule the whole subsystem turns on.
        Assert.Null(SentinelParser.Boolish(JsonDocument.Parse("null").RootElement));
        Assert.Null(SentinelParser.Boolish(JsonDocument.Parse("\"maybe\"").RootElement));
        Assert.Null(SentinelParser.Boolish(null));
    }

    [Fact]
    public void AnAddressWithNoReputationStillHasAVerdict()
    {
        /* `known: false` comes back with null signals and an explicit call. Taking the
           verdict is right; inferring the per-category fields from it is not. */
        var reading = Parse("""
            {"known":false,"verdict":"allow","risk_score":3,
             "signals":{"vpn":null,"proxied":null,"tor":null},
             "network":{"asn":15169,"org":"Google","country":"US"}}
            """, out _);

        Assert.NotNull(reading);
        Assert.False(reading!.Flagged);
        Assert.Null(reading.Vpn);       // their overall call is known; this is not
        Assert.Null(reading.Proxy);
        Assert.Equal("AS15169", reading.Asn);
        Assert.Contains("no reputation data", reading.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void AReviewVerdictCountsAsFlagged()
    {
        var reading = Parse("""{"known":false,"verdict":"review","signals":{}}""", out _);
        Assert.True(reading!.Flagged);
    }

    [Fact]
    public void AnUnrecognisableBodyReturnsNullAndExplainsWhy()
    {
        // Never a guess - and the message carries the VALUES, because the keys alone were
        // not enough to spot that the booleans were encoded as something else.
        var reading = Parse("""{"lip":"1.2.3.4","latency_ms":42}""", out var warning);

        Assert.Null(reading);
        Assert.NotNull(warning);
        Assert.Contains("lip", warning!, StringComparison.Ordinal);
        Assert.Contains("latency_ms", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatacenterAloneIsNotAHit()
    {
        // The same rule ipapi.is follows: honest players sit behind carrier and cloud ranges.
        var reading = Parse("""{"signals":{"vpn":false,"proxied":false,"tor":false,"dch":true}}""", out _);

        Assert.False(reading!.Flagged);
        Assert.True(reading.Hosting);
        Assert.False(reading.Residential);   // inferable from datacenter, and only that
        Assert.Contains("datacenter", reading.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonObjectBodyIsRejectedCleanly()
    {
        Assert.Null(Parse("[1,2,3]", out var warning));
        Assert.NotNull(warning);
    }
}
