using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Factions;

/// <summary>
/// A whitelist snapshot, in the Node bot's <c>faction_backup.json</c> shape.
/// </summary>
/// <param name="SavedAt">Unix milliseconds. 0 when nothing has been saved.</param>
/// <param name="Files">Roster file name to its names, one per line in the file.</param>
/// <remarks>
/// THE NODE SHAPE ON PURPOSE: <c>{ savedAt, files: { "x.txt": [...] } }</c>. A live server's
/// dataset may still hold a snapshot the Node bot wrote, and that is exactly the one worth
/// being able to load. The earlier C# shape (<c>At</c>, <c>Ranks</c>, <c>Config</c>) reads as
/// an empty snapshot, which is what it always was.
/// </remarks>
public sealed record FactionSnapshot(
    [property: JsonPropertyName("savedAt")] long SavedAt,
    [property: JsonPropertyName("files")] Dictionary<string, List<string>>? Files)
{
    public int Names => Files?.Values.Sum(v => v.Count) ?? 0;

    public DateTimeOffset At => DateTimeOffset.FromUnixTimeMilliseconds(SavedAt);
}

/// <param name="Ok">False when nothing was saved or restored.</param>
/// <param name="Message">What happened, fit to show an owner.</param>
public sealed record WhitelistBackupResult(bool Ok, string Message);

/// <summary>
/// Save and restore every faction roster file.
/// </summary>
/// <remarks>
/// A PORT GAP. The Node bot snapshotted the roster FILES; the C# port snapshotted two datasets
/// (<c>faction_ranks</c>, <c>faction_config</c>) that nothing in it writes any more, so "save"
/// stored two empty maps and "load" restored them - both reported success and neither touched a
/// single whitelist.
///
/// AN EMPTY SAVE NEVER REPLACES A FULL ONE. The moment somebody most needs the snapshot is right
/// after the rosters were wiped, and that is also when saving would capture nothing. Clicking
/// save then - or the daily auto-save running then - would destroy the only copy.
/// </remarks>
public sealed class WhitelistBackup(SerializedStore store, RosterService rosters, ILogger<WhitelistBackup> logger)
{
    /// <summary>How old the snapshot may get before the daily auto-save replaces it.</summary>
    public static readonly TimeSpan AutoSaveAge = TimeSpan.FromHours(23);

    public FactionSnapshot? Current()
    {
        var snapshot = store.Read<FactionSnapshot?>(Datasets.FactionBackup, null);
        return snapshot is { SavedAt: > 0, Files.Count: > 0 } ? snapshot : null;
    }

    public async Task<WhitelistBackupResult> SaveAsync(CancellationToken ct = default)
    {
        if (!rosters.Enabled)
            return new(false, "The roster folder (`FACTION_ROLES_PATH`) is not set or does not exist, so there is nothing to save.");

        var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        foreach (var file in rosters.ListRosterFiles())
        {
            if (rosters.Read(file) is { } lines) files[file] = [.. lines];
            else unreadable.Add(file);
        }

        /* A PARTIAL READ IS NOT SAVED. A snapshot missing the files that could not be read would
           restore them as absent - which is how a backup deletes a faction. */
        if (unreadable.Count > 0)
            return new(false, $"Not saved: {unreadable.Count} roster file(s) could not be read ({string.Join(", ", unreadable)}). The existing snapshot is untouched.");

        var snapshot = new FactionSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), files);
        if (snapshot.Names == 0 && Current() is { Names: > 0 } existing)
        {
            return new(false,
                $"Not saved: every roster is empty, and saving would replace the snapshot from " +
                $"{existing.At:yyyy-MM-dd HH:mm} UTC holding **{existing.Names}** name(s). Load it first if the rosters were wiped.");
        }

        await store.WriteAsync(Datasets.FactionBackup, snapshot, ct).ConfigureAwait(false);
        logger.LogInformation("Whitelist snapshot saved: {Files} file(s), {Names} name(s)", files.Count, snapshot.Names);
        return new(true, $"Snapshot saved: **{files.Count}** roster file(s), **{snapshot.Names}** name(s). Loading it overwrites the current rosters.");
    }

    public async Task<WhitelistBackupResult> LoadAsync(CancellationToken ct = default)
    {
        if (Current() is not { } snapshot)
            return new(false, "There is no snapshot to restore. Save one first.");
        if (!rosters.Enabled)
            return new(false, "The roster folder (`FACTION_ROLES_PATH`) is not set or does not exist, so nothing can be restored into it.");

        var restored = 0;
        var failed = new List<string>();
        foreach (var (file, lines) in snapshot.Files!)
        {
            /* The names come from a JSON file on disk. One that is not a plain .txt name - a
               path, a traversal - is refused rather than written wherever it points. */
            if (!IsRosterFileName(file))
            {
                failed.Add($"{file} (not a roster file name)");
                continue;
            }

            // allowBulk: a restore legitimately rewrites whole rosters. RosterService still
            // keeps its pre-write copy of each, so a mistaken restore is recoverable by hand.
            if (await rosters.WriteAsync(file, lines, allowBulk: true, ct).ConfigureAwait(false)) restored++;
            else failed.Add(file);
        }

        logger.LogWarning("Whitelist snapshot from {At} restored: {Restored} file(s), {Failed} failed",
            snapshot.At, restored, failed.Count);

        var message = $"Restored the snapshot from {snapshot.At:yyyy-MM-dd HH:mm} UTC: **{restored}** roster file(s), **{snapshot.Names}** name(s).";
        return failed.Count == 0
            ? new(true, message)
            : new(restored > 0, $"{message}\nNot restored: {string.Join(", ", failed)}");
    }

    /// <summary>The daily save. Skipped while the snapshot is younger than <see cref="AutoSaveAge"/>.</summary>
    public async Task AutoSaveAsync(CancellationToken ct)
    {
        if (!rosters.Enabled) return;
        if (Current() is { } existing && DateTimeOffset.UtcNow - existing.At < AutoSaveAge) return;

        var result = await SaveAsync(ct).ConfigureAwait(false);
        if (result.Ok) logger.LogInformation("Auto-saved whitelists: {Message}", result.Message);
        else logger.LogWarning("Auto whitelist snapshot skipped: {Message}", result.Message);
    }

    internal static bool IsRosterFileName(string file) =>
        !string.IsNullOrWhiteSpace(file) &&
        string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal) &&
        file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
        file is not "." and not "..";
}
