using Microsoft.Extensions.Logging;
using PavlovBot.Core.Economy;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Economy;

/// <summary>
/// Watching the game's ledger files and reporting every balance change.
/// </summary>
/// <remarks>
/// The game writes one plain-text file per player containing their balance. There is no
/// event, no hook, and no API - so the only way to know a payout happened is to notice the
/// number changed. This polls, diffs against a cache, and posts the deltas.
///
/// THE BUG THIS SHAPE EXISTS TO PREVENT: an unreadable or unparseable ledger must carry the
/// CACHED ENTRY FORWARD, not drop it. Dropping it makes the player look new on the next
/// successful read, so their whole balance is reported as a credit - "+5,500" for a player
/// who was paid 500. A fabricated transaction in a money log is worse than a missed one,
/// because somebody will act on it.
///
/// Modification times are used to skip unchanged files, which is what keeps the scan cheap
/// on a server with hundreds of ledgers.
/// </remarks>
public sealed class MoneyLog
{
    private sealed record CachedBalance(long Balance, DateTimeOffset ModifiedAt);

    private readonly Dictionary<string, CachedBalance> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _directory;
    private readonly FeedWebhooks _feeds;
    private readonly ILogger<MoneyLog> _logger;
    private readonly TimeProvider _time;
    private bool _primed;

    public MoneyLog(string? ledgerDirectory, FeedWebhooks feeds, ILogger<MoneyLog> logger, TimeProvider? time = null)
    {
        _directory = ledgerDirectory;
        _feeds = feeds;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_directory) && Directory.Exists(_directory);

    /// <summary>Poll once. Returns the changes found, which is what the tests assert on.</summary>
    public async Task<IReadOnlyList<(string Player, long Delta)>> TickAsync(CancellationToken ct = default)
    {
        if (!Enabled) return [];

        var changes = new List<(string Player, long Delta)>();

        foreach (var file in EnumerateLedgers())
        {
            ct.ThrowIfCancellationRequested();

            var player = Path.GetFileNameWithoutExtension(file.Name);
            var cached = _cache.GetValueOrDefault(player);

            // Unchanged since the last look: skip without opening it.
            if (cached is not null && cached.ModifiedAt == file.LastWriteTimeUtc) continue;

            if (!TryReadBalance(file.FullName, out var balance))
            {
                /* THE CRITICAL BRANCH. Carry the cached entry forward WITH ITS OLD mtime, so
                   the next tick retries and - crucially - the player is not treated as new. */
                if (cached is not null) _cache[player] = cached;
                continue;
            }

            _cache[player] = new CachedBalance(balance, file.LastWriteTimeUtc);

            if (cached is null) continue;                    // first sighting is a baseline, not a change
            if (balance == cached.Balance) continue;         // touched but unchanged

            changes.Add((player, balance - cached.Balance));
        }

        if (!_primed)
        {
            /* The first tick builds the baseline. Reporting on it would announce every
               player's entire balance as a credit the moment the bot starts. */
            _primed = true;
            _logger.LogInformation("Money log primed with {Count} ledger(s)", _cache.Count);
            return [];
        }

        if (changes.Count > 0)
            await _feeds.PostMoneyAsync(changes, _time.GetUtcNow(), ct).ConfigureAwait(false);

        return changes;
    }

    private IEnumerable<FileInfo> EnumerateLedgers()
    {
        try
        {
            return new DirectoryInfo(_directory!).EnumerateFiles("*.txt");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Could not list {Directory}: {Message}", _directory, ex.Message);
            return [];
        }
    }

    private bool TryReadBalance(string path, out long balance)
    {
        balance = 0;
        try
        {
            var text = File.ReadAllText(path).Trim();
            // The file is a bare number. Anything else is a partial write or a different
            // file that happened to land here, and either way it is not a balance.
            return long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out balance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The last known balance, without touching the disk.</summary>
    public long? Cached(string player) => _cache.TryGetValue(player, out var entry) ? entry.Balance : null;

    public int Tracked => _cache.Count;
}

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
/// <see cref="MoneyLog.TryReadBalance"/> already has to cope with half-written ones.
/// </remarks>
public sealed class LedgerFileStore(string? directory, PavlovBot.Host.Rcon.IOnlineRoster? online = null) : IBalanceStore
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

        try
        {
            /* TEMP THEN RENAME. The game reads these files, and a reader that catches one
               mid-write sees a truncated number - MoneyLog.TryReadBalance already has to
               handle exactly that. A rename is atomic within a directory, so a reader sees
               either the old value or the new one and never half of either. */
            var temporary = path + ".bot.tmp";
            File.WriteAllText(temporary, balance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
