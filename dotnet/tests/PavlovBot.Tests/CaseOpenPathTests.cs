using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Cases;
using PavlovBot.Core.Data;
using PavlovBot.Core.Intelligence;
using PavlovBot.Host.Cases;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Intelligence;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The whole /case open path, end to end below the Discord layer.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. Every other test here covers one piece: CaseService against a store,
/// RiskScorer against signals, redaction against a profile. All of them passed while
/// /case open threw in production on the first real invocation, because nothing exercised
/// the JOIN - a command that asks the intelligence service for a profile and hands the
/// result to the case service as seed evidence.
///
/// A SocketSlashCommand cannot be constructed, so the command class itself is not reachable
/// from a test. Everything underneath it is, and that is where the work happens. This runs
/// the exact sequence CaseCommand.OpenAsync runs, against empty datasets - which is the state
/// every one of these features is in on the deploy that introduces them, and therefore the
/// state most likely to be broken.
/// </remarks>
public class CaseOpenPathTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pavlov-caseopen-{Guid.NewGuid():N}");
    private readonly SerializedStore _store;
    private readonly PlayerIntelligenceService _intelligence;
    private readonly CaseService _cases;

    public CaseOpenPathTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());

        var metrics = new MetricsRegistry();
        var masters = new MasterNames([], _store);
        var tracking = new IpTrackingService(_store, metrics, NullLogger<IpTrackingService>.Instance);

        var options = new BotOptions
        {
            DiscordToken = "t",
            // Port 1: nothing answers, which is what an unreachable server looks like and is
            // the state a fresh deploy is in before the first probe.
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 1, Password = "x" }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        };

        var rcon = new RconRegistry(options, metrics, NullLogger<RconRegistry>.Instance);
        var bans = new BanService(rcon, _store, masters, NullLogger<BanService>.Instance);
        var warnings = new WarningService(_store, new AuditLog(_store), NullLogger<WarningService>.Instance);

        _intelligence = new PlayerIntelligenceService(
            tracking, bans, warnings, rcon, _store,
            NullLogger<PlayerIntelligenceService>.Instance,
            // Every optional dependency absent, which is what an unconfigured install has.
            rosters: null, members: null, balances: null, payroll: null, vpn: null);

        _cases = new CaseService(_store, new AuditLog(_store), NullLogger<CaseService>.Instance);
    }

    /// <summary>
    /// The exact sequence /case open runs: profile, then open with the signals as seed.
    /// </summary>
    /// <remarks>
    /// Against a player the bot has never seen and completely empty datasets - the first
    /// invocation after a deploy, and the one that failed.
    /// </remarks>
    private async Task<CaseResult> OpenAsync(string player)
    {
        var profile = await _intelligence.ProfileAsync(player, ProfileVisibility.Moderation);

        var seed = new List<(EvidenceKind, string, string)>
        {
            (EvidenceKind.Detection,
                $"Risk at opening: {profile.Risk.Score}/100 ({profile.Risk.Confidence} confidence). {profile.Risk.Assessment}",
                "risk-engine"),
        };

        seed.AddRange(profile.Risk.Signals.Select(s =>
            (EvidenceKind.Detection, $"[+{s.Weight}] {s.Summary}", "risk-engine")));

        return await _cases.OpenAsync(player, "possible ban evasion", "ModOne", seed);
    }

    [Fact]
    public async Task OpeningACaseOnAnUnknownPlayerWithEmptyDatasetsSucceeds()
    {
        var result = await OpenAsync("Nobody");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("Nobody", result.Case!.Subject);
        Assert.NotEmpty(result.Case.Evidence);
        Assert.True(CaseService.Verify(result.Case).Intact);
    }

    /// <summary>Profiling alone must not throw on a completely empty install.</summary>
    /// <remarks>
    /// Isolated from the case path so a failure says WHICH half broke. The intelligence
    /// service touches eight subsystems and any one of them can be unconfigured.
    /// </remarks>
    [Fact]
    public async Task ProfilingAnUnknownPlayerOnAnEmptyInstallDoesNotThrow()
    {
        var profile = await _intelligence.ProfileAsync("Nobody", ProfileVisibility.Moderation);

        Assert.False(profile.Known);
        Assert.Equal(0, profile.Risk.Score);
    }

    /// <summary>And with data present, which exercises the paths the empty case skips.</summary>
    [Fact]
    public async Task OpeningACaseOnAKnownPlayerSucceeds()
    {
        await _store.WriteAsync(Datasets.Playtime, new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Evader"] = new("Evader", 600, DateTimeOffset.UtcNow.AddDays(-1)),
        });

        var result = await OpenAsync("Evader");

        Assert.True(result.Ok, result.Error);
    }

    /// <summary>A second case gets its own number and its own chain.</summary>
    [Fact]
    public async Task TwoCasesOnOnePlayerAreIndependent()
    {
        var first = await OpenAsync("Evader");
        var second = await OpenAsync("Evader");

        Assert.True(first.Ok, first.Error);
        Assert.True(second.Ok, second.Error);
        Assert.NotEqual(first.Case!.Id, second.Case!.Id);
        Assert.True(CaseService.Verify(second.Case).Intact);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
