using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The RCON+ commands that grant and revoke an in-game menu.
/// </summary>
/// <remarks>
/// The port got every one of these wrong, in three files independently, and none of them
/// failed loudly: RCON accepts an unknown command and answers, so each looked successful
/// from Discord while doing nothing on the server. That is why staff received a menu with
/// no permissions in it.
/// </remarks>
public class RconMenuTests
{
    [Fact]
    public void GrantingIsTheSingleGiveMenuCommand()
    {
        /* ALL IT NEEDS IS `GiveMenu <player> <bitcode>`. The bitcode is positional and cannot
           be omitted or abbreviated; the AddMod/AddAccessManager the Node bot bolted on are
           NOT sent - the fuller bitcode carries those powers itself. */
        var commands = RconMenu.Grant("Pkdestroy", RconMenu.Staff);

        Assert.Equal([$"GiveMenu Pkdestroy {RconMenu.StaffMenuId}"], commands);
        Assert.Contains(" ", RconMenu.StaffMenuId, StringComparison.Ordinal);   // the space is part of the code
    }

    [Fact]
    public void TheBitCodesAreTheOnesTheServerItselfSends()
    {
        // Positional: a changed character silently grants a DIFFERENT set of buttons rather
        // than failing, so each is pinned verbatim from a working grant on a live server.
        Assert.Equal("0010010000000000101000000000000 10001000000000", RconMenu.StaffMenuId);
        Assert.Equal("0111110000000001101000000000010 11111111000010", RconMenu.HighStaffMenuId);
    }

    [Fact]
    public void HighStaffIsTheFullerCode_NotExtraCommands()
    {
        // High Staff is one command too, just a different (fuller) bitcode - the mod and
        // access-manager powers live in the mask, not in separate AddMod/AddAccessManager runs.
        var commands = RconMenu.Grant("Pkdestroy", RconMenu.HighStaff);

        Assert.Equal([$"GiveMenu Pkdestroy {RconMenu.HighStaffMenuId}"], commands);
    }

    [Fact]
    public void TheTwoTiersUseDifferentMenus()
    {
        Assert.NotEqual(RconMenu.StaffMenuId, RconMenu.HighStaffMenuId);
        Assert.Contains(RconMenu.HighStaffMenuId, RconMenu.Grant("X", RconMenu.HighStaff)[0], StringComparison.Ordinal);
        Assert.Contains(RconMenu.StaffMenuId, RconMenu.Grant("X", RconMenu.Staff)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void RevokingUsesRemoveMenu()
    {
        // "StripMenu" is not an RCON+ verb. Revoking appeared to work and left the menu.
        Assert.Equal(["RemoveMenu Pkdestroy"], RconMenu.Revoke("Pkdestroy", wasHighStaff: false));
    }

    [Fact]
    public void RevokingHighStaffTakesBackTheExtras()
    {
        /* AddMod ran at grant time, so skipping this leaves the player with in-game
           moderator powers after losing the menu - the worse half of the two, and invisible
           from Discord. */
        Assert.Equal(
            ["RemoveMenu Pkdestroy", "RemoveMod Pkdestroy", "RemoveAccessManager Pkdestroy"],
            RconMenu.Revoke("Pkdestroy", wasHighStaff: true));
    }

    [Fact]
    public void AnUnknownTierIsTreatedAsPlainStaff()
    {
        // Never as high staff: a typo must not hand out moderator powers.
        Assert.False(RconMenu.IsHighStaff("HIGHSTAFF ")); 
        Assert.False(RconMenu.IsHighStaff("mod"));
        Assert.False(RconMenu.IsHighStaff(null));
        Assert.True(RconMenu.IsHighStaff("HighStaff"));   // case only
    }
}
