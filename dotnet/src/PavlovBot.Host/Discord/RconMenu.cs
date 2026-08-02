namespace PavlovBot.Host.Discord;

/// <summary>
/// The exact RCON+ commands that grant and revoke an in-game menu.
/// </summary>
/// <remarks>
/// ONE PLACE, because the port got every one of them wrong and got them wrong in three
/// files independently. The verbs are RCON+'s, not names that read sensibly:
///
///   <c>GiveMenu &lt;name&gt; &lt;bitcode&gt;</c> - the BIT CODE IS NOT OPTIONAL. The port
///   sent <c>GiveMenu &lt;name&gt;</c> with no code at all, so the command was accepted and
///   granted nothing. That is why staff got a menu with no permissions in it.
///
///   <c>AddMod</c> and <c>AddAccessManager</c>, not "GiveMod" and "GiveAccessManager". The
///   port invented the Give- forms by analogy with GiveMenu; RCON+ does not have them, so
///   high staff silently never received moderator powers.
///
///   <c>RemoveMenu</c>, not "StripMenu". Same mistake in the other direction: revoking
///   appeared to work and left the menu in place.
///
/// None of these fail loudly. RCON accepts an unknown command and answers, so every one of
/// these looked successful from Discord while doing nothing on the server.
/// </remarks>
public static class RconMenu
{
    /// <summary>
    /// The staff menu bit code, exactly as the Node bot sends it - spaces included.
    /// </summary>
    /// <remarks>
    /// It is a positional bitmask, so a changed character silently grants a different set of
    /// buttons rather than failing. Copied verbatim and pinned by a test for that reason.
    /// High Staff uses the SAME code: the difference is the extra AddMod and
    /// AddAccessManager, not the menu.
    /// </remarks>
    public const string StaffMenuId = "0010010000000000101000000000000 10001000000000";

    public const string Staff = "staff";
    public const string HighStaff = "highstaff";

    /// <summary>Whether a tier gets moderator and access-manager powers on top of the menu.</summary>
    public static bool IsHighStaff(string? tier) =>
        string.Equals(tier, HighStaff, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every command needed to grant a tier, in order.
    /// </summary>
    /// <remarks>
    /// The menu comes LAST for high staff, matching the Node bot: it is the command whose
    /// success the caller reports on, so it should be the one that ran most recently.
    /// </remarks>
    public static IReadOnlyList<string> Grant(string player, string tier)
    {
        var menu = $"GiveMenu {player} {StaffMenuId}";

        return IsHighStaff(tier)
            ? [$"AddMod {player}", $"AddAccessManager {player}", menu]
            : [menu];
    }

    /// <summary>
    /// Every command needed to revoke, in order.
    /// </summary>
    /// <param name="wasHighStaff">
    /// Revoke the extras too. AddMod was run at grant time, so skipping this leaves the
    /// player with in-game moderator powers after losing the menu - which is the worse half
    /// of the two, and invisible from Discord.
    /// </param>
    public static IReadOnlyList<string> Revoke(string player, bool wasHighStaff) =>
        wasHighStaff
            ? [$"RemoveMenu {player}", $"RemoveMod {player}", $"RemoveAccessManager {player}"]
            : [$"RemoveMenu {player}"];
}
