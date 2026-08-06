using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Police actions must reach the police channel.
/// </summary>
/// <remarks>
/// REPORTED FROM PRODUCTION: "arrests etc do not go to police logs". They could not.
/// POLICE_LOG_CHANNEL and ARREST_CHANNEL are documented in .env.example and implemented in
/// the Node bot (index.js:416, `POLICE_LOG_CHANNEL || ARREST_CHANNEL || STAFF_LOG_CHANNEL`),
/// and the C# bot - which is production - read neither variable anywhere. Every arrest fell
/// through to the general staff log, which is precisely what a police channel exists to keep
/// them out of. A port gap, not a misconfiguration.
/// </remarks>
public class PoliceLogRoutingTests
{
    private const ulong Mod = 1, Ban = 2, Police = 3, Arrest = 4;

    [Theory]
    [InlineData("arrest")]
    [InlineData("release")]
    [InlineData("warrant")]
    [InlineData("suspendrank")]
    [InlineData("promotion")]
    public void PoliceActionsGoToThePoliceChannel(string action) =>
        Assert.Equal(Police, ChannelStaffLog.ChannelFor(action, new StaffLogChannels(Mod, Ban, Police)));

    [Fact]
    public void ArrestChannelOverridesThePoliceChannelForBookingsOnly()
    {
        // ".env.example: ARREST_CHANNEL optionally overrides just the arrest bookings."
        var channels = new StaffLogChannels(Mod, Ban, Police, Arrest);

        Assert.Equal(Arrest, ChannelStaffLog.ChannelFor("arrest", channels));
        Assert.Equal(Police, ChannelStaffLog.ChannelFor("warrant", channels));
        Assert.Equal(Police, ChannelStaffLog.ChannelFor("release", channels));
    }

    [Fact]
    public void ArrestChannelAloneStillServesEveryPoliceAction()
    {
        /* Matching the Node precedence, where policeChannelId falls back to ARREST_CHANNEL.
           An operator who set only ARREST_CHANNEL asked for police lines to go there, and
           dropping the non-arrest ones would be an odd reading of that. */
        var channels = new StaffLogChannels(Mod, Ban, Police: null, Arrest: Arrest);

        Assert.Equal(Arrest, ChannelStaffLog.ChannelFor("arrest", channels));
        Assert.Equal(Arrest, ChannelStaffLog.ChannelFor("warrant", channels));
    }

    [Fact]
    public void WithoutAPoliceChannelTheyStillGoSomewhere()
    {
        // The behaviour before this change, and still correct when nobody configured one.
        // Falling through beats dropping the line.
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("arrest", new StaffLogChannels(Mod, Ban)));
        Assert.Equal(Ban, ChannelStaffLog.ChannelFor("arrest", new StaffLogChannels(null, Ban)));
    }

    [Fact]
    public void BansAndGeneralActionsAreUnaffected()
    {
        /* The regression that would matter most: police routing must not capture anything it
           should not. "unban" contains no police word, and a ban must still outrank it. */
        var channels = new StaffLogChannels(Mod, Ban, Police, Arrest);

        Assert.Equal(Ban, ChannelStaffLog.ChannelFor("permban", channels));
        Assert.Equal(Ban, ChannelStaffLog.ChannelFor("unban", channels));
        Assert.Equal(Ban, ChannelStaffLog.ChannelFor("vpnban", channels));
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("serverswitch", channels));
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("kick", channels));
        Assert.Equal(Mod, ChannelStaffLog.ChannelFor("manual", channels));
    }

    [Fact]
    public void APoliceChannelOnItsOwnIsEnoughToEnableTheSink()
    {
        /* Program.cs used to attach the sink only when the mod or ban channel was set, so an
           operator who configured ONLY a police channel got nothing at all. */
        var channels = new StaffLogChannels(null, null, Police);

        Assert.True(channels.Any);
        Assert.Equal(Police, ChannelStaffLog.ChannelFor("arrest", channels));

        var log = new ChannelStaffLog(new RecordingPostTarget(), channels,
            NullLogger<ChannelStaffLog>.Instance);
        Assert.True(log.Enabled);
    }
}
