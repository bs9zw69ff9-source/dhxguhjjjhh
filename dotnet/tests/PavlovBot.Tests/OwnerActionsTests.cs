using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Servers;
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

    // ---- firewall (manual address blocks only) ----

    private sealed class FakeFirewall : IFirewall
    {
        public List<string> Denied { get; } = [];
        public List<string> Undenied { get; } = [];
        public bool Fail { get; init; }

        public Task<FirewallResult> DenyAsync(string ip, CancellationToken ct = default)
        {
            Denied.Add(ip);
            return Task.FromResult(new FirewallResult(!Fail, Fail ? "ufw not found" : "ok"));
        }

        public Task<FirewallResult> UndenyAsync(string ip, CancellationToken ct = default)
        {
            Undenied.Add(ip);
            return Task.FromResult(new FirewallResult(!Fail, Fail ? "ufw not found" : "ok"));
        }

        public Task<FirewallResult> StatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new FirewallResult(!Fail, "Status: active"));
    }

    private OwnerActions WithFirewall(FakeFirewall firewall) =>
        new(_store, _tracking, _ledgers, firewall: firewall);

    [Fact]
    public async Task BlacklistingAnAddressAlsoDeniesItAtTheFirewall()
    {
        var firewall = new FakeFirewall();
        var result = await WithFirewall(firewall).BlacklistAsync("203.0.113.9");

        Assert.Equal("203.0.113.9", Assert.Single(firewall.Denied));
        Assert.Contains("203.0.113.9", _tracking.LoadFlags().ManualIps);
        Assert.Contains("firewall", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlacklistingAUsernameNeverTouchesTheFirewall()
    {
        // A username is not an address. Handing "CheaterMcGee" to ufw would be meaningless
        // at best; the firewall is only ever driven by a parsed address.
        var firewall = new FakeFirewall();
        await WithFirewall(firewall).BlacklistAsync("CheaterMcGee");

        Assert.Empty(firewall.Denied);
    }

    [Fact]
    public async Task ClearingAManualAddressRemovesTheFirewallRule()
    {
        var firewall = new FakeFirewall();
        var actions = WithFirewall(firewall);

        await actions.BlacklistAsync("203.0.113.9");
        await actions.ClearAddressAsync("203.0.113.9");

        Assert.Equal("203.0.113.9", Assert.Single(firewall.Undenied));
        Assert.DoesNotContain("203.0.113.9", _tracking.LoadFlags().ManualIps);
    }

    [Fact]
    public async Task AFirewallFailureDoesNotFailTheBlacklist()
    {
        /* The flag write is the part that must survive. A box without ufw, or a bot not
           running as root, still gets the address blacklisted in the bot - the firewall is a
           bonus, reported as failed, never a reason to lose the block. */
        var firewall = new FakeFirewall { Fail = true };
        var result = await WithFirewall(firewall).BlacklistAsync("203.0.113.9");

        Assert.True(result.Ok);
        Assert.Contains("203.0.113.9", _tracking.LoadFlags().ManualIps);
        Assert.Contains("did not apply", result.Detail, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>OwnerActions wired to a real roster folder, the way the bot runs it.</summary>
    private (OwnerActions Actions, string Rosters) WithRosters()
    {
        var rosters = Path.Combine(_directory, "FactionRoles");
        Directory.CreateDirectory(rosters);
        var service = new PavlovBot.Host.Factions.RosterService(rosters, NullLogger<PavlovBot.Host.Factions.RosterService>.Instance,
            backupDirectory: Path.Combine(_directory, "roster_bak"));
        var backup = new PavlovBot.Host.Factions.WhitelistBackup(_store, service,
            NullLogger<PavlovBot.Host.Factions.WhitelistBackup>.Instance);
        return (new OwnerActions(_store, _tracking, _ledgers, whitelists: backup), rosters);
    }

    [Fact]
    public async Task ASnapshotHoldsTheRosterFilesAndRestoresThem()
    {
        /* THE BUG: save snapshotted faction_ranks/faction_config, which nothing writes, so it
           stored nothing and a restore changed nothing. It has to be the FILES. */
        var (actions, rosters) = WithRosters();
        await File.WriteAllTextAsync(Path.Combine(rosters, "ncr_trooper.txt"), "Alice\nBob\n");
        await File.WriteAllTextAsync(Path.Combine(rosters, "handmade.txt"), "Carol\n");

        var saved = await actions.SaveWhitelistsAsync();
        Assert.True(saved.Ok, saved.Detail);
        Assert.Contains("**3** name(s)", saved.Detail, StringComparison.Ordinal);

        // Wiped, as a delete-and-reprovision does.
        foreach (var file in Directory.GetFiles(rosters)) File.Delete(file);

        var loaded = await actions.LoadWhitelistsAsync();

        Assert.True(loaded.Ok, loaded.Detail);
        Assert.Equal(["Alice", "Bob"], await File.ReadAllLinesAsync(Path.Combine(rosters, "ncr_trooper.txt")));
        Assert.Equal(["Carol"], await File.ReadAllLinesAsync(Path.Combine(rosters, "handmade.txt")));
    }

    [Fact]
    public async Task EmptyRostersNeverReplaceAPopulatedSnapshot()
    {
        var (actions, rosters) = WithRosters();
        await File.WriteAllTextAsync(Path.Combine(rosters, "ncr_trooper.txt"), "Alice\n");
        Assert.True((await actions.SaveWhitelistsAsync()).Ok);

        await File.WriteAllTextAsync(Path.Combine(rosters, "ncr_trooper.txt"), "");
        var second = await actions.SaveWhitelistsAsync();

        Assert.False(second.Ok);
        Assert.True((await actions.LoadWhitelistsAsync()).Ok);
        Assert.Equal(["Alice"], await File.ReadAllLinesAsync(Path.Combine(rosters, "ncr_trooper.txt")));
    }

    [Fact]
    public async Task RestoringWithNoSnapshotIsRefusedAndTouchesNothing()
    {
        var (actions, rosters) = WithRosters();
        await File.WriteAllTextAsync(Path.Combine(rosters, "ncr_trooper.txt"), "Alice\n");

        var result = await actions.LoadWhitelistsAsync();

        Assert.False(result.Ok);
        Assert.Equal(["Alice"], await File.ReadAllLinesAsync(Path.Combine(rosters, "ncr_trooper.txt")));
    }

    [Fact]
    public async Task ANodeSnapshotOnDiskLoads()
    {
        // The dataset shape is the Node bot's: { savedAt, files }. A snapshot it wrote is the
        // one most worth restoring on a server that migrated.
        var (actions, rosters) = WithRosters();
        await File.WriteAllTextAsync(Path.Combine(_directory, "data", "faction_backup.json"),
            "{\"savedAt\":1700000000000,\"files\":{\"legion_recruit.txt\":[\"Dave\"]}}");

        var result = await actions.LoadWhitelistsAsync();

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(["Dave"], await File.ReadAllLinesAsync(Path.Combine(rosters, "legion_recruit.txt")));
    }

    [Fact]
    public async Task ASnapshotEntryThatIsAPathIsNotWritten()
    {
        var (actions, _) = WithRosters();
        await File.WriteAllTextAsync(Path.Combine(_directory, "data", "faction_backup.json"),
            "{\"savedAt\":1700000000000,\"files\":{\"../../escape.txt\":[\"x\"],\"ok.txt\":[\"y\"]}}");

        var result = await actions.LoadWhitelistsAsync();

        Assert.Contains("not a roster file name", result.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_directory, "..", "escape.txt")));
    }

    [Theory]
    [InlineData("ncr_trooper.txt", true)]
    [InlineData("../x.txt", false)]
    [InlineData("/etc/x.txt", false)]
    [InlineData("sub/x.txt", false)]
    [InlineData("x.json", false)]
    [InlineData("", false)]
    public void OnlyPlainTxtNamesAreRosterFiles(string file, bool ok) =>
        Assert.Equal(ok, PavlovBot.Host.Factions.WhitelistBackup.IsRosterFileName(file));

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

    [Fact]
    public async Task BulkClearsKeepMasterOwnerBans()
    {
        // The panel is owner-level; a super owner is an owner. Neither clear may lift what a
        // master owner banned - /unban already refuses them one at a time.
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync(Datasets.TempBans, new List<PavlovBot.Core.Moderation.BanRecord>
        {
            new() { PlayerId = "masterTemp", Expires = now.AddDays(1), Tier = PavlovBot.Core.Moderation.StaffTier.MasterOwner },
            new() { PlayerId = "masterPerm", Permanent = true, Tier = PavlovBot.Core.Moderation.StaffTier.MasterOwner },
            new() { PlayerId = "ownerPerm", Permanent = true, Tier = PavlovBot.Core.Moderation.StaffTier.SuperOwner },
            new() { PlayerId = "modTemp", Expires = now.AddDays(1), Tier = PavlovBot.Core.Moderation.StaffTier.Mod },
        });

        var temp = await _actions.ClearTempBansAsync();
        Assert.Equal(["masterTemp", "masterPerm", "ownerPerm"], Stored().Select(b => b.PlayerId));
        Assert.Contains("Master Owner", temp.Detail, StringComparison.Ordinal);

        await _actions.ClearAllBansAsync();
        Assert.Equal(["masterTemp", "masterPerm"], Stored().Select(b => b.PlayerId));
    }

    [Fact]
    public async Task ClearingTemporaryBansKeepsEveryFieldOfTheOnesItKeeps()
    {
        // It used to write the kept records back through the evasion model, which has no tier,
        // account id or ban time - stripping them from every permanent ban it kept.
        var at = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        await _store.WriteAsync(Datasets.TempBans, new List<PavlovBot.Core.Moderation.BanRecord>
        {
            new() { PlayerId = "temp", Expires = at.AddDays(1) },
            new() { PlayerId = "perm", Permanent = true, UniqueId = "acct1", At = at, Tier = PavlovBot.Core.Moderation.StaffTier.Admin },
        });

        await _actions.ClearTempBansAsync();

        var kept = Assert.Single(Stored());
        Assert.Equal("acct1", kept.UniqueId);
        Assert.Equal(at, kept.At);
        Assert.Equal(PavlovBot.Core.Moderation.StaffTier.Admin, kept.Tier);
    }

    private List<PavlovBot.Core.Moderation.BanRecord> Stored() =>
        _store.Read<List<PavlovBot.Core.Moderation.BanRecord>>(Datasets.TempBans, []);

    // ---- never-ban ----

    private (OwnerActions Actions, MasterNames Masters, List<string> Lifted) WithProtection()
    {
        var masters = new MasterNames([], _store);
        var lifted = new List<string>();
        var actions = new OwnerActions(_store, _tracking, _ledgers, masters,
            (player, _) => { lifted.Add(player); return Task.CompletedTask; });
        return (actions, masters, lifted);
    }

    [Fact]
    public async Task ProtectingAPlayerAlsoLiftsWhatIsAlreadyOnThem()
    {
        /* BOTH HALVES OR IT DOES NOTHING USEFUL. Somebody reaches for this because the ban
           has already landed; protecting them against the NEXT one while leaving the current
           record in place keeps them locked out and looks like the command failed. */
        var (actions, masters, lifted) = WithProtection();

        var result = await actions.ProtectPlayerAsync("Holosight1");

        Assert.True(result.Ok);
        Assert.True(masters.IsProtected("Holosight1"));
        Assert.Equal("Holosight1", Assert.Single(lifted));
    }

    [Fact]
    public async Task ProtectionSurvivesAFailedLiftAndSaysSo()
    {
        /* The protection is the half that stops this recurring, so it is written first and
           kept. A silent "done" over a failed lift would leave them banned with nothing
           saying why. */
        var masters = new MasterNames([], _store);
        var actions = new OwnerActions(_store, _tracking, _ledgers, masters,
            (_, _) => throw new IOException("rcon down"));

        var result = await actions.ProtectPlayerAsync("Holosight1");

        Assert.True(masters.IsProtected("Holosight1"));
        Assert.Contains("could NOT be lifted", result.Detail, StringComparison.Ordinal);
        Assert.Contains("/unban", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectionIsCaseInsensitiveAndReversible()
    {
        var (actions, masters, _) = WithProtection();

        await actions.ProtectPlayerAsync("  Holosight1  ");
        Assert.True(masters.IsProtected("holosight1"));

        Assert.Contains("**1**", actions.ProtectedPlayers().Detail, StringComparison.Ordinal);

        await actions.UnprotectPlayerAsync("HOLOSIGHT1");
        Assert.False(masters.IsProtected("Holosight1"));
        Assert.Contains("Nobody", actions.ProtectedPlayers().Detail, StringComparison.Ordinal);
    }

    // ---- the blacklist view ----

    [Fact]
    public async Task AnUnreadableFlagRowIsReportedAsUnreadable_NotAsEmpty()
    {
        /* THE SCREENSHOT THIS EXISTS FOR. An auto-ban quoted "blacklisted ip <address>" in
           the same minute this panel said nothing was blacklisted. Both read the same row, so
           one was wrong - and a row that will not deserialize comes back as an empty
           StoredFlags, identical to an absent one. Asserting "nothing is blacklisted" over it
           is what made the contradiction impossible to see. */
        await _store.WriteAsync(Datasets.IpFlags, new[] { "76561198000000042" });

        var result = _actions.ViewBlacklist();

        Assert.Contains("could not be read", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing is blacklisted", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AGenuinelyEmptyFlagRowStillReadsAsEmpty()
    {
        // The other half: no row at all is not a fault, and must not be reported as one.
        var result = _actions.ViewBlacklist();

        Assert.Contains("Nothing is blacklisted", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
