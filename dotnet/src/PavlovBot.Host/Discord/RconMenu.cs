namespace PavlovBot.Host.Discord;

/// <summary>
/// The exact RCON+ commands that grant and revoke an in-game menu.
/// </summary>
/// <remarks>
/// ONE COMMAND GRANTS A MENU: <c>GiveMenu &lt;name&gt; &lt;bitcode&gt;</c>, and the bitcode is
/// the whole of it. The permissions a tier gets - including moderator and access-manager
/// powers for High Staff - are encoded in the bitcode itself, so the two tiers are two
/// DIFFERENT codes and nothing more.
///
/// IT DOES NOT NEED AddMod/AddAccessManager. Those were carried over from the Node bot, which
/// ran them alongside GiveMenu for High Staff; the server's own menu grants (see the codes
/// below, both read off a live Pavlov.log) do not, because the fuller bitcode already carries
/// those powers. Sending them was extra commands that could be refused and a grant that
/// reported on the wrong one.
///
/// <c>RemoveMenu</c>, not "StripMenu", takes a menu back. The revoke still clears AddMod and
/// AddAccessManager for a former High Staff - harmless when they were never set, and the one
/// thing that cleans up a grant made under the old three-command path.
///
/// THE BITCODES ARE POSITIONAL: a changed character silently grants a DIFFERENT set of buttons
/// rather than failing, which is why each is copied verbatim from a working grant and pinned
/// by a test rather than typed by hand.
/// </remarks>
public static class RconMenu
{
    /// <summary>
    /// The Staff menu bitcode - two segments (31 + 14 bits) with the space between them, as
    /// the server logs it. Verbatim from a working <c>GiveMenu</c> on a live server.
    /// </summary>
    public const string StaffMenuId = "0010010000000000101000000000000 10001000000000";

    /// <summary>
    /// The High Staff menu bitcode - the fuller set, carrying the moderator and access-manager
    /// powers in the mask itself.
    /// </summary>
    /// <remarks>
    /// READ OFF THE WIRE, from this server's own <c>GiveMenu &lt;admin&gt; 0111… 1111…</c> - not
    /// typed by hand, because a positional mask cannot be guessed. If a deployment's High Staff
    /// menu differs, this is the one line to change; the test pins exactly this string so the
    /// change is deliberate.
    /// </remarks>
    public const string HighStaffMenuId = "0111110000000001101000000000010 11111111000010";

    public const string Staff = "staff";
    public const string HighStaff = "highstaff";

    /// <summary>Whether a tier is the fuller High Staff menu.</summary>
    public static bool IsHighStaff(string? tier) =>
        string.Equals(tier, HighStaff, StringComparison.OrdinalIgnoreCase);

    /// <summary>The bitcode for a tier.</summary>
    public static string MenuId(string? tier) => IsHighStaff(tier) ? HighStaffMenuId : StaffMenuId;

    /// <summary>
    /// The command that grants a tier. One line: <c>GiveMenu &lt;player&gt; &lt;bitcode&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Grant(string player, string tier) =>
        [$"GiveMenu {player} {MenuId(tier)}"];

    /// <summary>
    /// Every command needed to revoke, in order.
    /// </summary>
    /// <param name="wasHighStaff">
    /// Also clear AddMod and AddAccessManager. New grants do not set them, but one made under
    /// the old three-command path did, and skipping this would leave that player with in-game
    /// moderator powers after losing the menu - the worse half of the two, invisible from
    /// Discord. Harmless when they were never set.
    /// </param>
    public static IReadOnlyList<string> Revoke(string player, bool wasHighStaff) =>
        wasHighStaff
            ? [$"RemoveMenu {player}", $"RemoveMod {player}", $"RemoveAccessManager {player}"]
            : [$"RemoveMenu {player}"];
}
