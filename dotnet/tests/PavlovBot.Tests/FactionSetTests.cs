using PavlovBot.Core.Factions;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The faction roster as configuration, so one binary can serve more than one RP.
/// </summary>
/// <remarks>
/// THE DEPLOYMENT THIS EXISTS FOR: a themed clone running beside the normal bot, against the
/// SAME game server. That makes the file-name checks the important half - two factions
/// sharing a roster file merge their members in game, and nothing downstream could notice.
/// </remarks>
public class FactionSetTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"factions-{Guid.NewGuid():N}");

    private string Write(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "factions.json");
        File.WriteAllText(path, json);
        return path;
    }

    // ---- the default ----

    [Fact]
    public void TheBuiltInSetIsTheDefaultAndIsValid()
    {
        /* An existing deployment configures nothing and must get exactly what it has today.
           If the built-in set could not pass its own validation, every normal bot would be
           one strict-mode change away from refusing to start. */
        Assert.Empty(FactionRegistry.Default.Problems());
        Assert.Contains("NYPD", FactionRegistry.Default.Names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, FactionRegistry.Default.All.Count);
    }

    // ---- loading ----

    [Fact]
    public void AFactionFileReplacesTheBuiltInSetEntirely()
    {
        var path = Write("""
            { "factions": [
                { "name": "NCR", "spawnFile": "ncrspawn.txt",
                  "ranks": [ { "name": "Recruit", "file": "ncrrecruit.txt", "cap": 40 },
                             { "name": "Ranger",  "file": "ncrranger.txt",  "cap": 5 } ] }
            ] }
            """);

        var loaded = FactionsFile.Load(path);

        Assert.Empty(loaded.Problems);
        Assert.NotNull(loaded.Set);

        // REPLACES, not merges. A Fallout bot must not answer /whitelist add NYPD.
        Assert.Equal(["NCR"], loaded.Set!.Names);

        var ncr = loaded.Set.Get("NCR")!;
        Assert.Equal("Recruit", ncr.Default);          // the lowest rank, unstated
        Assert.Equal("Ranger", ncr.Highest);
        Assert.True(ncr.HasRanks);
    }

    [Fact]
    public void ALadderlessFactionNeedsOnlyItsOneFile()
    {
        // Requiring spawnFile to repeat the single rank file would be a rule to remember for
        // no benefit, and forgetting it produces a faction nobody can spawn as.
        var path = Write("""
            { "factions": [
                { "name": "Traders", "ranks": [ { "name": "Member", "file": "tradersspawn.txt" } ] }
            ] }
            """);

        var traders = FactionsFile.Load(path).Set!.Get("Traders")!;

        Assert.Equal("tradersspawn.txt", traders.SpawnFile);
        Assert.False(traders.HasRanks);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        /* This file is hand-edited by whoever runs the server, at the point they are adding a
           rank at 2am. Strict JSON's refusal to explain a trailing comma is a bad experience
           for a config file. */
        var path = Write("""
            {
              // the wasteland
              "factions": [
                { "name": "Legion", "ranks": [ { "name": "Recruit", "file": "legionrecruit.txt" }, ] },
              ],
            }
            """);

        Assert.Empty(FactionsFile.Load(path).Problems);
    }

    // ---- refusing a bad file ----

    [Fact]
    public void TwoFactionsSharingARosterFileIsRefused()
    {
        /* THE CHECK THAT MATTERS. Two factions writing one file merge their memberships in
           game, and the bot would report both rosters correctly while the server honoured one
           list for both - there is no downstream symptom that points at the cause. */
        var path = Write("""
            { "factions": [
                { "name": "NCR",    "ranks": [ { "name": "Member", "file": "shared.txt" } ] },
                { "name": "Legion", "ranks": [ { "name": "Member", "file": "shared.txt" } ] }
            ] }
            """);

        var loaded = FactionsFile.Load(path);

        Assert.Null(loaded.Set);
        Assert.Contains(loaded.Problems, p => p.Contains("shared.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefaultRankThatIsNotARankIsRefused()
    {
        var path = Write("""
            { "factions": [
                { "name": "NCR", "default": "General",
                  "ranks": [ { "name": "Recruit", "file": "ncrrecruit.txt" } ] }
            ] }
            """);

        var loaded = FactionsFile.Load(path);

        Assert.Null(loaded.Set);
        Assert.Contains(loaded.Problems, p => p.Contains("General", StringComparison.Ordinal));
    }

    [Fact]
    public void AFactionWithNoRanksIsRefused()
    {
        var loaded = FactionsFile.Load(Write("""{ "factions": [ { "name": "Ghosts" } ] }"""));

        Assert.Null(loaded.Set);
        Assert.Contains(loaded.Problems, p => p.Contains("Ghosts", StringComparison.Ordinal));
    }

    [Fact]
    public void BrokenJsonIsReportedWithItsPositionRatherThanThrown()
    {
        /* Startup calls this. An exception would turn a typo in a roster file into a stack
           trace, which is a worse answer to the same question. */
        var loaded = FactionsFile.Load(Write("{ \"factions\": [ "));

        Assert.Null(loaded.Set);
        Assert.NotEmpty(loaded.Problems);
    }

    [Fact]
    public void AMissingFileIsRefusedRatherThanFallingBackToTheBuiltIns()
    {
        /* A path that was set and does not resolve is a typo. Falling back would start a
           Fallout bot running the police roster - which looks exactly like the file being
           ignored, because it is. */
        var loaded = FactionsFile.Load(Path.Combine(_directory, "nope.json"));

        Assert.Null(loaded.Set);
        Assert.Contains(loaded.Problems, p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        // Fixing a roster file one restart at a time turns a five-minute edit into an
        // afternoon. Same rule the rest of startup follows.
        var path = Write("""
            { "factions": [
                { "name": "A", "default": "Nope", "ranks": [ { "name": "One", "file": "a.txt" } ] },
                { "name": "B", "default": "Also", "ranks": [ { "name": "One", "file": "b.txt" } ] }
            ] }
            """);

        Assert.True(FactionsFile.Load(path).Problems.Count >= 2);
    }

    // ---- caps, which are no longer enforced ----

    [Fact]
    public void AFileThatStillSetsCapsLoadsAndSaysHowManyWereIgnored()
    {
        /* AN OLD FILE IS A GOOD FILE. Rank caps are gone, and refusing to start over a
           setting that no longer does anything would be worse than ignoring it - but
           silently ignoring it is how a setting comes to mean something other than what it
           says, so the count comes back for the startup summary to report. */
        var path = Write("""
            { "factions": [
                { "name": "NCR", "ranks": [ { "name": "Recruit", "file": "ncrrecruit.txt", "cap": 40 },
                                            { "name": "Trooper", "file": "ncrtrooper.txt", "cap": 25 } ] }
            ] }
            """);

        var loaded = FactionsFile.Load(path);

        Assert.NotNull(loaded.Set);
        Assert.Empty(loaded.Problems);
        Assert.Equal(2, loaded.IgnoredRankCaps);
    }

    [Fact]
    public void AFileWithNoCapsReportsNoneIgnored()
    {
        // The control: the count has to mean "caps were found", not "ranks were found".
        var path = Write("""
            { "factions": [
                { "name": "NCR", "ranks": [ { "name": "Recruit", "file": "ncrrecruit.txt" },
                                            { "name": "Trooper", "file": "ncrtrooper.txt", "cap": 0 } ] }
            ] }
            """);

        Assert.Equal(0, FactionsFile.Load(path).IgnoredRankCaps);
    }

    // ---- the shipped template ----

    /// <summary>
    /// The Fallout example file in the repo actually loads.
    /// </summary>
    /// <remarks>
    /// A template that does not parse is worse than no template: it is copied, it fails at
    /// startup, and the person copying it has no reason to suspect the file they were given.
    /// This is also what stops it rotting the next time the schema moves.
    ///
    /// THE FILE NAMES ARE CHECKED AGAINST THE BUILT-IN SET, because the deployment this was
    /// written for runs both bots against ONE game server. The loader cannot see the other
    /// bot's roster - a collision here would be found by a player who could not spawn.
    /// </remarks>
    [Fact]
    public void TheShippedFalloutTemplateLoadsAndCannotCollideWithTheBuiltInRosters()
    {
        var path = Repository("factions.fallout.example.json");
        Assert.True(File.Exists(path), $"The shipped template is missing: {path}");

        var loaded = FactionsFile.Load(path);

        Assert.Empty(loaded.Problems);
        Assert.NotNull(loaded.Set);
        Assert.Contains("NCR", loaded.Set!.Names, StringComparer.OrdinalIgnoreCase);

        static IEnumerable<string> FilesOf(FactionSet set) => set.All.Values
            .SelectMany(f => f.RankFiles.Values.Concat(f.Subclasses.Values).Append(f.SpawnFile));

        var builtIn = FilesOf(FactionRegistry.Default).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var themed = FilesOf(loaded.Set).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(themed.Intersect(builtIn, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Walk up from the test binary to the repository root.</summary>
    private static string Repository(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, file)))
            directory = directory.Parent;

        return Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, file);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
