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
    public void GrantingSendsTheBitCode()
    {
        /* THE BUG. "GiveMenu <name>" with no bit code is accepted and grants nothing - and
           the bit code is positional, so it cannot be omitted or abbreviated. */
        var commands = RconMenu.Grant("Pkdestroy", RconMenu.Staff);

        Assert.Equal([$"GiveMenu Pkdestroy {RconMenu.StaffMenuId}"], commands);
        Assert.Contains(" ", RconMenu.StaffMenuId, StringComparison.Ordinal);   // the space is part of it
    }

    [Fact]
    public void TheBitCodeIsTheOneTheNodeBotSends()
    {
        // Positional: a changed character silently grants a DIFFERENT set of buttons rather
        // than failing, so it is pinned verbatim.
        Assert.Equal("0010010000000000101000000000000 10001000000000", RconMenu.StaffMenuId);
    }

    [Fact]
    public void HighStaffAlsoGetsModAndAccessManager()
    {
        /* AddMod and AddAccessManager - NOT "GiveMod"/"GiveAccessManager", which the port
           invented by analogy with GiveMenu. RCON+ has no such verbs, so high staff silently
           never received moderator powers. */
        var commands = RconMenu.Grant("Pkdestroy", RconMenu.HighStaff);

        Assert.Equal(
            ["AddMod Pkdestroy", "AddAccessManager Pkdestroy", $"GiveMenu Pkdestroy {RconMenu.StaffMenuId}"],
            commands);
    }

    [Fact]
    public void HighStaffUsesTheSameMenuAsStaff()
    {
        // The difference is the extra powers, not a different menu.
        Assert.Contains(RconMenu.StaffMenuId, RconMenu.Grant("X", RconMenu.HighStaff)[^1], StringComparison.Ordinal);
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
