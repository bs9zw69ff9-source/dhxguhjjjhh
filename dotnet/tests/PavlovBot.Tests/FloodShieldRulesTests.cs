using PavlovBot.Core.Network;
using PavlovBot.Host.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>The nftables rules the DDoS filter loads, and the reading of their counters.</summary>
public class FloodShieldRulesTests
{
    [Theory]
    [InlineData("7777-7799,8177-8199", "7777-7799, 8177-8199")]
    [InlineData("7777", "7777")]
    [InlineData(" 7777 , 7778-7780 ", "7777, 7778-7780")]
    [InlineData("7777; flush ruleset", null)]   // it goes into an nft script: allow-listed, not escaped
    [InlineData("0", null)]
    [InlineData("70000", null)]
    [InlineData("7799-7777", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PortsAreAllowListed(string? spec, string? nft) => Assert.Equal(nft, FloodShieldRules.Ports(spec));

    [Fact]
    public void StandingByKeepsBlocksButSetsNoLimits()
    {
        var script = FloodShieldRules.Script(new ShieldLimits(), engaged: false);

        Assert.Contains("ip saddr @blocked4 counter drop", script, StringComparison.Ordinal);
        Assert.DoesNotContain("per-source", script, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-cap", script, StringComparison.Ordinal);
        Assert.DoesNotContain("flush set inet pavlovbot_ddos blocked4", script, StringComparison.Ordinal);
        Assert.DoesNotContain("flush set inet pavlovbot_ddos players4", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SshAndPlayersComeBeforeAnyDrop()
    {
        var lines = FloodShieldRules.Script(new ShieldLimits(), engaged: true).Split('\n');
        int Index(string marker) => Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));

        Assert.True(Index("comment \"ssh\"") < Index("comment \"players\""));
        Assert.True(Index("comment \"players\"") < Index("comment \"blocked\""));
        Assert.True(Index("comment \"blocked\"") < Index("comment \"per-source\""));
        Assert.True(Index("comment \"per-source\"") < Index("comment \"unknown-cap\""));
    }

    [Fact]
    public void EngagedSetsBothLimitsFromTheSettings()
    {
        var script = FloodShieldRules.Script(new ShieldLimits("7777-7779", PerSourcePps: 800, UnknownPps: 3000, BlockMinutes: 15), engaged: true);

        Assert.Contains("add element inet pavlovbot_ddos gameports { 7777-7779 }", script, StringComparison.Ordinal);
        Assert.Contains("limit rate over 800/second burst 1600 packets } add @blocked4 { ip saddr } counter drop", script, StringComparison.Ordinal);
        Assert.Contains("limit rate over 3000/second burst 6000 packets counter drop", script, StringComparison.Ordinal);
        Assert.Contains("timeout 15m", script, StringComparison.Ordinal);
        Assert.Contains("priority -10", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadPortsSettingIsRefusedBeforeAnythingIsLoaded() =>
        Assert.Throws<ArgumentException>(() => FloodShieldRules.Script(new ShieldLimits("all of them"), engaged: true));

    [Fact]
    public void PlayersGoInInBatches()
    {
        var addresses = Enumerable.Range(1, 1200).Select(i => (uint)(0x0A000000 + i)).ToList();

        var scripts = FloodShieldRules.AllowScripts(addresses, batch: 500).ToList();

        Assert.Equal(3, scripts.Count);
        Assert.StartsWith("add element inet pavlovbot_ddos players4 { 10.0.0.1, 10.0.0.2", scripts[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCountersAreReadByCommentFromNftsOwnJson()
    {
        // The shape `nft -j list table inet pavlovbot_ddos` printed on nftables 1.0.9.
        const string json = """
            {"nftables": [{"metainfo": {"version": "1.0.9", "json_schema_version": 1}},
            {"table": {"family": "inet", "name": "pavlovbot_ddos", "handle": 1}},
            {"set": {"family": "inet", "name": "blocked4", "table": "pavlovbot_ddos", "type": "ipv4_addr", "handle": 3,
              "flags": ["dynamic", "timeout"], "timeout": 600,
              "elem": [{"elem": {"val": "127.0.0.5", "expires": 599}}, {"elem": {"val": "198.51.100.9", "expires": 590}}]}},
            {"rule": {"family": "inet", "table": "pavlovbot_ddos", "chain": "input", "handle": 7, "comment": "blocked",
              "expr": [{"match": {"op": "==", "left": {"payload": {"protocol": "ip", "field": "saddr"}}, "right": "@blocked4"}},
                       {"counter": {"packets": 27991, "bytes": 34372948}}, {"drop": null}]}},
            {"rule": {"family": "inet", "table": "pavlovbot_ddos", "chain": "input", "handle": 8, "comment": "per-source",
              "expr": [{"counter": {"packets": 1, "bytes": 1228}}, {"drop": null}]}},
            {"rule": {"family": "inet", "table": "pavlovbot_ddos", "chain": "input", "handle": 9, "comment": "unknown-cap",
              "expr": [{"counter": {"packets": 4000, "bytes": 1}}, {"drop": null}]}}]}
            """;

        var stats = FloodShieldRules.ParseStats(json)!;

        Assert.Equal(27_991, stats.BlockedPackets);
        Assert.Equal(1, stats.PerSourcePackets);
        Assert.Equal(4_000, stats.CappedPackets);
        Assert.Equal(2, stats.BlockedSources);
        Assert.Equal(31_992, stats.Dropped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"something\": 1}")]
    public void AnythingElseIsNoStats(string json) => Assert.Null(FloodShieldRules.ParseStats(json));

    [Fact]
    public void IftopIsNumericAndShowsPorts() =>
        Assert.Equal(["-i", "eth0", "-nNP", "-t", "-s", "10", "-L", "20"], IftopSnapshot.Argv("eth0", 10));
}
