using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Time;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <param name="Unban">The raw "Unban:" line, e.g. "3d 4h" or "Permanent".</param>
public sealed record BanFileEntry(string Name, string Reason, string Unban);

/// <summary>Why a ban-file lookup found nothing, which is not one answer but four.</summary>
public enum BanFileStatus
{
    /// <summary>No path configured, so the bot neither reads nor writes any ban file.</summary>
    Disabled,

    /// <summary>Configured, but nothing is there. Usually the wrong path.</summary>
    Missing,

    /// <summary>There, and could not be read. Permissions, almost always.</summary>
    Unreadable,

    /// <summary>Read successfully. Only here does "not listed" mean "not banned".</summary>
    Read,
}

/// <summary>
/// What the server's own ban file says about one player.
/// </summary>
/// <remarks>
/// The STATUS matters as much as the entry. "Not in the file" and "the file could not be
/// read" are the same absence of an entry and completely different answers to give a
/// moderator, and reporting the second as the first is how a wrong path stays invisible.
/// </remarks>
public sealed record BanFileLookup(BanFileStatus Status, string? Path, BanFileEntry? Entry)
{
    public bool Listed => Entry is not null;

    /// <summary>True when the file was actually read, so "not listed" can be trusted.</summary>
    public bool Conclusive => Status == BanFileStatus.Read;
}

/// <summary>
/// The game's own ban-list file: the message a banned player sees, and a way in.
/// </summary>
/// <remarks>
/// Two independent jobs sharing one file:
///
///   EXPORT tells the PLAYER why they are banned and for how long. Without it they see a
///   bare rejection and open a ticket asking what happened, every single time.
///
///   IMPORT picks up bans made from the in-game admin menu, which the bot never saw. Those
///   are real bans issued by real staff, and a bot that does not know about them shows an
///   incomplete ban list and will happily "unban" somebody it never banned.
///
/// THE TIME LEFT IS WRITTEN AS A SPAN, NOT A DATE. A player reading "3d 4h" knows what it
/// means; a player reading a timestamp has to work out the timezone, and gets it wrong.
/// That means the file goes stale as the clock moves, which is why it is rewritten on every
/// sync rather than only on change.
/// </remarks>
public sealed class ServerBanFile(
    string? path, SerializedStore store, ILogger<ServerBanFile> logger, TimeProvider? time = null,
    Func<string, string?>? resolveName = null,
    Storage.GameFileGuard? guard = null) : IBanFileExport
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Storage.GameFileGuard _guard = guard ?? Storage.GameFileGuard.None;

    /// <summary>Said once. This runs on a timer, and a wrong path is wrong every tick.</summary>
    private bool _pathWarned;

    public bool Enabled => !string.IsNullOrWhiteSpace(path);

    /// <summary>The file this syncs, so a command can name it rather than describe it.</summary>
    public string? Path => path;

    /// <summary>
    /// What the server's own ban file says about one player.
    /// </summary>
    /// <remarks>
    /// THE QUESTION /checkban AND /unban COULD NOT ANSWER. The server reads this file itself,
    /// so a name in it is refused whatever the bot's store says. Both commands used to answer
    /// from the store alone and then print a paragraph of guesswork about the file - which is
    /// how "banlist and checkban both return nothing but he is still in the blacklist" happens
    /// and stays unexplained. Reading it costs one file read on a command nobody spams.
    ///
    /// BOTH IDENTITIES ARE COMPARED, the same as the importer: an in-game ban is filed under
    /// the EOS id while staff type the display name, and matching only one of them is how a
    /// listed player looks absent.
    /// </remarks>
    public async Task<BanFileLookup> FindAsync(string player, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!Enabled) return new BanFileLookup(BanFileStatus.Disabled, path, null);
        if (!File.Exists(path)) return new BanFileLookup(BanFileStatus.Missing, path, null);

        IReadOnlyList<BanFileEntry> parsed;
        try
        {
            parsed = Parse(await File.ReadAllTextAsync(path!, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the server ban file {Path}", path);
            return new BanFileLookup(BanFileStatus.Unreadable, path, null);
        }

        var match = parsed.FirstOrDefault(e =>
            BanRules.SamePlayer(e.Name, player) ||
            BanRules.SamePlayer(ResolveName(e.Name, resolveName), player));

        return new BanFileLookup(BanFileStatus.Read, path, match);
    }

    /// <summary>
    /// How long a deliberate unban blocks the importer from re-creating that ban.
    /// </summary>
    /// <remarks>
    /// Long enough to outlast an export failure somebody would notice and fix, short enough
    /// that re-banning the same player next month is not silently ignored. Seven days.
    ///
    /// It does NOT block a fresh ban. Bans are written to the store directly, and the importer
    /// only ever ADDS names the store does not already know.
    /// </remarks>
    public static TimeSpan TombstoneLife { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Rewrite the file from the bot's ban store, which is the source of truth.
    /// </summary>
    public async Task<int> ExportAsync(CancellationToken ct = default)
    {
        if (!Enabled) return 0;

        var now = _time.GetUtcNow();
        var active = BanRules.Active(store.Read<List<BanRecord>>(Datasets.TempBans, []), now);

        var body = new StringBuilder();
        foreach (var ban in active.OrderBy(b => b.PlayerId, StringComparer.OrdinalIgnoreCase))
        {
            body.Append(ban.PlayerId).Append('\n');
            body.Append("Reason: ").Append(Flatten(ban.Reason ?? "No reason given")).Append('\n');
            body.Append("Unban: ")
                .Append(ban.Permanent ? "Permanent" : EasternTime.TimeLeft(ban.Expires!.Value, _time))
                .Append("\n\n");
        }

        /* NOT CreateDirectory. This lives in a directory the game owns and already made, so
           a missing one means the path is wrong - and building it produces a second ModSave
           tree beside the real one that the game never reads. */
        if (_guard.Problem(path) is { } problem)
        {
            if (!_pathWarned)
            {
                _pathWarned = true;
                logger.LogError("Not writing the server ban file: {Problem}", problem);
            }
            return 0;
        }

        try
        {
            var temp = $"{path}.tmp";
            await File.WriteAllTextAsync(temp, body.ToString(), ct).ConfigureAwait(false);
            File.Move(temp, path!, overwrite: true);
            return active.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the server ban file");
            return 0;
        }
    }

    /// <summary>The username for an EOS id, or the id unchanged when it is not known.</summary>
    internal static string ResolveName(string entryName, Func<string, string?>? resolve)
    {
        if (resolve is null) return entryName;

        var resolved = resolve(entryName);
        return string.IsNullOrWhiteSpace(resolved) ? entryName : resolved;
    }

    /// <summary>
    /// When either identity for one player was deliberately unbanned, or null for neither.
    /// </summary>
    /// <remarks>
    /// A player has two names in this system - the EOS id the game writes and the username
    /// staff read - and an unban is recorded under whichever one was typed. Checking a single
    /// identity leaves the other half of the players unprotected, so both are looked up.
    ///
    /// The EARLIER of the two when both are present. A stale tombstone under one identity must
    /// not extend the protection a fresher one under the other would already have expired.
    /// </remarks>
    internal static DateTimeOffset? LiftedAt(
        IReadOnlyDictionary<string, DateTimeOffset> tombstones, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(tombstones);

        DateTimeOffset? earliest = null;

        foreach (var key in new[] { id, name })
        {
            if (key is null || !tombstones.TryGetValue(key, out var at)) continue;
            if (earliest is null || at < earliest) earliest = at;
        }

        return earliest;
    }

    /// <summary>Whether an <c>Unban:</c> value says the ban has no time left.</summary>
    /// <remarks>
    /// Covers both spellings the writer can produce: "expired" for a ban already past its
    /// time, and a zero quantity ("0m", "0", "00h") for one whose remaining minutes truncated
    /// to nothing. Neither is corrupt input and neither means permanent, which is what the
    /// importer used to make of them.
    /// </remarks>
    internal static bool DenotesElapsed(string? unban)
    {
        var text = unban?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Equals("expired", StringComparison.OrdinalIgnoreCase)) return true;

        var digits = text.TrimEnd('s', 'm', 'h', 'd', 'S', 'M', 'H', 'D', ' ');
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) && digits.All(c => c == '0');
    }

    /// <summary>A reason must not contain a newline - the format is line-oriented, and one
    /// would turn the rest of the reason into a bogus field.</summary>
    private static string Flatten(string text) =>
        string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>
    /// Parse the file. Exposed for tests, and because the format is the fragile part.
    /// </summary>
    /// <remarks>
    /// Blocks separated by a blank line: the first line is the NAME, and the rest are
    /// <c>Field: value</c>. A block whose first line looks like a field is skipped rather
    /// than treated as a player called "Reason" - which is what a trailing blank line or a
    /// hand-edit produces.
    /// </remarks>
    /// <summary>The reason recorded for a ban this bot did not issue.</summary>
    internal const string DefaultReason = "Imported from the server's ban list";

    /// <summary>A "Reason:"/"Unban:"/"Appeal:" line from the old ModSave block format.</summary>
    private static bool IsMetadata(string line) =>
        line.StartsWith("Reason:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Unban:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Appeal:", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<BanFileEntry> Parse(string contents)
    {
        var entries = new List<BanFileEntry>();

        foreach (var block in (contents ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("//", StringComparison.Ordinal))
                .ToList();
            if (lines.Count == 0) continue;

            /* BOTH SHAPES, and getting this wrong loses people silently. A plain
               one-per-line list has no blank lines in it, so the WHOLE FILE arrives here as
               a single block: read as one Reason/Unban record it would import the first name
               and discard every other line as unrecognised metadata. Everybody but the first
               player would quietly stop being banned.

               A block is metadata-shaped only if something after its first line actually
               says Reason, Unban or Appeal. Otherwise every line in it is its own entry. */
            if (!lines.Skip(1).Any(IsMetadata))
            {
                foreach (var line in lines.Where(l => !IsMetadata(l)))
                    entries.Add(new BanFileEntry(line, DefaultReason, "Permanent"));
                continue;
            }

            var name = lines[0];
            if (IsMetadata(name)) continue;

            var reason = DefaultReason;
            var unban = "Permanent";

            foreach (var line in lines.Skip(1))
            {
                var split = line.Split(':', 2);
                if (split.Length != 2) continue;

                var value = split[1].Trim();
                if (value.Length == 0) continue;

                if (split[0].Trim().Equals("Reason", StringComparison.OrdinalIgnoreCase)) reason = value;
                else if (split[0].Trim().Equals("Unban", StringComparison.OrdinalIgnoreCase)) unban = value;
            }

            entries.Add(new BanFileEntry(name, reason, unban));
        }
        return entries;
    }

    /// <summary>
    /// Pull in bans the bot never issued.
    /// </summary>
    /// <remarks>
    /// Idempotent: a player already in the store is skipped, so this can run every few
    /// minutes forever. Importing is deliberately one-way - the file never removes a ban
    /// from the store, because the store is the source of truth and a truncated or
    /// mid-write file would otherwise lift every ban on the server.
    /// </remarks>
    public async Task<int> ImportAsync(CancellationToken ct = default)
    {
        if (!Enabled || !File.Exists(path)) return 0;

        IReadOnlyList<BanFileEntry> parsed;
        try
        {
            parsed = Parse(await File.ReadAllTextAsync(path!, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        if (parsed.Count == 0) return 0;

        var now = _time.GetUtcNow();
        var added = 0;

        /* NAMES DELIBERATELY UNBANNED ARE NOT RE-IMPORTED. Read once, outside the mutator.

           THE BUG THIS FIXES. An unban removes the store record, but the game's ban FILE still
           listed them until the next export - so this import, running every five minutes, saw
           a name it did not recognise and re-created the ban that had just been lifted. The
           export then wrote it straight back and the sweep enforced it. From the outside, bans
           came back on their own a few minutes after /unban reported success.

           The lift rewrites the file now, so in the normal case there is nothing here to skip.
           This is the half that holds when the export failed, when BLACKLIST_PATH is
           wrong, or when somebody edits the file by hand. */
        var lifted = store.ReadMap(Datasets.UnbanTombstones,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

        await store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            var known = bans.Select(b => b.PlayerId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in parsed)
            {
                /* RESOLVED BEFORE ANYTHING IS COMPARED, because the id and the name are one
                   identity and every check below has to see both of them.

                   The de-duplication used to compare the FILE's key against a set of STORED
                   keys. For an in-game ban those are different strings for the same person -
                   the file says the EOS id, the store says the name it was resolved to - so
                   the check never matched and the same ban was re-created on every sync,
                   every five minutes, forever. An /unban then removed one record out of the
                   pile and the player stayed banned, which is what "still being banned from
                   the unreadable number" was. */
                var player = ResolveName(entry.Name, resolveName);
                var resolved = !string.Equals(player, entry.Name, StringComparison.Ordinal);

                // Either identity being on record means this ban is already known. Both go in,
                // so the store's older id-keyed records are recognised as well as new ones.
                if (known.Contains(player) || known.Contains(entry.Name)) continue;
                known.Add(player);
                known.Add(entry.Name);

                /* THE TOMBSTONE IS KEYED ON WHAT STAFF TYPED, which is the name in front of
                   them - the ban list shows it and /unban autocompletes it. Looking it up by
                   the file's id alone meant a lift was invisible here and the ban came
                   straight back. Both identities are checked, so it does not matter which one
                   they used. */
                if (LiftedAt(lifted, entry.Name, player) is { } when && now - when < TombstoneLife)
                {
                    logger.LogInformation(
                        "Not re-importing the in-game ban for {Name} - it was deliberately lifted {Ago} ago. " +
                        "To ban them again, use the ban commands rather than the file",
                        player, now - when);
                    continue;
                }

                /* NO TIME LEFT MEANS EXPIRED, NOT FOREVER. This is the half that turned temp
                   bans permanent. "0m" and "expired" are both unparseable to ParseBanSpan,
                   which rejects a zero quantity, and unparseable fell through to the
                   permanent branch below - so a temp ban whose file entry had run down was
                   re-imported as a permanent one, with the elapsed value printed in the
                   reason as if it were corrupt input.

                   Skipped rather than imported: the ban has served its time, and the sweep
                   that would have lifted it cannot lift what it has already re-added. */
                if (DenotesElapsed(entry.Unban))
                {
                    logger.LogInformation(
                        "Skipping in-game ban for {Name} - its unban value \"{Unban}\" has already elapsed",
                        entry.Name, entry.Unban);
                    continue;
                }

                /* An "Unban" value the parser cannot read becomes a PERMANENT ban with the
                   unreadable value recorded in the reason. Guessing a duration would either
                   free somebody early or hold them longer than staff intended, and the note
                   makes it fixable by hand. */
                var span = EasternTime.ParseBanSpan(entry.Unban);
                var permanent = span is null;
                var reason = entry.Reason;

                if (permanent && !entry.Unban.Equals("Permanent", StringComparison.OrdinalIgnoreCase))
                    reason = $"{reason} [unreadable unban value: {entry.Unban}]";

                bans.Add(new BanRecord
                {
                    /* THE GAME WRITES AN EOS ID HERE, NOT A NAME. Bans made in-game through
                       the RCON+ menu are written by Pavlov, and the block header it writes is
                       the UniqueID. Taking it verbatim filed those bans under a 32-character
                       id, so the ban list showed ids for in-game bans and usernames for the
                       bot's own - the same player looking like two different entries.

                       Resolved through the account registry, which already maps id to name.
                       An id nobody has seen keeps the id: a ban you cannot name is still a
                       ban, and dropping it would be far worse than showing it awkwardly. */
                    PlayerId = player,

                    /* THE ID IS KEPT for the evasion flags and evidence, which key on the
                       account id even though Shack's Ban/Kick/Unban target the NAME. It is the
                       id straight from the file, set only when resolution actually happened: an
                       unresolved entry could be either an id or a name, and guessing is what the
                       existing lookup fallback is for. */
                    UniqueId = resolved ? entry.Name : null,
                    Reason = reason,
                    Moderator = "in-game",
                    At = now,
                    Expires = span is { } s ? now + s : null,
                    Permanent = permanent,
                    DurationLabel = permanent ? "Permanent" : entry.Unban,
                });
                added++;
            }

            return added > 0 ? bans : null;   // veto when there is nothing to write
        }, ct).ConfigureAwait(false);

        if (added > 0) logger.LogInformation("Imported {Count} ban(s) from the in-game ban list", added);
        return added;
    }

    /// <summary>
    /// Import THEN export, in that order.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing: exporting first would rewrite the file from the store and
    /// destroy the in-game entries before they were read, so a ban issued from the admin
    /// menu would vanish and never be recorded.
    /// </remarks>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        await ImportAsync(ct).ConfigureAwait(false);
        await ExportAsync(ct).ConfigureAwait(false);
    }
}
