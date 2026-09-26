using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A served <c>/suspendrank</c> puts the member back at exactly their rank - never past it.
/// </summary>
public class RankRestorerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rank-restore-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store = new(new MemoryBackend(), new SystemTextJsonCodec());
    private readonly RosterService _rosters;
    private readonly FactionDefinition _nypd = FactionRegistry.Police.Get("NYPD")!;

    public RankRestorerTests()
    {
        Directory.CreateDirectory(_root);
        _rosters = new RosterService(_root, NullLogger<RosterService>.Instance, factions: FactionRegistry.Police);
        _rosters.EnsureRosterFiles();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RankRestorer Restorer() => new(_store, _rosters, NullLogger.Instance);

    private Task Suspend(string player, string restoreTo, DateTimeOffset until) =>
        _store.UpdateAsync(Datasets.RankSuspensions,
            new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase),
            s => { s[player] = new RankSuspension(player, "NYPD", restoreTo, "admin", until); return s; });

    private IReadOnlyDictionary<string, RankSuspension> Suspensions() =>
        _store.Read(Datasets.RankSuspensions, new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase));

    private async Task<string?> RankOf(string player) => (await _rosters.FindAsync(player))?.Rank;

    private async Task PutAt(string player, string rank)
    {
        await _rosters.JoinAsync(_nypd, player);
        if (!string.Equals(rank, _nypd.Default, StringComparison.Ordinal)) await _rosters.SetRankAsync(_nypd, player, rank);
    }

    [Fact]
    public async Task ARemovedMemberComesBackAtExactlyTheirRank()
    {
        await Suspend("Alice", "Sergeant", Now.AddMinutes(-1));

        Assert.Equal(1, await Restorer().RestoreExpiredAsync(Now));

        Assert.Equal("Sergeant", await RankOf("Alice"));
        Assert.Empty(Suspensions());
    }

    [Fact]
    public async Task AMemberReWhitelistedDuringTheSuspensionIsNotPromotedPastTheirRank()
    {
        /* The over-promotion: re-added at Corporal by staff, the old restore still promoted them
           Cadet->Sergeant's worth of steps FROM Corporal, landing on Lieutenant. */
        await PutAt("Alice", "Corporal");
        await Suspend("Alice", "Sergeant", Now.AddMinutes(-1));

        await Restorer().RestoreExpiredAsync(Now);

        Assert.Equal("Sergeant", await RankOf("Alice"));
    }

    [Fact]
    public async Task AMemberAlreadyAboveTheirRankIsLeftAlone()
    {
        await PutAt("Alice", "Captain");
        await Suspend("Alice", "Sergeant", Now.AddMinutes(-1));

        await Restorer().RestoreExpiredAsync(Now);

        Assert.Equal("Captain", await RankOf("Alice"));
        Assert.Empty(Suspensions());
    }

    [Fact]
    public async Task RunningAgainChangesNothing()
    {
        // The replay: a suspension left in place used to be restored again every 30 seconds.
        await Suspend("Alice", "Sergeant", Now.AddMinutes(-1));

        await Restorer().RestoreExpiredAsync(Now);
        await Restorer().RestoreExpiredAsync(Now);
        await Restorer().RestoreExpiredAsync(Now);

        Assert.Equal("Sergeant", await RankOf("Alice"));
    }

    [Fact]
    public async Task AMemberWhoJoinedAnotherFactionIsNotReportedRestored()
    {
        await _rosters.JoinAsync(FactionRegistry.Police.Get("Gambino")!, "Alice");
        await Suspend("Alice", "Sergeant", Now.AddMinutes(-1));

        await Restorer().RestoreExpiredAsync(Now);

        Assert.Equal("Gambino", (await _rosters.FindAsync("Alice"))!.Faction.Name);
        Assert.Empty(Suspensions());   // settled: there is nothing to retry
    }

    [Fact]
    public async Task ASuspensionStillRunningIsUntouched()
    {
        await Suspend("Alice", "Sergeant", Now.AddHours(1));

        Assert.Equal(0, await Restorer().RestoreExpiredAsync(Now));

        Assert.Null(await RankOf("Alice"));
        Assert.Single(Suspensions());
    }
}
