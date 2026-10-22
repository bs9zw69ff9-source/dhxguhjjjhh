using PavlovBot.Core.Economy;
using PavlovBot.Core.Licensing;
using PavlovBot.Core.Sync;

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
///
/// A WRITE IS AN EDIT, NOT A REPLACEMENT. The file can hold more than money - the police
/// server's keeps licences beside it (see <see cref="ModSaveFile"/>) - so every write reads
/// the file, changes one value, and puts the rest back untouched. A file in neither shape is
/// refused rather than overwritten: that is the fifth refusal, and the one that used to be a
/// bare number written over somebody's licences.
///
/// THE EDITS ARE SERIALISED PER PLAYER, here, because two different callers now edit one
/// file. <see cref="Ledger"/> serialises money against money; it cannot see a licence being
/// revoked, and a revocation landing between a payout's read and its write would come back.
/// </remarks>
public sealed class LedgerFileStore(
    string? directory,
    PavlovBot.Host.Rcon.IOnlineRoster? online = null,
    PavlovBot.Host.Storage.GameFileGuard? guard = null) : IBalanceStore
{
    /// <summary>Striped, for the same reason <see cref="Ledger"/>'s are: nothing to reclaim.</summary>
    private readonly Lock[] _stripes = [.. Enumerable.Range(0, 64).Select(_ => new Lock())];

    private Lock StripeFor(string playerId) =>
        _stripes[(StringComparer.OrdinalIgnoreCase.GetHashCode(playerId.Trim()) & int.MaxValue) % _stripes.Length];

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
            return ModSaveFile.Parse(File.ReadAllText(path))?.Money;
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
        if (Gate(playerId) is { } refused) return refused;

        return ReadSave(PathFor(playerId)!)?.WithMoney(0) is null
            ? "their save file is not in a shape the bot recognises, so it will not rewrite it"
            : null;
    }

    /// <summary>
    /// Every refusal that is about WHEN and WHERE rather than what the file holds.
    /// </summary>
    /// <remarks>
    /// Shared by money and licences: the game rewrites the whole file from memory, so a
    /// licence revoked while the player is connected comes back on their next save exactly
    /// as a payout would vanish.
    /// </remarks>
    private string? Gate(string playerId)
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
            return "they are currently in game. The server holds their save in memory and would " +
                   "overwrite anything written now - try again once they log off";
        }

        if (!File.Exists(path))
            return "they have no ledger file yet. The bot never creates one - the game does that when they first bank";

        return null;
    }

    /// <summary>
    /// Persist a balance, if and only if the player is offline.
    /// </summary>
    /// <returns>False when refused or when the write failed. The balance is unchanged either way.</returns>
    public bool Write(string playerId, long balance) =>
        Edit(playerId, save => save.WithMoney(balance)) is EditOutcome.Written;

    /// <summary>
    /// Revoke licences, if and only if the player is offline.
    /// </summary>
    /// <returns>
    /// What happened, and which of the licences asked about they held beforehand. Nothing is
    /// written unless at least one of them was held.
    /// </returns>
    public LicenseRemoval RemoveLicenses(string playerId, IReadOnlyCollection<License> licenses)
    {
        ArgumentNullException.ThrowIfNull(licenses);

        if (Gate(playerId) is { } refused) return new(LicenseOutcome.Refused, [], [], refused);

        IReadOnlyList<License> held = [];
        IReadOnlyList<License> known = [];
        var outcome = Edit(playerId, save =>
        {
            known = [.. licenses.Where(l => save.Get(l.Key) is not null)];
            held = [.. known.Where(l => save.IsTrue(l.Key))];
            if (held.Count == 0) return null;

            var edited = save;
            foreach (var license in held) edited = edited.With(license.Key, "false")!;
            return edited;
        });

        return outcome switch
        {
            EditOutcome.Written => new(LicenseOutcome.Removed, held, known, null),
            EditOutcome.Unchanged when known.Count == 0 =>
                new(LicenseOutcome.NotInFile, [], [], "their save file has no such license in it"),
            EditOutcome.Unchanged => new(LicenseOutcome.NoneHeld, [], known, null),
            EditOutcome.Unreadable => new(LicenseOutcome.Refused, [], [],
                "their save file is not in a shape the bot recognises, so it will not rewrite it"),
            _ => new(LicenseOutcome.Refused, [], [],
                Gate(playerId)
                ?? (PathFor(playerId) is { } path ? (guard ?? PavlovBot.Host.Storage.GameFileGuard.None).Problem(path) : null)
                ?? "the save file did not accept the write"),
        };
    }

    private enum EditOutcome { Refused, Unreadable, Unchanged, Written, Failed }

    /// <summary>
    /// Read, change, write back - one player at a time, and only through every refusal.
    /// </summary>
    /// <param name="change">The edited file, or null for "nothing to write".</param>
    private EditOutcome Edit(string playerId, Func<ModSaveFile, ModSaveFile?> change)
    {
        if (Gate(playerId) is not null) return EditOutcome.Refused;
        if (PathFor(playerId) is not { } path) return EditOutcome.Refused;

        /* THE ONLY WRITE HERE THAT DID NOT GO THROUGH THE GUARD, and the highest-stakes one
           on a shared server: this is somebody's money. Two bots against one install both
           rewriting modsave/<player>.txt lose updates - the per-player lock serialises
           writes INSIDE a process, and there is no lock between processes. */
        if ((guard ?? PavlovBot.Host.Storage.GameFileGuard.None).Problem(path) is not null) return EditOutcome.Refused;

        lock (StripeFor(playerId))
        {
            if (ReadSave(path) is not { } save) return EditOutcome.Unreadable;
            if (change(save) is not { } edited) return EditOutcome.Unchanged;

            try
            {
                /* TEMP THEN RENAME. The game reads these files, and a reader that catches one
                   mid-write sees a truncated file, which Read already has to handle. A
                   rename is atomic within a directory, so a reader sees either the old
                   contents or the new ones and never half of either. */
                PavlovBot.Host.Storage.AtomicFile.Write(path, edited.ToString());
                return EditOutcome.Written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return EditOutcome.Failed;
            }
        }
    }

    private static ModSaveFile? ReadSave(string path)
    {
        try
        {
            return File.Exists(path) ? ModSaveFile.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public enum LicenseOutcome
{
    /// <summary>At least one licence was held and is now revoked.</summary>
    Removed,

    /// <summary>The file has the licences asked about, and none of them was held.</summary>
    NoneHeld,

    /// <summary>The file has none of the licences asked about - a server without them.</summary>
    NotInFile,

    /// <summary>Not written; <see cref="LicenseRemoval.Reason"/> says why.</summary>
    Refused,
}

/// <param name="Removed">The licences that were held and are now revoked.</param>
/// <param name="Known">The licences asked about that the file has at all.</param>
/// <param name="Reason">Why nothing was written, in words a command can show.</param>
public sealed record LicenseRemoval(
    LicenseOutcome Outcome, IReadOnlyList<License> Removed, IReadOnlyList<License> Known, string? Reason);
