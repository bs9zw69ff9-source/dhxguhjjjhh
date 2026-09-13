using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Stats;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The kill block a live Pavlov server actually writes, line for line.
/// </summary>
/// <remarks>
/// THE SHAPE THE TESTS DID NOT HAVE. The only multi-line coverage fed two lines with the
/// weapon on the same line as the victim, which is not what the game produces:
///
///   "KillData":
///   {
///           "Killer": "afrobrofosho",
///           "Killed": "Swagthingchunk32",
///           "KilledBy": "obrez",
///           "Headshot": false
///   }
///
/// Four lines, one field each. The assembler reported on the "Killed" line, so every record
/// fired a line early with no weapon - and the orphaned "KilledBy" was held and merged into
/// the NEXT kill, which meant every death after the first was attributed to the previous
/// death's gun. Nothing failed, nothing logged, and the kill feed was wrong all day.
/// </remarks>
public class KillDataBlockTests
{
    private const string File = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";

    private readonly IpTrackingService _tracking =
        new(new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec()),
            new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);

    private readonly List<KillEvent> _raised = [];

    public KillDataBlockTests() => _tracking.Kill += k => { _raised.Add(k); return Task.CompletedTask; };

    /// <summary>One KillData block, exactly as the game writes it.</summary>
    private async Task BlockAsync(string killer, string killed, string weapon, bool headshot = false)
    {
        foreach (var line in new[]
        {
            "   \"KillData\":",
            "   {",
            $"           \"Killer\": \"{killer}\",",
            $"           \"Killed\": \"{killed}\",",
            $"           \"KilledBy\": \"{weapon}\",",
            $"           \"Headshot\": {headshot.ToString().ToLowerInvariant()}",
            "   }",
            "}",
        })
        {
            await _tracking.IngestAsync(new LogLine(File, line));
        }
    }

    [Fact]
    public async Task OneBlockIsOneKillWithItsOwnWeapon()
    {
        await BlockAsync("afrobrofosho", "Swagthingchunk32", "obrez");

        var kill = Assert.Single(_raised);
        Assert.Equal("afrobrofosho", kill.Killer);
        Assert.Equal("Swagthingchunk32", kill.Killed);
        Assert.Equal("obrez", kill.KilledBy);
    }

    [Fact]
    public async Task TheSecondKillIsNotAttributedToTheFirstsWeapon()
    {
        /* THE BUG, in the order the log writes it. The leftover "KilledBy" from one block was
           merged into the next, so a server where somebody switched weapons reported the old
           gun for the rest of the round. */
        await BlockAsync("kingofthepirats", "afrobrofosho", "nv_laserrifle");
        await BlockAsync("robbie1220", "lifelessB", "service_rifle");
        await BlockAsync("sweet_tea_is_good", "hssny313", "m60");

        Assert.Equal(3, _raised.Count);
        Assert.Equal(["nv_laserrifle", "service_rifle", "m60"], _raised.Select(k => k.KilledBy));
        Assert.Equal(["afrobrofosho", "lifelessB", "hssny313"], _raised.Select(k => k.Killed));
    }

    [Fact]
    public async Task ASuicideBlockIsStillOneRecord()
    {
        // Most of a real log is these: the same name on both sides, KilledBy "None".
        await BlockAsync("mrcat76o", "mrcat76o", "None");

        var kill = Assert.Single(_raised);
        Assert.Equal(kill.Killer, kill.Killed);
    }

    [Fact]
    public async Task ABlockWithNoWeaponIsNotSwallowedByTheNextOne()
    {
        /* Held rather than reported, until the next block's Killer line proves the old one
           is finished. Without that, a log that omits KilledBy would lose every kill. */
        await _tracking.IngestAsync(new LogLine(File, """        "Killer": "Alice","""));
        await _tracking.IngestAsync(new LogLine(File, """        "Killed": "Bob","""));
        Assert.Empty(_raised);

        await BlockAsync("Carol", "Dave", "de");

        Assert.Equal(2, _raised.Count);
        Assert.Equal("Bob", _raised[0].Killed);
        Assert.Null(_raised[0].KilledBy);
        Assert.Equal("Dave", _raised[1].Killed);
        Assert.Equal("de", _raised[1].KilledBy);
    }

    [Fact]
    public async Task AWholeRoundIsCountedCorrectlyEndToEnd()
    {
        /* Through the bridge and into the counter, which is what /stats reads. A kill for the
           killer, a death for the victim, and a suicide crediting nobody. */
        var stats = new KillStats(new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec()),
            NullLogger<KillStats>.Instance);

        var servers = new ServerLabels();
        servers.Assign([File]);

        _ = new FeedBridge(_tracking, new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, new MetricsRegistry()),
            servers, NullLogger<FeedBridge>.Instance, killStats: stats);

        await BlockAsync("Smokythabear45", "mrcat76o", "de", headshot: true);
        await BlockAsync("Smokythabear45", "robbie1220", "de");
        await BlockAsync("mrcat76o", "Smokythabear45", "m14");
        await BlockAsync("lifelessB", "lifelessB", "None");           // a suicide

        Assert.Equal(2, stats.Of("Smokythabear45").Kills);
        Assert.Equal(1, stats.Of("Smokythabear45").Deaths);
        Assert.Equal(1, stats.Of("mrcat76o").Kills);
        Assert.Equal(1, stats.Of("mrcat76o").Deaths);

        var suicide = stats.Of("lifelessB");
        Assert.Equal(0, suicide.Kills);
        Assert.Equal(1, suicide.Deaths);
        Assert.Equal(1, suicide.Suicides);

        // 2 kills, 1 death: the ratio somebody actually sees in /stats.
        Assert.Equal(2.0, stats.Of("Smokythabear45").Ratio);
    }
}
