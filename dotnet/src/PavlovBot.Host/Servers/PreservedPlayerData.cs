using System.Globalization;

namespace PavlovBot.Host.Servers;

/// <param name="Moved">The items moved, relative to <c>Pavlov/Saved/Config</c>.</param>
/// <param name="Skipped">Items left where they were, each with the reason.</param>
public sealed record PreservedMove(IReadOnlyList<string> Moved, IReadOnlyList<string> Skipped);

/// <summary>
/// The player data inside an install that <c>/deleteserver</c> must not destroy with it.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. <c>/deleteserver</c> ran <c>rm -rf</c> on the whole install, and
/// <c>Pavlov/Saved/Config/ModSave</c> lives inside it: the faction rosters
/// (<c>FACTION_ROLES_PATH</c> points there), the caps ledgers and every other mod save. Deleting
/// and re-provisioning the servers wiped all of it, and <c>/whitelist</c> then answered "the
/// roster files are unreachable" because the folder no longer existed.
///
/// So a delete MOVES that data out first, to <see cref="PreservedDir"/> beside the installs,
/// and a provision into the same install directory moves it back. Moving rather than copying:
/// it is one rename on the same disk, it keeps the steam ownership the game needs, and it
/// cannot half-finish into two diverging copies.
///
/// The preserved folder is a dot-directory, so <see cref="Storage.PavlovInstalls.Discover"/>'s
/// <c>pavlovserver*</c> scan never mistakes it for an install.
/// </remarks>
public static class PreservedPlayerData
{
    /// <summary>What is kept, relative to <c>Pavlov/Saved/Config</c>.</summary>
    /// <remarks>
    /// Player data only. Game.ini and RconSettings.txt are NOT kept: a provision writes them
    /// fresh with the new slot's ports and RCON password, and restoring the old ones would
    /// point the server at the wrong settings.
    /// </remarks>
    public static readonly IReadOnlyList<string> Items = ["ModSave", "mods.txt", "whitelist.txt", "blacklist.txt"];

    /// <summary>Where an install's player data waits between a delete and the next provision.</summary>
    public static string PreservedDir(string installDir)
    {
        var trimmed = installDir.TrimEnd('/');
        var parent = Path.GetDirectoryName(trimmed) ?? "/";
        return Path.Combine(parent, ".pavlov-preserved", Path.GetFileName(trimmed));
    }

    private static string ConfigDir(string installDir) => Path.Combine(installDir, "Pavlov", "Saved", "Config");

    /// <summary>
    /// Move the player data out of an install that is about to be deleted.
    /// </summary>
    /// <remarks>
    /// THROWS on an I/O failure, deliberately: the caller must not go on to delete an install
    /// whose data could not be moved out. Anything moved before the failure is already safe in
    /// <see cref="PreservedDir"/>.
    ///
    /// An earlier preserved copy that was never restored is moved aside with a timestamp rather
    /// than merged into or overwritten, so a second delete can never destroy the first one's data.
    /// </remarks>
    public static PreservedMove Preserve(string installDir, DateTimeOffset now)
    {
        var config = ConfigDir(installDir);
        var target = PreservedDir(installDir);

        var present = Items.Where(item => Exists(Path.Combine(config, item))).ToList();
        if (present.Count == 0) return new PreservedMove([], []);

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            Directory.Move(target, $"{target}-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");

        Directory.CreateDirectory(target);

        foreach (var item in present)
            Move(Path.Combine(config, item), Path.Combine(target, item));

        return new PreservedMove(present, []);
    }

    /// <summary>
    /// Move preserved data back into a freshly provisioned install. Never overwrites real data.
    /// </summary>
    /// <remarks>
    /// A fresh provision leaves the three lists empty (it touches them) and has no ModSave, so
    /// an empty file or an empty folder is replaced and anything with content is left alone and
    /// reported - the preserved copy then stays where it is for somebody to look at.
    /// </remarks>
    public static PreservedMove Restore(string installDir)
    {
        var source = PreservedDir(installDir);
        if (!Directory.Exists(source)) return new PreservedMove([], []);

        var config = ConfigDir(installDir);
        Directory.CreateDirectory(config);

        var moved = new List<string>();
        var skipped = new List<string>();

        foreach (var item in Items)
        {
            var from = Path.Combine(source, item);
            if (!Exists(from)) continue;

            var to = Path.Combine(config, item);
            if (HasContent(to))
            {
                skipped.Add($"{item} (the new install already has one with content)");
                continue;
            }

            if (Directory.Exists(to)) Directory.Delete(to);
            else if (File.Exists(to)) File.Delete(to);

            Move(from, to);
            moved.Add(item);
        }

        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);

        return new PreservedMove(moved, skipped);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool HasContent(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Any()
        : File.Exists(path) && new FileInfo(path).Length > 0;

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }
}
