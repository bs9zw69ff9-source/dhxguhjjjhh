using PavlovBot.Core.Economy;

namespace PavlovBot.Host.Economy;

/// <summary>
/// Balances, read from and written to the game's own <c>modsave/&lt;player&gt;.txt</c> files.
/// </summary>
/// <remarks>
/// WRITING WAS TURNED OFF ENTIRELY AND IS NOW BACK UNDER ONE RULE: the bot writes a player's
/// ledger only while that player is OFFLINE.
///
/// The reason it was off is not theoretical. The game holds balances in memory and writes
/// these files itself, so a number the bot wrote while somebody was playing got clobbered on
/// the next in-game save - and a read-modify-write landing across an in-game purchase lost
/// money outright. That is what "balances out of step with what the servers held" meant.
///
/// An offline player has no in-memory balance to overwrite, which removes the race rather
/// than narrowing it. The gate lives HERE, at the single write, rather than at each caller,
/// so payroll, <c>/givecaps</c> and <c>/adjustcaps</c> cannot each forget it differently.
///
/// FOUR REFUSALS, ALL FAIL-CLOSED:
///   - the roster cannot be trusted (RCON down): we do not know who is online, so we do not
///     write. "Cannot tell" is not "they are offline".
///   - the player IS online.
///   - the player has NO ledger: the bot never CREATES one. A file the game did not make is
///     a player the game has never banked, and inventing one is how a phantom balance
///     appears for a mistyped name.
///   - the directory does not exist: a wrong MODSAVE_PATH must not silently start a parallel
///     set of ledgers nobody reads.
///
/// The write itself is temp-file-plus-rename, because the game READS these files too and
/// <see cref="Read"/> already has to cope with half-written ones.
/// </remarks>
public sealed class LedgerFileStore(
    string? directory,
    PavlovBot.Host.Rcon.IOnlineRoster? online = null,
    PavlovBot.Host.Storage.GameFileGuard? guard = null) : IBalanceStore
{
    private string? PathFor(string playerId)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        /* Filenames must match what the GAME writes, and names may contain spaces
           ("Butter Life.txt"). Only path separators and control characters are stripped -
           removing spaces would send wages to a phantom "ButterLife.txt" nobody reads. */
        var safe = new string(playerId.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        return safe.Length == 0 ? null : Path.Combine(directory, $"{safe}.txt");
    }

    public long? Read(string playerId)
    {
        var path = PathFor(playerId);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            return long.TryParse(File.ReadAllText(path).Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Null means UNKNOWN, and the ledger treats it as zero at the point of use.
            // Returning 0 here would let a transient read error wipe somebody's balance.
            return null;
        }
    }

    /// <summary>Whether this player's ledger may be written right now. See the class remarks.</summary>
    public bool CanWrite(string playerId) => Refusal(playerId) is null;

    /// <summary>
    /// Why a write would be refused, in words a command can show somebody.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Write"/> so a command can explain the refusal BEFORE
    /// attempting it. "Not applied" with no reason is what sent people to read the source.
    /// </remarks>
    public string? Refusal(string playerId)
    {
        if (PathFor(playerId) is not { } path) return "there is no ledger directory configured";
        if (!Directory.Exists(directory)) return $"the ledger directory `{directory}` does not exist";

        /* No roster at all means this is a context with no game servers - tests, tooling.
           Refuse rather than assume, for the same reason as the untrusted case below. */
        if (online is null) return "the bot cannot see who is online, so it will not write a ledger";

        if (!online.IsTrustworthy)
            return "no server has a fresh player list, so the bot cannot tell whether they are online";

        if (online.Online.Contains(playerId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return "they are currently in game. The server holds their balance in memory and would " +
                   "overwrite anything written now - this applies once they log off";
        }

        if (!File.Exists(path))
            return "they have no ledger file yet. The bot never creates one - the game does that when they first bank";

        return null;
    }

    /// <summary>
    /// Persist a balance, if and only if the player is offline.
    /// </summary>
    /// <returns>False when refused or when the write failed. The balance is unchanged either way.</returns>
    public bool Write(string playerId, long balance)
    {
        if (Refusal(playerId) is not null) return false;
        if (PathFor(playerId) is not { } path) return false;

        /* THE ONLY WRITE HERE THAT DID NOT GO THROUGH THE GUARD, and the highest-stakes one
           on a shared server: this is somebody's money. Two bots against one install both
           rewriting modsave/<player>.txt lose updates - the per-player lock serialises
           writes INSIDE a process, and there is no lock between processes. */
        if ((guard ?? PavlovBot.Host.Storage.GameFileGuard.None).Problem(path) is not null) return false;

        try
        {
            /* TEMP THEN RENAME. The game reads these files, and a reader that catches one
               mid-write sees a truncated number, which Read already has to handle. A
               rename is atomic within a directory, so a reader sees either the old value or
               the new one and never half of either. */
            PavlovBot.Host.Storage.AtomicFile.Write(path, balance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
