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

    // ---- writing: refused ----

    [Fact]
    public async Task TheBotDoesNotWriteWhitelistTxt()
    {
        /* whitelist.txt belongs to the GAME SERVER. The bot writing it is one of the ways it
           ended up touching files the server owns, so both mutations refuse outright rather
           than half-succeeding on some installs. */
        var add = await _whitelist.AddAsync(Path0, "Pkdestroy");
        var remove = await _whitelist.RemoveAsync(Path0, "Pkdestroy");

        Assert.False(add.Ok);
        Assert.False(remove.Ok);
        Assert.False(File.Exists(Path0), "the bot created whitelist.txt");
    }

    [Fact]
    public async Task TheRefusalSaysWhoOwnsTheFile()
    {
        // The reply an admin sees has to tell them what to do instead, not just "failed".
        var result = await _whitelist.AddAsync(Path0, "Pkdestroy");

        Assert.Contains("game server owns", result.Error!, StringComparison.Ordinal);
        Assert.Contains("Edit it on the server", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingWhitelistIsNotTouched()
    {
        // The worst outcome would be a refusal that still managed to truncate the file.
        await File.WriteAllTextAsync(Path0, "# testers\nalice\nbob\n");

        await _whitelist.AddAsync(Path0, "carol");
        await _whitelist.RemoveAsync(Path0, "alice");

        Assert.Equal("# testers\nalice\nbob\n", await File.ReadAllTextAsync(Path0));
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
    public void ANameWithASpaceSurvivesAReadBack()
    {
        // The bot no longer writes the file, but it still has to READ names the game wrote -
        // and those routinely contain spaces.
        File.WriteAllText(Path0, "A Player Name\n");

        Assert.Equal(["A Player Name"], _whitelist.Read(Path0));
        Assert.Equal("A Player Name", WhitelistFile.Entry("A Player Name"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
