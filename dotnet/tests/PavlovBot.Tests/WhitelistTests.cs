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

    // ---- writing ----

    /* THESE REPLACE THREE TESTS THAT PINNED A REFUSAL. /tester add was turned into a
       read-only check under a blanket "the bot does not write files the game server owns"
       rule, and the tests below encoded that refusal as the intended behaviour.

       The rule is real but it was over-applied. It rests on the server holding a value in
       MEMORY and rewriting the file from it, which is true of player ledgers and is not true
       of whitelist.txt - a config input the server reads and never writes back, in the same
       directory and the same format as the faction roster files the bot has always written.

       So the refusal is gone and what the rule was actually protecting is tested instead:
       nothing is truncated, nothing unrelated is lost, and no directory is created. */

    [Fact]
    public async Task AddingAppendsAndLeavesEverythingElseExactlyAsItWas()
    {
        /* THE FILE IS HAND-MAINTAINED. It has comments in it saying who somebody is and blank
           lines separating groups. Rebuilding it from Read() - which drops both - would
           silently delete all of that on the first add. */
        await File.WriteAllTextAsync(Path0, "# testers\nalice\n\n// retired\nbob\n");

        var result = await _whitelist.AddAsync(Path0, "carol");

        Assert.True(result.Ok);
        Assert.True(result.Changed);
        Assert.Equal("# testers\nalice\n\n// retired\nbob\ncarol\n", await File.ReadAllTextAsync(Path0));
    }

    [Fact]
    public async Task RemovingDropsOnlyThatLine()
    {
        await File.WriteAllTextAsync(Path0, "# testers\nalice\nbob\ncarol\n");

        var result = await _whitelist.RemoveAsync(Path0, "bob");

        Assert.True(result.Changed);
        Assert.Equal("# testers\nalice\ncarol\n", await File.ReadAllTextAsync(Path0));
    }

    [Fact]
    public async Task AnEntryAlreadyThereIsReportedRatherThanRewritten()
    {
        /* Ok but not Changed. Rewriting a file the game reads live to change nothing is all
           risk and no benefit, and the reply needs to distinguish "I added them" from "they
           were already on it" or the admin cannot tell whether their first attempt worked. */
        await File.WriteAllTextAsync(Path0, "alice\n");
        var written = File.GetLastWriteTimeUtc(Path0);

        var result = await _whitelist.AddAsync(Path0, "alice");

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path0));
    }

    [Fact]
    public async Task RemovingSomebodyWhoIsNotThereChangesNothing()
    {
        await File.WriteAllTextAsync(Path0, "alice\n");

        var result = await _whitelist.RemoveAsync(Path0, "nobody");

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.Equal("alice\n", await File.ReadAllTextAsync(Path0));
    }

    [Fact]
    public async Task TheBotNeverCreatesADirectoryInsideAnInstall()
    {
        /* The half of the old rule that was never an over-application. A missing directory
           means the configured install path is wrong, and building it produced a second
           config tree beside the real one - writes that reported success against files the
           game server never read. */
        var missing = Path.Combine(_root, "no-such-install", "whitelist.txt");

        var result = await _whitelist.AddAsync(missing, "alice");

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        Assert.Contains("does not exist", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteThatWouldGutTheWhitelistIsRefused()
    {
        /* AN EMPTY WHITELIST IS A VALID FILE. The server accepts it without complaint and on
           a whitelisted server it locks out every single player - so the failure would arrive
           as "the server is broken", not as an error. The guard is on the SIZE of the change,
           which is why a bug cannot talk its way past it by producing well-formed output. */
        // Every entry is the same name, so one "remove" would take all forty out at once.
        await File.WriteAllTextAsync(Path0, string.Join("\n", Enumerable.Repeat("alice", 40)) + "\n");

        var result = await _whitelist.RemoveAsync(Path0, "alice");

        Assert.False(result.Ok);
        Assert.Equal(40, _whitelist.Read(Path0).Count);
    }

    [Fact]
    public async Task NoTemporaryFileIsLeftBesideTheWhitelist()
    {
        // The write is temp-then-rename so the game never reads a half-written file. A
        // leftover .tmp in the config directory is exactly the kind of stray the bot must
        // not leave inside an install.
        await File.WriteAllTextAsync(Path0, "alice\n");

        await _whitelist.AddAsync(Path0, "bob");

        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task ConcurrentAddsToOneFileDoNotLoseEntries()
    {
        /* A read-modify-write over a file the game reads live. Without the per-file gate both
           adds read the old contents and the second write drops the first entry - a lost
           update that leaves a well-formed file, so nothing looks wrong afterwards. */
        await File.WriteAllTextAsync(Path0, "alice\n");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => _whitelist.AddAsync(Path0, $"player{i}")));

        var entries = _whitelist.Read(Path0);
        Assert.Equal(9, entries.Count);
        Assert.Contains("alice", entries, StringComparer.Ordinal);
    }

    // ---- reading: unaffected ----

    [Fact]
    public void ReadingStillWorks()
    {
        /* /tester list still shows who is whitelisted, and can tell an admin the exact line
           to add by hand. A read cannot corrupt a file the game is also using. */
        File.WriteAllText(Path0, "# testers\nalice\nBart.simpson.2010kk\n\n// old\n");

        Assert.Equal(["alice", "Bart.simpson.2010kk"], _whitelist.Read(Path0));
    }

    [Fact]
    public void ReadingAFileThatIsNotThereIsEmpty_NotAnError()
    {
        Assert.Empty(_whitelist.Read(Path.Combine(_root, "nope", "whitelist.txt")));
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

    // ---- what actually gets written ----

    [Fact]
    public void AUsernameIsWrittenVerbatim()
    {
        /* ONE USERNAME PER LINE, and the file is matched against the player's actual in-game
           name - so anything reshaped on the way in matches nobody. Sanitize.Id, which the
           rest of the bot uses for RCON arguments, strips everything outside
           [a-zA-Z0-9_-.] and would silently drop the space out of this one. */
        Assert.Equal("A Player Name", WhitelistFile.Entry("A Player Name"));
        Assert.Equal("Bart.simpson.2010kk", WhitelistFile.Entry("Bart.simpson.2010kk"));
        Assert.Equal("[TAG] Someone!", WhitelistFile.Entry("[TAG] Someone!"));
        Assert.Equal("Ünïcødé", WhitelistFile.Entry("Ünïcødé"));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmed()
    {
        // Copied out of Discord, it arrives with a space on the end more often than not.
        Assert.Equal("Pkdestroy", WhitelistFile.Entry("  Pkdestroy \t"));
    }

    [Fact]
    public void ANameCannotForgeASecondEntry()
    {
        /* The file is one entry per line, so a newline in the name would whitelist a second
           account - which is how somebody gets an alt in by asking to be whitelisted once. */
        var entry = WhitelistFile.Entry("Innocent\nSomebodyElse");

        Assert.DoesNotContain("\n", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", entry, StringComparison.Ordinal);
        Assert.Equal("Innocent SomebodyElse", entry);
    }

    [Fact]
    public void AnEmptyOrControlOnlyNameYieldsNothing()
    {
        // The command refuses on an empty result rather than writing a blank line.
        Assert.Equal("", WhitelistFile.Entry(null));
        Assert.Equal("", WhitelistFile.Entry("   "));
        Assert.Equal("", WhitelistFile.Entry("\r\n\t"));
    }

    [Fact]
    public async Task ANameWithASpaceSurvivesTheRoundTrip()
    {
        // The file is matched against the player's actual in-game name, and those routinely
        // contain spaces. A name that does not read back is a whitelist entry matching nobody.
        await File.WriteAllTextAsync(Path0, "# testers\n");

        await _whitelist.AddAsync(Path0, WhitelistFile.Entry("A Player Name"));

        Assert.Equal(["A Player Name"], _whitelist.Read(Path0));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
