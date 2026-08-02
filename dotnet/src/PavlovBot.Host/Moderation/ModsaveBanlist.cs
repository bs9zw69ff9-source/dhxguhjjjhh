using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Time;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <param name="Unban">The raw "Unban:" line, e.g. "3d 4h" or "Permanent".</param>
public sealed record ModsaveEntry(string Name, string Reason, string Unban);

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
public sealed class ModsaveBanlist(
    string? path, SerializedStore store, ILogger<ModsaveBanlist> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Said once. This runs on a timer, and a wrong path is wrong every tick.</summary>
    private bool _pathWarned;

    public bool Enabled => !string.IsNullOrWhiteSpace(path);

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
        if (!Storage.GameFiles.Prepare(path!, out var problem))
        {
            if (!_pathWarned)
            {
                _pathWarned = true;
                logger.LogError("Not writing the ModSave ban list: {Problem}", problem);
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
            logger.LogWarning("Could not write the modsave ban list: {Message}", ex.Message);
            return 0;
        }
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
    public static IReadOnlyList<ModsaveEntry> Parse(string contents)
    {
        var entries = new List<ModsaveEntry>();

        foreach (var block in (contents ?? "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;

            var name = lines[0];
            if (name.StartsWith("Reason:", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Unban:", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Appeal:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var reason = "Imported from the in-game ban list";
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

            entries.Add(new ModsaveEntry(name, reason, unban));
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

        IReadOnlyList<ModsaveEntry> parsed;
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

        await store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            var known = bans.Select(b => b.PlayerId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in parsed)
            {
                if (!known.Add(entry.Name)) continue;

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
                    PlayerId = entry.Name,
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
