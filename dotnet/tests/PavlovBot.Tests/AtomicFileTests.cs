using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Replacing a game file must not take it away from the game.
/// </summary>
public class AtomicFileTests : IDisposable
{
    /// <summary>The conventional unprivileged uid/gid, standing in for the game's steam user.</summary>
    private const uint Nobody = 65534;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ContentIsReplacedAndNoTempFileIsLeft()
    {
        var path = Path.Combine(_dir, "Alice.txt");
        File.WriteAllText(path, "100");

        AtomicFile.Write(path, "250");

        Assert.Equal("250", File.ReadAllText(path));
        Assert.Equal(["Alice.txt"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void TheModeOfTheReplacedFileIsKept()
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_dir, "blacklist.txt");
        File.WriteAllText(path, "");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                  UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(path, mode);

        AtomicFile.Write(path, "someone");

        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void OwnershipCanBeReadAtAll()
    {
        // statx must bind, or preservation silently does nothing on this platform.
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(_dir, "probe.txt");
        File.WriteAllText(path, "");

        Assert.NotNull(UnixFileOwnership.Get(path));
        Assert.False(UnixFileOwnership.Unavailable);
    }

    [Fact]
    public void AsRootTheReplacedFileKeepsTheGamesOwner()
    {
        /* THE BUG: the bot runs as root, the game as steam. Temp-then-rename left every ledger
           and ban file the bot touched owned by root, and the game could no longer save them.
           Only root can give a file to another user, so this proves it only when run as root. */
        if (OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess) return;

        var path = Path.Combine(_dir, "Alice.txt");
        File.WriteAllText(path, "100");
        Assert.True(UnixFileOwnership.Set(path, Nobody, Nobody));

        AtomicFile.Write(path, "250");

        Assert.Equal((Nobody, Nobody), UnixFileOwnership.Get(path));
    }

    [Fact]
    public void AsRootANewFileTakesItsDirectorysOwner()
    {
        // A roster created for the first time lands in the game's directory; it must be the game's.
        if (OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess) return;

        Assert.True(UnixFileOwnership.Set(_dir, Nobody, Nobody));
        var path = Path.Combine(_dir, "NewRoster.txt");

        AtomicFile.Write(path, "");

        Assert.Equal((Nobody, Nobody), UnixFileOwnership.Get(path));
    }

    [Fact]
    public void AsRootTheFileGoesBackToItsOwner()
    {
        Assert.Equal((1000u, 1000u, false), AtomicFile.Plan(privileged: true, reference: (1000, 1000), mine: (0, 0)));
    }

    [Fact]
    public void UnprivilegedTheFileGoesToTheGamesGroupAndStaysGroupWritable()
    {
        /* A bot running as its own user cannot hand a file back to steam, but it can set the
           group to steam (it is a member) and keep it writable - otherwise the game could no
           longer save a file the bot had touched. */
        Assert.Equal((UnixFileOwnership.Unchanged, 1000u, true),
            AtomicFile.Plan(privileged: false, reference: (1000, 1000), mine: (1001, 1001)));
    }

    [Fact]
    public void NothingChangesWhenTheOwnerAlreadyMatches()
    {
        Assert.Null(AtomicFile.Plan(privileged: false, reference: (1001, 1001), mine: (1001, 1001)));
    }
}
