using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Logs;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The three situations a naive "read from where I stopped" tailer gets wrong. All three
/// lose data SILENTLY - the tailer keeps reporting healthy and simply sees nothing.
/// </summary>
public class LogTailerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-tail-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LogTailerTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "Pavlov.log");
    }

    private static LogTailer New(long cap = 50L * 1024 * 1024) => new(NullLogger.Instance, cap);

    private void Append(string text) => File.AppendAllText(_path, text);

    [Fact]
    public void TheFirstPassStartsAtTheEnd_NotTheBeginning()
    {
        /* Starting at zero would re-announce every join in the file. On a restart during an
           incident that is thousands of feed messages about players who left days ago. */
        Append("[2026.07.15-12.00.00:000]old line\n");
        var tailer = New();

        Assert.Empty(tailer.Poll(_path));

        Append("[2026.07.15-12.00.01:000]new line\n");
        Assert.Single(tailer.Poll(_path));
    }

    [Fact]
    public void ANewLogThatOutgrewTheOldOffsetIsReadFromItsStart()
    {
        /* A restart with a SMALL old log: by the next poll the new one is already longer than
           the old offset, so "it shrank" never fires. The tailer used to seek past the new
           file's first lines - the joins right after the restart - and never read them. */
        Append("Log file open, 07/15/26 12:00:00\nold\n");
        var tailer = New();
        tailer.Poll(_path);

        File.WriteAllText(_path, "Log file open, 07/15/26 13:30:00\n" +
            string.Concat(Enumerable.Range(1, 20).Select(i => $"join {i}\n")));

        var lines = tailer.Poll(_path).Select(l => l.Text).ToList();

        Assert.Contains("join 1", lines);
        Assert.Contains("join 20", lines);
    }

    [Fact]
    public void AGrowingLogIsNotMistakenForARotation()
    {
        Append("Log file open, 07/15/26 12:00:00\n");
        var tailer = New();
        tailer.Poll(_path);

        Append("a\n");
        Assert.Equal(["a"], tailer.Poll(_path).Select(l => l.Text));
        Append("b\n");
        Assert.Equal(["b"], tailer.Poll(_path).Select(l => l.Text));
    }

    [Fact]
    public void AMultiByteCharacterSplitAcrossPollsSurvives()
    {
        // "é" is two bytes in UTF-8. Split between two reads it used to decode as two
        // replacement characters, and a name with one in it stopped matching its flags.
        Append("start\n");
        var tailer = New();
        tailer.Poll(_path);

        var bytes = System.Text.Encoding.UTF8.GetBytes("René joined\n");
        using (var s = new FileStream(_path, FileMode.Append)) s.Write(bytes, 0, 4);   // ends mid-"é"
        tailer.Poll(_path);
        using (var s = new FileStream(_path, FileMode.Append)) s.Write(bytes, 4, bytes.Length - 4);

        Assert.Equal(["René joined"], tailer.Poll(_path).Select(l => l.Text));
    }

    [Fact]
    public void OnlyNewLinesComeBackOnEachPoll()
    {
        Append("first\n");
        var tailer = New();
        tailer.Poll(_path);

        Append("second\nthird\n");
        Assert.Equal(["second", "third"], tailer.Poll(_path).Select(l => l.Text));
        Assert.Empty(tailer.Poll(_path));
    }

    [Fact]
    public void APartialLineIsHeldUntilItsNewlineArrives()
    {
        /* A poll can land mid-write. Consuming the fragment means the parser sees half a
           line and the other half never arrives separately - so a join whose write was
           split is never seen at all. */
        Append("complete\n");
        var tailer = New();
        tailer.Poll(_path);

        Append("half a li");
        Assert.Empty(tailer.Poll(_path));

        Append("ne here\n");
        Assert.Equal(["half a line here"], tailer.Poll(_path).Select(l => l.Text));
    }

    [Fact]
    public void ARotatedLogIsReReadFromTheStart()
    {
        /* Pavlov renames the log and starts a new one. A naive tailer keeps its old offset,
           seeks past the end of the SHORTER new file, and reads nothing forever while
           appearing perfectly healthy. */
        Append(new string('x', 500) + "\n");
        var tailer = New();
        tailer.Poll(_path);

        File.WriteAllText(_path, "line after rotation\n");
        Assert.Equal(["line after rotation"], tailer.Poll(_path).Select(l => l.Text));
    }

    [Fact]
    public void CarriageReturnsAreStripped()
    {
        Append("a\n");
        var tailer = New();
        tailer.Poll(_path);

        Append("windows line\r\n");
        Assert.Equal(["windows line"], tailer.Poll(_path).Select(l => l.Text));
    }

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        // The server may not have started yet, or the path may be wrong. Neither is a
        // reason to throw on a poll that runs every 1.5 seconds forever.
        Assert.Empty(New().Poll(Path.Combine(_directory, "nope.log")));
    }

    [Fact]
    public void AHugeBackfillIsCapped()
    {
        /* A server that ran for months has a log measured in gigabytes. Reading all of it
           at startup would stall the bot and re-announce every join that ever happened. */
        Append(new string('a', 4096) + "\n" + "the tail line\n");

        var tailer = New(cap: 64);
        var lines = tailer.Poll(_path, fromStart: true);

        Assert.Contains(lines, l => l.Text == "the tail line");
        Assert.DoesNotContain(lines, l => l.Text.Length > 1000);
    }

    [Fact]
    public void ReadingFromTheStartSeesEverythingUnderTheCap()
    {
        Append("one\ntwo\nthree\n");
        Assert.Equal(["one", "two", "three"], New().Poll(_path, fromStart: true).Select(l => l.Text));
    }

    [Fact]
    public void EachLineKnowsWhichFileItCameFrom()
    {
        // Two servers write two logs, and correlation is per file.
        Append("x\n");
        Assert.Equal(_path, New().Poll(_path, fromStart: true)[0].File);
    }

    [Fact]
    public void AConfiguredPathThatDoesNotExistYieldsNothing()
    {
        // A wrong guess is worse than none: a tailer pointed at a stale log reads nothing
        // forever, which is indistinguishable from a quiet server.
        Assert.Empty(LogTailer.Discover("/no/such/file.log", NullLogger.Instance));
    }

    [Fact]
    public void ConfiguredPathsAreCommaSeparatedAndFiltered()
    {
        Append("x\n");
        var found = LogTailer.Discover($"{_path}, /no/such/file.log", NullLogger.Instance);
        Assert.Equal([_path], found);
    }

    [Fact]
    public void AutoDiscoveryFindsALogUnderEveryInstall()
    {
        /* THE MULTI-SERVER BUG. The old auto-detection was a hardcoded list that named
           pavlovserver and pavlovserver2 but not pavlovserver1, so a three-server box tailed
           at most two of them and the middle server was invisible. Discovery now takes the
           install roots and finds one Pavlov.log under each. */
        var roots = new List<string>();
        foreach (var name in new[] { "pavlovserver", "pavlovserver1", "pavlovserver2" })
        {
            var root = Path.Combine(_directory, name);
            var log = Path.Combine(root, "Pavlov", "Saved", "Logs", "Pavlov.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "x\n");
            roots.Add(root);
        }

        var found = LogTailer.Discover(configured: null, logger: NullLogger.Instance, installRoots: roots);

        Assert.Equal(3, found.Count);
        foreach (var root in roots)
            Assert.Contains(Path.Combine(root, "Pavlov", "Saved", "Logs", "Pavlov.log"), found, StringComparer.Ordinal);
    }

    [Fact]
    public void AnExplicitConfiguredPathStillWinsOverInstallDiscovery()
    {
        // Someone who set PAVLOV_LOGS meant it: the install roots are ignored when a path is given.
        Append("x\n");
        var roots = new[] { Path.Combine(_directory, "pavlovserver1") };

        var found = LogTailer.Discover(_path, NullLogger.Instance, installRoots: roots);

        Assert.Equal([_path], found);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
