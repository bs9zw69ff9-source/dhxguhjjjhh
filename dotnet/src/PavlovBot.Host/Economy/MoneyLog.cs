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

/// <summary>Reads and writes balances through the same plain-text ledgers.</summary>
public sealed class LedgerFileStore(string? directory) : IBalanceStore
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

    public bool Write(string playerId, long balance)
    {
        var path = PathFor(playerId);
        if (path is null) return false;

        /* NOT CreateDirectory. The ledger directory is the game's, and creating it would
           produce a second one the game never reads while the bot reported success. */
        if (!PavlovBot.Host.Storage.GameFiles.Prepare(path, out _)) return false;

        try
        {
            var temp = $"{path}.tmp";
            File.WriteAllText(temp, balance.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
