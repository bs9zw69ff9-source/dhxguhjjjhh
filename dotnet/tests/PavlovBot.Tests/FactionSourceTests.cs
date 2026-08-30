using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The startup line that says which faction file was actually read.
/// </summary>
/// <remarks>
/// THE QUESTION IT EXISTS TO ANSWER: "I removed a faction and it is still there." The line
/// used to print FACTIONS_PATH exactly as configured, and a relative path resolves against
/// the process's working directory - which under pm2 is not necessarily where somebody was
/// standing when they edited a file of that name. A bot reading a different copy printed the
/// same line as one reading theirs, so an edit that had plainly been made looked ignored.
/// </remarks>
public class FactionSourceTests
{
    [Fact]
    public void AnUnsetPathSaysSoRatherThanShowingNothing()
    {
        // Unset is a valid, normal configuration - it means the built-in set - and it must be
        // distinguishable from a path that resolved to nowhere.
        Assert.Equal("built in (FACTIONS_PATH not set)", PavlovBot.Host.Program.FactionSource(null));
    }

    [Fact]
    public void ARelativePathIsResolvedToTheFileActuallyRead()
    {
        /* THE WHOLE POINT. "factions.json" names a different file depending on where the
           process is standing, and the operator cannot see where that is. */
        var directory = Path.Combine(Path.GetTempPath(), $"pavlov-factions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previous = Directory.GetCurrentDirectory();

        try
        {
            var file = Path.Combine(directory, "factions.json");
            File.WriteAllText(file, "{}");
            Directory.SetCurrentDirectory(directory);

            var described = PavlovBot.Host.Program.FactionSource("factions.json");

            Assert.Contains(Path.GetFullPath(file), described, StringComparison.Ordinal);
            Assert.DoesNotContain("built in", described, StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheLastEditIsStampedSoAnUndeployedChangeIsVisible()
    {
        /* The other half of the same question: a file edited before this process started was
           edited and never deployed, and that is indistinguishable from a file the bot
           ignored unless the time is on the line. */
        var file = Path.Combine(Path.GetTempPath(), $"pavlov-factions-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{}");

        try
        {
            var edited = new DateTime(2026, 3, 4, 5, 6, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(file, edited);

            Assert.Contains("2026-03-04 05:06Z", PavlovBot.Host.Program.FactionSource(file), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AMissingFileStillResolvesRatherThanThrowing()
    {
        // Startup refuses this path before the summary runs, so it should never be seen - but
        // the summary must not be the thing that throws if it ever is.
        var missing = Path.Combine(Path.GetTempPath(), $"pavlov-absent-{Guid.NewGuid():N}.json");

        var described = PavlovBot.Host.Program.FactionSource(missing);

        Assert.Equal(Path.GetFullPath(missing), described);
    }
}
