using PavlovBot.Core.Network;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Which addresses an attack was made of, and which may be banned for it.</summary>
public class FloodSourceTests
{
    private static uint Ip(string address) => CapturedPacket.Number(address)!.Value;

    private static void Send(FloodRecorder r, string source, ushort port, int count, PacketProtocol protocol = PacketProtocol.Udp, bool player = false)
    {
        for (var i = 0; i < count; i++) r.Add(new CapturedPacket(Ip(source), port, protocol, 1200), player);
    }

    [Fact]
    public void TheAttacksSourcesAreThoseThatSentTheMinimumToTheGamePortsBusiestFirst()
    {
        var r = new FloodRecorder(targeted: FloodShieldRules.PortMatcher("7777-7799,8177-8199"));
        Send(r, "198.51.100.1", 7777, 400);                       // a bot
        Send(r, "198.51.100.2", 8177, 900);                       // a bot on the query port
        Send(r, "198.51.100.3", 8177, 2);                         // someone's server browser
        Send(r, "162.159.135.232", 51234, 5000);                  // a reply to the bot's own Discord connection
        Send(r, "198.51.100.4", 0, 5000, PacketProtocol.Icmp);    // a monitoring ping
        Send(r, "203.0.113.50", 7777, 5000, player: true);        // a player

        Assert.Equal(["198.51.100.2", "198.51.100.1"], r.TargetingSources(50));
        Assert.Equal(400 + 900 + 2 + 5000 + 5000, r.Totals.Packets);
    }

    [Fact]
    public void WithoutGamePortsNothingIsTargeting()
    {
        var r = new FloodRecorder();
        Send(r, "198.51.100.1", 7777, 400);

        Assert.Empty(r.TargetingSources(1));
    }

    [Fact]
    public void PortBytesAreKeptForTheTargets()
    {
        var r = new FloodRecorder();
        Send(r, "198.51.100.1", 7777, 10);

        Assert.Equal(12_000, r.Snapshot().PortBytes![7777]);
    }

    [Theory]
    [InlineData("7777-7799,8177-8199", 7777, true)]
    [InlineData("7777-7799,8177-8199", 7799, true)]
    [InlineData("7777-7799,8177-8199", 8000, false)]
    [InlineData("7777", 7777, true)]
    [InlineData("7777", 7778, false)]
    public void PortsMatchTheSameListTheFirewallUses(string spec, int port, bool expected) =>
        Assert.Equal(expected, FloodShieldRules.PortMatcher(spec)!((ushort)port));

    [Theory]
    [InlineData("not ports")]
    [InlineData("70000")]
    public void ANonsenseListMatchesNothing(string spec) => Assert.Null(FloodShieldRules.PortMatcher(spec));

    [Theory]
    [InlineData("198.51.100.7", true)]
    [InlineData("203.0.113.5", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("100.64.3.3", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("224.0.0.251", false)]
    [InlineData("2001:db8::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::1", false)]
    public void OnlyInternetAddressesAreEverBanned(string address, bool expected) =>
        Assert.Equal(expected, IpNetworks.IsPublic(IpNetworks.Canonical(address)!));

    [Fact]
    public void PortNamesAreReadAndCleaned()
    {
        var names = AttackReport.ParsePortNames("7777:shack-04, 7779:shack-05 ,bad,0:zero,8177:`**evil**`,9000:");

        Assert.Equal("shack-04", names[7777]);
        Assert.Equal("shack-05", names[7779]);
        Assert.Equal("evil", names[8177]);
        Assert.Equal(3, names.Count);
    }
}
