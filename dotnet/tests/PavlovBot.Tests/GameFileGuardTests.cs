using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Paths a bot must never write to, for a second bot sharing one game server.
/// </summary>
/// <remarks>
/// THE FAILURE THIS PREVENTS. Several subsystems rewrite a whole file from their own
/// database - the ban list, the whitelist, a player's balance. Two bots against one install
/// means each erases the other's work on a timer, and the loser is whichever wrote first.
///
/// The second bot's .env is a COPY of the first's, so every shared path is already in it,
/// correct, and pointing at files it must not touch.
/// </remarks>
public class GameFileGuardTests : IDisposable
{
    /* REAL DIRECTORIES, because the guard COMPOSES the older rule: the bot never writes into
       a directory that does not exist. Against imaginary paths every case would be refused
       for that reason instead, and the tests would pass while proving nothing about the
       ignore list. */
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}");

    private static GameFileGuard Guarding(params string[] paths) =>
        new(paths, NullLogger<GameFileGuard>.Instance);

    private string Temp(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        return path;
    }

    /// <summary>A directory that exists, so only the ignore rule is under test.</summary>
    private string TempDirectory(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AFileInsideAnIgnoredDirectoryIsRefused()
    {
        var guard = Guarding(TempDirectory("pavlovserver"));

        var problem = guard.Problem(Temp("pavlovserver", "Pavlov", "Saved", "Config", "blacklist.txt"));

        Assert.NotNull(problem);
        Assert.Contains("IGNORE_PATHS", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIgnoredPathItselfIsRefused()
    {
        // Naming a FILE rather than a directory has to work: the ban list is one file inside a
        // directory that also holds ledgers the bot may legitimately write.
        var file = Temp("pavlovserver", "blacklist.txt");

        Assert.NotNull(Guarding(file).Problem(file));
    }

    /// <summary>
    /// Matching is separator-bounded, not a bare prefix.
    /// </summary>
    /// <remarks>
    /// A prefix test would make "pavlovserver" also cover "pavlovserver-backup" and
    /// "pavlovserver2" - quietly refusing writes to a second install that the bot genuinely
    /// owns, which presents as whitelists silently not saving.
    /// </remarks>
    [Fact]
    public void ASiblingDirectoryWithTheSamePrefixIsNotCovered()
    {
        var guard = Guarding(TempDirectory("pavlovserver"));
        TempDirectory("pavlovserver-backup");
        TempDirectory("pavlovserver2");

        Assert.Null(guard.Problem(Temp("pavlovserver-backup", "blacklist.txt")));
        Assert.Null(guard.Problem(Temp("pavlovserver2", "blacklist.txt")));
    }

    [Fact]
    public void NothingIsRefusedWhenNothingIsIgnored()
    {
        /* The default, and what every existing single-bot deployment gets. GameFileGuard.None
           must behave exactly as the bot did before this existed. */
        Assert.False(GameFileGuard.None.Any);
        Assert.Empty(GameFileGuard.None.Paths);
    }

    [Fact]
    public void BlankEntriesAreDroppedRatherThanMatchingEverything()
    {
        /* A trailing comma in .env produces an empty entry, and an empty string turned into a
           full path is the CURRENT DIRECTORY - which would refuse every write the bot makes,
           everywhere, and read as the bot being completely broken. */
        var guard = Guarding("", "   ");
        TempDirectory("pavlovserver");

        Assert.False(guard.Any);
        Assert.Null(guard.Problem(Temp("pavlovserver", "blacklist.txt")));
    }

    [Fact]
    public void TheExistingDirectoryRuleStillApplies()
    {
        /* The guard COMPOSES the older rule rather than replacing it: the bot still never
           writes into a directory that does not exist, because a missing one means the
           configured path is wrong. */
        var guard = Guarding(TempDirectory("somewhere-else"));

        var problem = guard.Problem(Path.Combine(_root, $"no-such-directory-{Guid.NewGuid():N}", "file.txt"));

        Assert.NotNull(problem);
        Assert.DoesNotContain("IGNORE_PATHS", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void APathThatIsNotUsableIsReportedRatherThanThrown()
    {
        // This runs on every write. An exception here would turn a typo in .env into a crash
        // in the middle of a roster update.
        Assert.NotNull(Guarding(TempDirectory("pavlovserver")).Problem(""));
        Assert.NotNull(Guarding(TempDirectory("pavlovserver")).Problem(null));
    }

    /// <summary>
    /// Ignoring a PARENT directory takes everything under it, including the rosters.
    /// </summary>
    /// <remarks>
    /// THE TRAP THIS DOCUMENTS, and the one the shipped example originally fell into.
    /// FactionRoles lives INSIDE ModSave, so ignoring the ModSave tree to protect the ban list
    /// and the ledgers silently takes the second bot's own whitelists with it.
    ///
    /// The behaviour here is correct and deliberate - a directory covers what is under it.
    /// What was wrong was the advice. Startup now refuses the combination of
    /// FACTION_ROLES_PATH inside IGNORE_PATHS outright, because the bot would otherwise come
    /// up reporting the whitelists as enabled and refuse every single write.
    /// </remarks>
    [Fact]
    public void IgnoringAParentDirectoryAlsoCoversTheRostersInsideIt()
    {
        var modsave = TempDirectory("ModSave");
        var rosters = TempDirectory("ModSave", "FactionRoles");

        var guard = Guarding(modsave);

        Assert.NotNull(guard.Problem(Path.Combine(rosters, "policecadet.txt")));

        // Naming the files instead leaves the rosters writable, which is the documented fix.
        var byFile = Guarding(Path.Combine(modsave, "blacklist.txt"));

        Assert.Null(byFile.Problem(Path.Combine(rosters, "policecadet.txt")));
        Assert.NotNull(byFile.Problem(Path.Combine(modsave, "blacklist.txt")));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
