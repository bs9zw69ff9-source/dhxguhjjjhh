using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Sync;
using PavlovBot.Host.Rcon;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Keeps the <c>ModSave</c> tree identical across every Pavlov install on the box.
/// </summary>
/// <remarks>
/// A PORT OF THE NODE BOT'S cross-install ModSave sync, which the C# bot dropped. Each install
/// has its own <c>Pavlov/Saved/Config/ModSave</c> - caps ledgers, faction roles, gamemode
/// saves, the mod ban-message file - and the servers write them independently, so a player
/// who earns caps on server 1 has a different balance on server 2 until something reconciles
/// the two. That something used to be this; without it the numbers drift apart the moment a
/// second install exists.
///
/// The decision is in <see cref="ModSaveSyncPlanner"/> (newest-wins, mtime only) and the two
/// content guards in <see cref="LedgerFile"/>; this class is the filesystem around them -
/// listing trees, reading the few files a candidate touches, and the atomic copy. It is Host,
/// not Core, for exactly that reason.
///
/// EVERY WRITE IS ATOMIC AND NOTHING IS EVER DELETED. A copy is written to a temp file and
/// renamed over the target, so the game - which reads these files - sees the old bytes or the
/// new ones and never a torn file. A missing file is copied in, never removed from the install
/// that has it.
/// </remarks>
public sealed class ModSaveSync
{
    private readonly IReadOnlyList<string> _installs;
    private readonly IOnlineRoster? _online;
    private readonly Regex _skip;
    private readonly ILogger<ModSaveSync> _logger;

    /// <summary>
    /// The RCON+ / menu-access files, matched case-insensitively anywhere in a relative path.
    /// </summary>
    /// <remarks>
    /// These are managed live by the give/remove-menu commands. Mirroring them newest-wins
    /// would let one install's stale copy wipe a player's menu access, so they are never
    /// touched by the sweep - the one carve-out the Node bot kept, extended by
    /// <c>MODSAVE_SYNC_SKIP_EXTRA</c>.
    /// </remarks>
    private static readonly string[] AlwaysSkip = ["menuaccess", "accessmanager", "rconplus", "rcon_plus"];

    public ModSaveSync(
        IReadOnlyList<string> installs,
        IOnlineRoster? online,
        bool enabled,
        ILogger<ModSaveSync> logger,
        IEnumerable<string>? extraSkip = null)
    {
        ArgumentNullException.ThrowIfNull(installs);
        _installs = installs;
        _online = online;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Enabled = enabled;

        var terms = AlwaysSkip
            .Concat((extraSkip ?? []).Select(s => s.Trim()).Where(s => s.Length > 0))
            .Select(Regex.Escape);
        _skip = new Regex(string.Join('|', terms), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>On when not disabled AND there is more than one install to reconcile.</summary>
    /// <remarks>
    /// A single install is not a failure, it is just nothing to do - so this reads false and
    /// the sweep never registers rather than running a no-op every minute.
    /// </remarks>
    public bool Enabled { get; }

    public bool CanSync => Enabled && _installs.Count > 1;

    /// <summary>
    /// One blanket newest-wins pass across every install. Returns how many files were copied.
    /// </summary>
    public Task<int> SweepAsync(CancellationToken ct = default) => Task.Run(() => Sweep(ct), ct);

    private int Sweep(CancellationToken ct)
    {
        if (!CanSync) return 0;

        var snapshots = _installs
            .Select(root => new ModSaveSnapshot(root, ListTree(PavlovInstalls.ModSavePath(root))))
            .ToList();

        var candidates = ModSaveSyncPlanner.Candidates(snapshots, _skip.IsMatch);
        if (candidates.Count == 0) return 0;

        // Group by source file so its content is read once, the online guard is applied once,
        // and then each destination copy is considered.
        var copied = 0;
        foreach (var bySource in candidates.GroupBy(c => (c.RelPath, c.FromRoot)))
        {
            ct.ThrowIfCancellationRequested();

            var (rel, fromRoot) = bySource.Key;
            var sourcePath = Path.Combine(PavlovInstalls.ModSavePath(fromRoot), ToOsPath(rel));
            if (ReadAllBytes(sourcePath) is not { } content) continue;

            var text = AsText(content);

            /* THE ONLINE GUARD. A connected player's ledger is still moving in server memory,
               so the sweep cannot trust a modified-time race for it; the join-time sync owns
               those. Keyed off the SOURCE content, exactly as the Node bot did. */
            if (LedgerFile.LooksLikeLedger(text) &&
                _online is { } roster && roster.IsTrustworthy &&
                roster.Online.Contains(Path.GetFileNameWithoutExtension(rel), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var copy in bySource)
            {
                ct.ThrowIfCancellationRequested();

                var destPath = Path.Combine(PavlovInstalls.ModSavePath(copy.ToRoot), ToOsPath(rel));

                /* THE MONEY GUARD. Never let an empty-or-zero file land on a positive balance.
                   Only reads the destination when the source is the losing shape, so the common
                   copy pays nothing for it. */
                if (LedgerFile.WouldWipeBalance(text, ReadTextOrNull(destPath)))
                {
                    _logger.LogDebug(
                        "ModSave sync kept {Rel} in {Install} - refused to overwrite a balance with {Source}",
                        rel, Path.GetFileName(copy.ToRoot), text.Trim().Length == 0 ? "an empty file" : "0");
                    continue;
                }

                if (WriteMirrored(destPath, content, copy.ModifiedUnixMs)) copied++;
            }
        }

        if (copied > 0)
            _logger.LogInformation("ModSave sync: propagated {Count} file(s) across {Installs} installs",
                copied, _installs.Count);

        return copied;
    }

    /// <summary>
    /// Push one player's caps ledger to its newest copy across every install, immediately.
    /// </summary>
    /// <remarks>
    /// THE FAST PATH FOR SERVER-HOPPERS. The blanket sweep runs on an interval and skips
    /// online players, so on its own a player who leaves server 1 and joins server 2 inside
    /// the window loads a stale balance on server 2 and can then save it back over the caps
    /// they earned - which is the exact loss the sweep's online guard defers to this. Run on
    /// join, it copies the newest ledger in before the destination reads it. Best-effort, as it
    /// was in the Node bot: if the server they just left has not yet flushed the balance to
    /// disk there is nothing newer to copy, and the next sweep after they disconnect catches it.
    ///
    /// NO ONLINE GUARD HERE, and that is the point - the sweep skips the online player so this
    /// can act for them. The money guard still applies: a fresh zero must not wipe real caps.
    /// </remarks>
    public Task SyncPlayerLedgerAsync(string playerName, CancellationToken ct = default) =>
        Task.Run(() => SyncPlayerLedger(playerName), ct);

    private void SyncPlayerLedger(string playerName)
    {
        if (!CanSync) return;

        var file = new string(playerName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        if (file.Length == 0) return;
        var rel = $"{file}.txt";

        // Where does the newest copy of this one file live?
        string? bestRoot = null;
        var bestModified = long.MinValue;
        foreach (var root in _installs)
        {
            var path = Path.Combine(PavlovInstalls.ModSavePath(root), rel);
            if (TryModifiedUnixMs(path, out var modified) && modified > bestModified)
            {
                bestModified = modified;
                bestRoot = root;
            }
        }

        if (bestRoot is null) return;   // no install has a ledger for them yet; the game makes it, not the bot

        var sourcePath = Path.Combine(PavlovInstalls.ModSavePath(bestRoot), rel);
        if (ReadAllBytes(sourcePath) is not { } content) return;
        var text = AsText(content);

        var copied = 0;
        foreach (var root in _installs)
        {
            if (string.Equals(root, bestRoot, StringComparison.Ordinal)) continue;

            var destPath = Path.Combine(PavlovInstalls.ModSavePath(root), rel);
            if (TryModifiedUnixMs(destPath, out var destModified) &&
                destModified >= bestModified - ModSaveSyncPlanner.DefaultToleranceMs)
            {
                continue;
            }

            if (LedgerFile.WouldWipeBalance(text, ReadTextOrNull(destPath))) continue;
            if (WriteMirrored(destPath, content, bestModified)) copied++;
        }

        if (copied > 0)
            _logger.LogDebug("ModSave sync: propagated {Player}'s ledger to {Count} install(s)", playerName, copied);
    }

    /// <summary>One line for the startup summary, and the diagnostics behind it.</summary>
    /// <remarks>
    /// A silent no-op is the failure mode this whole feature is prone to: one install, a
    /// MODSAVE_PATH that points outside every install, a ModSave directory the bot cannot write.
    /// Each of those is reported once at startup rather than left to be inferred from balances
    /// that never converge.
    /// </remarks>
    public IEnumerable<string> Diagnose(string? modsavePath)
    {
        if (!Enabled)
        {
            yield return "cross-install ModSave sync is OFF (MODSAVE_SYNC=off)";
            yield break;
        }

        if (_installs.Count < 2)
        {
            yield return
                $"only {_installs.Count} install found, so cross-install sync does nothing. " +
                "If you run more, set PAVLOV_BASES to list them";
            yield break;
        }

        yield return $"cross-install ModSave sync across {_installs.Count} installs: " +
                     string.Join(", ", _installs.Select(Path.GetFileName));

        if (modsavePath is { Length: > 0 } mp && !GameFiles.InsideInstall(mp, _installs))
            yield return
                $"WARNING: MODSAVE_PATH ({mp}) is not inside any install, so balance writes are not " +
                "mirrored out. Point it inside one of the installs above";

        foreach (var root in _installs)
        {
            var dir = PavlovInstalls.ModSavePath(root);
            if (Directory.Exists(dir) && !Writable(dir))
                yield return $"WARNING: ModSave for {Path.GetFileName(root)} ({dir}) is not writable - " +
                             "sync into it will fail. Check ownership and permissions";
        }
    }

    // ---- filesystem ----

    private static IReadOnlyDictionary<string, long> ListTree(string root)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return files;

        try
        {
            foreach (var abs in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!TryModifiedUnixMs(abs, out var modified)) continue;
                // Store forward-slashed so the relative key is identical across installs and
                // the planner compares like with like whatever the OS separator is.
                var rel = Path.GetRelativePath(root, abs).Replace(Path.DirectorySeparatorChar, '/');
                files[rel] = modified;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A tree we cannot list contributes nothing; it must not take the sweep down.
        }

        return files;
    }

    private static string ToOsPath(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    private static bool TryModifiedUnixMs(string path, out long modified)
    {
        modified = 0;
        try
        {
            if (!File.Exists(path)) return false;
            modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeMilliseconds();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[]? ReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadTextOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Ledger files are one integer; the tree also holds text config. UTF-8 is what the game
    // writes, and the content guards only ever inspect files that parse as a bare number, so a
    // genuinely binary save that is not valid UTF-8 simply never looks like a ledger.
    private static string AsText(byte[] content) => System.Text.Encoding.UTF8.GetString(content);

    private bool WriteMirrored(string destPath, byte[] content, long modifiedUnixMs)
    {
        try
        {
            var directory = Path.GetDirectoryName(destPath);

            /* NEVER CREATE A ModSave TREE. A missing directory means this install is not laid
               out the way discovery assumed, and building one produces a second tree beside the
               real one that the game never reads - the same rule the rest of the bot's
               game-file writes keep. */
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return false;

            // Unique per write, not just per process: the interval sweep and the join-time
            // fast path can both target one file at once, and a shared temp name would let
            // them clobber each other's half-written temp before either rename.
            var temp = $"{destPath}.modsync.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, content);
            File.Move(temp, destPath, overwrite: true);

            /* MTIME CARRIED FROM THE SOURCE, so the two copies now compare equal and the next
               sweep leaves them alone. Without it the fresh write is always "newer" and the
               file ping-pongs between installs forever. Best-effort: a filesystem that refuses
               the timestamp still gets the right bytes, and convergence just waits for the next
               real change. */
            try { File.SetLastWriteTimeUtc(destPath, DateTimeOffset.FromUnixTimeMilliseconds(modifiedUnixMs).UtcDateTime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("ModSave sync could not write {Path}: {Message}", destPath, ex.Message);
            return false;
        }
    }

    private static bool Writable(string directory)
    {
        var probe = Path.Combine(directory, $".modsync-probe.{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
