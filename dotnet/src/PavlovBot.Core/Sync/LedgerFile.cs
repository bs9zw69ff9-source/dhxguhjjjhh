namespace PavlovBot.Core.Sync;

/// <summary>
/// Recognising a caps ledger by its contents, and the two guards that reading protects.
/// </summary>
/// <remarks>
/// A player's balance file is either a single non-negative integer, or the keyed shape with a
/// <c>Money</c> line - see <see cref="ModSaveFile"/>. Recognising one by CONTENT rather than by
/// its folder is deliberate: the sub-directory a mod files balances under is the mod's choice
/// and not something to hard-code, but the shape of the file is the same on every server.
///
/// The two guards below are why the sync reads content at all, and both protect the same
/// thing - money that has already been earned:
///
///   THE ONLINE GUARD keys off <see cref="LooksLikeLedger"/>. A connected player's balance is
///   still moving in server memory, so which install's copy has the newest modified time no
///   longer tells you which is actually current - an unrelated autosave can stamp a stale
///   number with a fresh time. The blanket sweep therefore skips a ledger whose player is
///   online, and the join-time sync handles those instead.
///
///   THE MONEY GUARD is <see cref="WouldWipeBalance"/>. A player hopping to a server that
///   just made them a fresh zero-cap file must not have that zero propagate over the caps they
///   earned elsewhere. Copying empty-or-zero over a positive balance is refused outright.
/// </remarks>
public static class LedgerFile
{
    /// <summary>True when the content holds a non-negative balance, in either shape.</summary>
    public static bool LooksLikeLedger(string? content) => Balance(content) is not null;

    /// <summary>The balance, or null when the content holds no non-negative one.</summary>
    public static long? Balance(string? content) =>
        ModSaveFile.Parse(content)?.Money is { } value && value >= 0 ? value : null;

    /// <summary>
    /// Whether copying <paramref name="source"/> over <paramref name="destination"/> would
    /// replace a positive balance with nothing.
    /// </summary>
    /// <remarks>
    /// True only in the exact losing shape: the source is empty or holds a balance of zero, and
    /// the destination holds a positive one. Anything else - a source with a real number, a destination that is
    /// not a balance at all - is a normal copy and returns false, because this guard exists to
    /// stop money loss and not to second-guess every write.
    /// </remarks>
    public static bool WouldWipeBalance(string? source, string? destination)
    {
        var s = source?.Trim() ?? "";
        if (s.Length != 0 && Balance(source) is not 0) return false;   // the source carries something real

        return Balance(destination) is > 0;             // and the destination is caps we would lose
    }
}
