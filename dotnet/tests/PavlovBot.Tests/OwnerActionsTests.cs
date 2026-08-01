using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

public class OwnerActionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-owner-" + Guid.NewGuid().ToString("N"));
    private readonly string _ledgers;
    private readonly SerializedStore _store;
    private readonly IpTrackingService _tracking;
    private readonly OwnerActions _actions;

    public OwnerActionsTests()
    {
        _ledgers = Path.Combine(_directory, "modsave");
        Directory.CreateDirectory(_ledgers);

        _store = new SerializedStore(new FileKeyValueBackend(Path.Combine(_directory, "data")), new SystemTextJsonCodec());
        _tracking = new IpTrackingService(_store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);
        _actions = new OwnerActions(_store, _tracking, _ledgers);
    }

    private Task SeedAccountsAsync(params AccountRecord[] accounts) =>
        _store.WriteAsync(Datasets.KnownPlayers,
            accounts.ToDictionary(a => a.Id, a => a, StringComparer.OrdinalIgnoreCase));

    // ---- blacklisting ----

    [Fact]
    public async Task AnAddressAndANameAreToldApartWithoutBeingAsked()
    {
        await _actions.BlacklistAsync("203.0.113.9");
        await _actions.BlacklistAsync("CheaterMcGee");

        var flags = _tracking.LoadFlags();
        Assert.Contains("203.0.113.9", flags.ManualIps);
        Assert.Contains("CheaterMcGee", flags.Names);

        // The name must NOT have been filed as an address, or nothing would ever match it.
        Assert.DoesNotContain("CheaterMcGee", flags.ManualIps);
    }

    [Fact]
    public async Task BlacklistingAnAddressNamesTheAccountsItAlreadyMatches()
    {
        /* "Blacklisted, and these two accounts are on it" is a different instruction to the
           owner than "blacklisted" - it tells them there is someone to ban right now. */
        await SeedAccountsAsync(
            new AccountRecord("76561198000000001", ["203.0.113.9"], [], ["Alice"]),
            new AccountRecord("76561198000000002", ["203.0.113.9"], [], ["Bob"]),
            new AccountRecord("76561198000000003", ["198.51.100.1"], [], ["Carol"]));

        var result = await _actions.BlacklistAsync("203.0.113.9");

        Assert.True(result.Ok);
        Assert.Equal(2, result.Lines.Count);
        Assert.Contains(result.Lines, l => l.Contains("Alice", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.Contains("Carol", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BlacklistingIsIdempotent()
    {
        await _actions.BlacklistAsync("203.0.113.9");
        await _actions.BlacklistAsync("203.0.113.9");

        Assert.Single(_tracking.LoadFlags().ManualIps);
    }

    [Fact]
    public async Task AnEmptyTargetIsRefusedRatherThanFlaggingNothing()
    {
        var result = await _actions.BlacklistAsync("   ");

        Assert.False(result.Ok);
        Assert.Empty(_tracking.LoadFlags().ManualIps);
    }

    // ---- alts ----

    [Fact]
    public async Task AltsComeFromConfirmedAddressesOnly()
    {
        /* A guessed address is a correlation between a join line and a nearby address line;
           on a busy server two unrelated players connecting in the same second can be
           correlated to each other. Treating that as "same person" produces confident,
           wrong accusations the accused cannot disprove. */
        await SeedAccountsAsync(
            new AccountRecord("1", ConfirmedIps: ["203.0.113.9"], GuessedIps: ["198.51.100.1"], Names: ["Alice"]),
            new AccountRecord("2", ConfirmedIps: ["203.0.113.9"], GuessedIps: [], Names: ["RealAlt"]),
            new AccountRecord("3", ConfirmedIps: [], GuessedIps: ["198.51.100.1"], Names: ["Innocent"]));

        var result = _actions.Alts("Alice");

        Assert.Single(result.Lines);
        Assert.Contains(result.Lines, l => l.Contains("RealAlt", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.Contains("Innocent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAccountWithNoConfirmedAddressHasNoAlts()
    {
        // Otherwise every untracked account would "share" the empty set with every other.
        await SeedAccountsAsync(
            new AccountRecord("1", [], [], ["Alice"]),
            new AccountRecord("2", [], [], ["Bob"]));

        Assert.Empty(_actions.Alts("Alice").Lines);
    }

    // ---- barring discord users ----

    [Fact]
    public async Task BarringWritesTheNodeArrayFormat()
    {
        await _actions.BarUserAsync("1014251293159731310");

        // An ARRAY of id strings - what the Node bot reads. An object here breaks it.
        Assert.Equal(["1014251293159731310"], _store.Read(Datasets.UserBlacklist, new List<string>()));
    }

    [Fact]
    public async Task AMentionIsAcceptedAsWellAsABareId()
    {
        await _actions.BarUserAsync("<@1014251293159731310>");

        Assert.Contains("1014251293159731310", _store.Read(Datasets.UserBlacklist, new List<string>()));
    }

    [Fact]
    public async Task SomethingThatIsNotAnIdIsRefused()
    {
        var result = await _actions.BarUserAsync("CheaterMcGee");

        Assert.False(result.Ok);
        Assert.Empty(_store.Read(Datasets.UserBlacklist, new List<string>()));
    }

    [Fact]
    public async Task UnbarringRecordsAnExplicitOverride()
    {
        /* BLACKLIST_IDS in .env also feeds the bar list. Merely removing the id would let it
           come straight back on the next restart with no way to override it. */
        await _actions.BarUserAsync("1014251293159731310");
        await _actions.UnbarUserAsync("1014251293159731310");

        Assert.Empty(_store.Read(Datasets.UserBlacklist, new List<string>()));
        Assert.Contains("1014251293159731310", _store.Read(Datasets.UserUnbarred, new List<string>()));
    }

    [Fact]
    public async Task BarringClearsAStaleUnbarOverride()
    {
        // An un-bar outranks the bar list, so leaving it would make this silently do nothing.
        await _actions.UnbarUserAsync("1014251293159731310");
        await _actions.BarUserAsync("1014251293159731310");

        Assert.Empty(_store.Read(Datasets.UserUnbarred, new List<string>()));
        Assert.Contains("1014251293159731310", _store.Read(Datasets.UserBlacklist, new List<string>()));
    }

    // ---- ignore list ----

    [Fact]
    public async Task IgnoringSurvivesARestart()
    {
        /* The tracker's copy is in memory. If the persisted list is not reloaded at startup,
           it exists in storage while doing nothing - and the bot quietly resumes recording
           addresses for the accounts somebody asked it not to. */
        await _actions.IgnoreAsync("OwnerAlt");

        var freshTracking = new IpTrackingService(_store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);
        Assert.DoesNotContain("OwnerAlt", freshTracking.Untracked);

        new OwnerActions(_store, freshTracking, _ledgers).RestoreIgnoreList();
        Assert.Contains("OwnerAlt", freshTracking.Untracked);
    }

    [Fact]
    public async Task UnignoringTakesEffectImmediately()
    {
        await _actions.IgnoreAsync("OwnerAlt");
        await _actions.UnignoreAsync("OwnerAlt");

        Assert.DoesNotContain("OwnerAlt", _tracking.Untracked);
        Assert.Empty(_actions.IgnoredNames().Lines);
    }

    // ---- whitelists ----

    [Fact]
    public async Task RestoringWithNoSnapshotIsRefusedRatherThanClearing()
    {
        /* The dangerous version of this bug: "restore" on an empty snapshot writing empty
           maps over the live ranks, wiping every whitelist rank in one click. */
        await _store.WriteAsync(Datasets.FactionRanks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = "Capo" });

        var result = await _actions.LoadWhitelistsAsync();

        Assert.False(result.Ok);
        Assert.Equal("Capo", _store.Read(Datasets.FactionRanks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))["Alice"]);
    }

    [Fact]
    public async Task ASnapshotRoundTrips()
    {
        await _store.WriteAsync(Datasets.FactionRanks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = "Capo" });
        await _actions.SaveWhitelistsAsync();

        await _store.WriteAsync(Datasets.FactionRanks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Alice"] = "Soldier" });

        var result = await _actions.LoadWhitelistsAsync();

        Assert.True(result.Ok);
        Assert.Equal("Capo", _store.Read(Datasets.FactionRanks,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))["Alice"]);
    }

    // ---- wipes ----

    [Fact]
    public void WipingMoneyDeletesLedgersAndSaysHowMany()
    {
        File.WriteAllText(Path.Combine(_ledgers, "player1.txt"), "500");
        File.WriteAllText(Path.Combine(_ledgers, "player2.txt"), "900");

        var result = _actions.WipeMoney();

        Assert.True(result.Ok);
        Assert.Contains("**2**", result.Detail, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_ledgers, "*.txt"));
    }

    [Fact]
    public void AnUnconfiguredLedgerPathIsRefused_NotReportedAsASuccessfulWipe()
    {
        /* "Wiped 0 ledgers" on a wrong MODSAVE_PATH would look like a completed wipe
           forever, and the owner would never learn the path was wrong. */
        var result = new OwnerActions(_store, _tracking, ledgerDirectory: null).WipeMoney();

        Assert.False(result.Ok);
        Assert.Contains("MODSAVE_PATH", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WipingPlayerDataLeavesBansAlone()
    {
        await SeedAccountsAsync(new AccountRecord("1", ["203.0.113.9"], [], ["Alice"]));
        await _actions.BlacklistAsync("CheaterMcGee");

        await _actions.WipePlayerDataAsync();

        Assert.Empty(_store.Read(Datasets.KnownPlayers,
            new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase)));

        // Flags are a ban mechanism, not player data. Clearing them here would quietly
        // un-blacklist everybody.
        Assert.Contains("CheaterMcGee", _tracking.LoadFlags().Names);
    }

    [Fact]
    public async Task ClearingTemporaryBansKeepsPermanentOnes()
    {
        await _store.WriteAsync(Datasets.TempBans, new List<BanRecord>
        {
            new() { PlayerId = "temp", Permanent = false },
            new() { PlayerId = "perm", Permanent = true },
        });

        var result = await _actions.ClearTempBansAsync();

        var remaining = _store.Read<List<BanRecord>>(Datasets.TempBans, []);
        Assert.Single(remaining);
        Assert.Equal("perm", remaining[0].PlayerId);
        Assert.Contains("**1**", result.Detail, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
