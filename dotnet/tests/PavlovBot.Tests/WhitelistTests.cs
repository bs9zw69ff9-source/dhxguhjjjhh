using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Pavlov's own whitelist.txt, written across every install.
/// </summary>
/// <remarks>
/// The manual version is <c>nano</c> on one file per server, and the bot must be no more
/// dangerous than that. This is a LIVE GAME CONFIG on a whitelisted server: a truncated
/// file locks out everybody below the cut, and a dropped line quietly revokes somebody.
/// </remarks>
public class WhitelistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlovbot-wl-" + Guid.NewGuid().ToString("N"));
    private readonly WhitelistFile _whitelist = new(NullLogger<WhitelistFile>.Instance);

    private string Path0 => Path.Combine(_root, "whitelist.txt");

    public WhitelistTests() => Directory.CreateDirectory(_root);

    // ---- parsing ----

    [Fact]
    public void CommentsAndBlanksAreNotEntries()
    {
        var entries = WhitelistFile.Entries(["# testers", "", "  ", "76561198000000001", "// old", "  0002abc  "]);

        Assert.Equal(["76561198000000001", "0002abc"], entries);
    }

    // ---- adding ----

    [Fact]
    public async Task AddingCreatesTheFileAndTheDirectory()
    {
        var nested = Path.Combine(_root, "Pavlov", "Saved", "Config", "whitelist.txt");

        var result = await _whitelist.AddAsync(nested, "76561198000000001");

        Assert.True(result.Ok);
        Assert.True(result.Changed);
        Assert.Equal(["76561198000000001"], _whitelist.Read(nested));
    }

    [Fact]
    public async Task AddingTwiceDoesNotDuplicate()
    {
        await _whitelist.AddAsync(Path0, "abc");
        var second = await _whitelist.AddAsync(Path0, "abc");

        Assert.True(second.Ok);
        Assert.False(second.Changed);     // "already correct", not a second line
        Assert.Single(_whitelist.Read(Path0));
    }

    [Fact]
    public async Task EverythingAlreadyInTheFileSurvivesAnEdit()
    {
        /* The file is HAND-MAINTAINED. Rewriting it from a parsed copy would silently delete
           comments and anything the parser did not understand - which is somebody's access. */
        await File.WriteAllTextAsync(Path0, "# testers, do not remove\nexisting-one\n\nexisting-two\n");

        await _whitelist.AddAsync(Path0, "new-one");

        var text = await File.ReadAllTextAsync(Path0);
        Assert.Contains("# testers, do not remove", text, StringComparison.Ordinal);
        Assert.Contains("existing-one", text, StringComparison.Ordinal);
        Assert.Contains("existing-two", text, StringComparison.Ordinal);
        Assert.Contains("new-one", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithNoTrailingNewlineDoesNotJoinTwoEntries()
    {
        // Hand-edited files routinely lack one, and "abcdef" instead of "abc\ndef" silently
        // revokes one person and whitelists nobody.
        await File.WriteAllTextAsync(Path0, "abc");

        await _whitelist.AddAsync(Path0, "def");

        Assert.Equal(["abc", "def"], _whitelist.Read(Path0));
    }

    [Fact]
    public async Task MatchingIsCaseInsensitive()
    {
        await _whitelist.AddAsync(Path0, "AbC");
        var again = await _whitelist.AddAsync(Path0, "abc");

        Assert.False(again.Changed);
        Assert.Single(_whitelist.Read(Path0));
    }

    // ---- removing ----

    [Fact]
    public async Task RemovingTakesOutOnlyTheOneLine()
    {
        await File.WriteAllTextAsync(Path0, "# keep me\nalice\nbob\ncarol\n");

        var result = await _whitelist.RemoveAsync(Path0, "bob");

        Assert.True(result.Changed);
        Assert.Equal(["alice", "carol"], _whitelist.Read(Path0));
        Assert.Contains("# keep me", await File.ReadAllTextAsync(Path0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingSomebodyWhoIsNotThereChangesNothing()
    {
        await File.WriteAllTextAsync(Path0, "alice\n");

        var result = await _whitelist.RemoveAsync(Path0, "bob");

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.Equal(["alice"], _whitelist.Read(Path0));
    }

    [Fact]
    public async Task RemovingFromAFileThatDoesNotExistIsNotAnError()
    {
        var result = await _whitelist.RemoveAsync(Path.Combine(_root, "nope", "whitelist.txt"), "bob");

        Assert.True(result.Ok);
        Assert.False(result.Changed);
    }

    [Fact]
    public async Task NoTemporaryFileIsLeftBehind()
    {
        // The atomic write renames its temp file. One left on disk would be read by nobody
        // and would confuse whoever next opens the directory.
        await _whitelist.AddAsync(Path0, "abc");

        Assert.False(File.Exists(Path0 + ".tmp"));
    }

    [Fact]
    public async Task AnUnwritablePathIsReportedRatherThanThrowing()
    {
        /* One install failing must not take the command down - the other two still need
           writing, and the reply has to say which one did not. */
        var result = await _whitelist.AddAsync(Path.Combine(_root, "\0bad", "whitelist.txt"), "abc");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    // ---- install discovery ----

    [Fact]
    public void ExplicitBasesWinOutright()
    {
        var found = PavlovInstalls.Discover("/srv/one, /srv/two");

        Assert.Equal(["/srv/one", "/srv/two"], found);
    }

    [Fact]
    public void SiblingInstallsAreFound()
    {
        /* pavlovserver, pavlovserver1, pavlovserver2 - the real layout. A directory without
           a Pavlov folder is a backup or an unpacked archive, not an install, and writing
           to it would look successful and reach nothing. */
        foreach (var name in new[] { "pavlovserver", "pavlovserver1", "pavlovserver2" })
            Directory.CreateDirectory(Path.Combine(_root, name, "Pavlov"));

        Directory.CreateDirectory(Path.Combine(_root, "pavlovserver.old"));   // no Pavlov dir

        var found = PavlovInstalls.Discover(null, Path.Combine(_root, "pavlovserver"));

        Assert.Equal(3, found.Count);
        Assert.All(found, f => Assert.True(Directory.Exists(Path.Combine(f, "Pavlov"))));
    }

    [Fact]
    public void NothingFoundStillYieldsTheConfiguredBase()
    {
        /* An empty list would make every cross-install write silently succeed at doing
           nothing, which is the worst possible outcome for a whitelist. */
        var found = PavlovInstalls.Discover(null, Path.Combine(_root, "does-not-exist"));

        Assert.Single(found);
    }

    [Fact]
    public void TheWhitelistPathIsTheOneOperatorsEdit()
    {
        Assert.Equal(
            Path.Combine("/home/steam/pavlovserver", "Pavlov", "Saved", "Config", "whitelist.txt"),
            PavlovInstalls.WhitelistPath("/home/steam/pavlovserver"));
    }

    // ---- name vs unique id ----

    [Fact]
    public void AUniqueIdIsRecognisedAndPassedThrough()
    {
        /* Pavlov matches the whitelist on unique ID. An operator pasting one must not have
           it treated as a display name and looked up. */
        Assert.True(TesterCommand.LooksLikeId("76561198000000001"));
        Assert.True(TesterCommand.LooksLikeId("0002abc0002abc0002abc"));
    }

    [Fact]
    public void ADisplayNameIsNotMistakenForAnId()
    {
        // These get resolved through the account records instead, and flagged when they
        // cannot be - a name in a file keyed by id never matches anybody.
        Assert.False(TesterCommand.LooksLikeId("Pkdestroy"));
        Assert.False(TesterCommand.LooksLikeId("A Very Long Player Name"));
        Assert.False(TesterCommand.LooksLikeId("short"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
