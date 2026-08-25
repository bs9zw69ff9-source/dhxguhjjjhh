using System.Text;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Turning a faction name into something Discord will accept as an option name.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. Per-faction role options used to be two fixed slots named after the
/// built-in factions, <c>mafia_role</c> and <c>nypd_role</c>. A bot running a configured
/// faction set has neither, so those slots granted nothing and its only way to delegate a
/// roster was the leader role - which manages EVERY roster. That is the separation rule
/// failing open, and the fix is options built from the loaded set, which means turning
/// "Brotherhood of Steel" into a name Discord will take.
///
/// DISCORD'S RULES ARE NARROW AND THE REJECTION IS ATOMIC. An option name must be 1-32
/// characters of letters, digits, dashes and underscores, and lowercase. One bad name does
/// not fail one command: the bulk overwrite is rejected whole, and every command disappears
/// from the picker. So this cannot be a best-effort <c>ToLower().Replace(" ", "_")</c> - a
/// faction named "Followers of the Apocalypse!" has to come out usable or not at all.
/// </remarks>
internal static class CommandOptionName
{
    /// <summary>Discord's limit on an option name.</summary>
    private const int MaxLength = 32;

    /// <summary>
    /// A Discord-safe option name, or null when nothing usable survives.
    /// </summary>
    /// <remarks>
    /// Null rather than a fallback like "faction_1", because a silently renumbered option is
    /// worse than an absent one: the operator sees a slot with no relationship to the faction
    /// it sets, and the caller here can say which faction it could not name.
    ///
    /// Only ASCII letters and digits are kept. Discord does accept other scripts, but the
    /// name is also what an operator types, and a faction whose option name cannot be typed
    /// on the keyboard in front of them is not usable either.
    /// </remarks>
    public static string? Slug(string? name, string suffix = "")
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var builder = new StringBuilder(name.Length + suffix.Length);
        var underscore = false;

        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                underscore = false;
            }
            else if (!underscore && builder.Length > 0)
            {
                // Collapsed, so "Followers of the Apocalypse" does not become a run of
                // underscores, and never leading - Discord rejects neither, but both read
                // as a bug in the bot rather than a faction with punctuation in its name.
                builder.Append('_');
                underscore = true;
            }
        }

        if (underscore) builder.Length--;   // trailing, from a name ending in punctuation
        if (builder.Length == 0) return null;

        /* TRUNCATE THE FACTION PART, NOT THE SUFFIX. "_role" is what tells an operator the
           option takes a role at all, and a name cut to "brotherhood_of_steel_ro" would be
           the one piece of information worth keeping that got dropped. */
        var room = MaxLength - suffix.Length;
        if (room <= 0) return null;
        if (builder.Length > room) builder.Length = room;

        // Truncation can land on the underscore it collapsed to.
        while (builder.Length > 0 && builder[^1] == '_') builder.Length--;
        if (builder.Length == 0) return null;

        return builder.Append(suffix).ToString();
    }
}
