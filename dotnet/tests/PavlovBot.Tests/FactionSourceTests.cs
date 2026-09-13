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
        Assert.Equal("built in (neither FACTION_SET nor FACTIONS_PATH set)",
            PavlovBot.Host.Program.FactionSource(null));
    }

    [Fact]
    public void ANamedSetIsReportedByName()
    {
        // The whole point of the preset is that there is no file to point at, so the line
        // has to say where the factions came from instead of falling silent.
        Assert.Contains("fallout", PavlovBot.Host.Program.FactionSource(null, "fallout"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileIsNamedEvenWhenASetNameIsAlsoSet()
    {
        /* FACTIONS_PATH wins, so naming the set here would state the opposite of what was
           loaded - the exact class of lie this line exists to stop. */
        var file = Path.Combine(Path.GetTempPath(), $"pavlov-factions-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{}");

        try
        {
            var described = PavlovBot.Host.Program.FactionSource(file, "fallout");

            Assert.Contains(file, described, StringComparison.Ordinal);
            Assert.DoesNotContain("fallout", described, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
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

/// <summary>
/// What happens when FACTIONS_PATH names a file that is not there.
/// </summary>
/// <remarks>
/// THE FAILURE THIS COMES FROM. A themed clone moved off its faction file onto the built-in
/// set, deleted the file, and left the FACTIONS_PATH line in .env. The bot then refused to
/// start - correct on its own terms, and invisible: a process that is not running registers
/// no commands, so Discord kept serving the list from the last time it was alive and every
/// change to the factions looked like it had never shipped.
/// </remarks>
public class MissingFactionFileTests
{
    [Fact]
    public void TheSummaryNamesTheSetItFellBackTo()
    {
        /* THE LINE EVERYBODY CHECKS. Naming a file that was not read is the same lie the
           silent built-in fallback used to tell - "factions: /root/.../factions.json" while
           the ladders came from somewhere else entirely. */
        var absent = Path.Combine(Path.GetTempPath(), "pavlovbot-absent-" + Guid.NewGuid().ToString("N") + ".json");

        var line = PavlovBot.Host.Program.FactionSource(absent, "fallout");

        Assert.Contains("fallout", line, StringComparison.Ordinal);
        Assert.Contains("does not exist", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFileFallsBackToTheSetWhenOneIsNamed()
    {
        var absent = Path.Combine(Path.GetTempPath(), "pavlovbot-absent-" + Guid.NewGuid().ToString("N") + ".json");

        Assert.True(PavlovBot.Host.Program.MayFallBackToSet(absent, "fallout"));
    }

    [Fact]
    public void WithNoSetNamedItIsStillAFatalMisconfiguration()
    {
        /* Nothing to fall back TO. Starting on the built-in police ladders would have a
           Fallout bot writing policecadet.txt, which is worse than not starting. */
        var absent = Path.Combine(Path.GetTempPath(), "pavlovbot-absent-" + Guid.NewGuid().ToString("N") + ".json");

        Assert.False(PavlovBot.Host.Program.MayFallBackToSet(absent, null));
        Assert.False(PavlovBot.Host.Program.MayFallBackToSet(absent, ""));
        Assert.False(PavlovBot.Host.Program.MayFallBackToSet(absent, "falout"));   // a typo is not a set
    }

    [Fact]
    public void AFileTHATEXISTSIsNeverSkipped()
    {
        // A malformed file must still stop the bot: those are ladders somebody is editing,
        // and running a different set writes the wrong roster files.
        var present = Path.Combine(Path.GetTempPath(), "pavlovbot-present-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(present, "{}");

        try
        {
            Assert.False(PavlovBot.Host.Program.MayFallBackToSet(present, "fallout"));
        }
        finally
        {
            File.Delete(present);
        }
    }
}
