using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Lifting a ban, against the wire.
/// </summary>
/// <remarks>
/// TWO REPORTED FAILURES, one root cause each, and they stacked into "unbans do not apply and
/// they are re-banned on join".
///
///   THE UNBAN NAMED THE WRONG THING. Pavlov's Ban and Unban take a UniqueId; a display name
///   is accepted, answers normally, and does nothing. Enforcement already preferred the id,
///   but BanRecord only ever remembered the display name - so the ban landed on an EOS id and
///   the lift sent the name. The expiry sweep did not even try to look one up. Every temp ban
///   that ran out stayed natively banned forever, with a log line saying it had been lifted.
///
///   THE EVIDENCE OUTLIVED THE BAN. A ban flags the player's address and account id. The
///   expiry path never cleared them, and the one-hour exemption that covered the gap was
///   sized against a "clean-up sweep" referenced in three files and implemented in none. An
///   hour later they connect, the evasion responder sees a flagged join with no covering ban,
///   and issues a fresh PERMANENT one.
///
/// ASSERTED AGAINST FakeRconServer, which records the exact command bytes. A test that read
/// the format string would have passed against both bugs: the code always did send an Unban.
/// </remarks>
public class BanLiftTests : IAsyncDisposable
{
    private const string Name = "Evader";
    private const string Account = "0002a1b7c3d4e5f60718293a4b5c6d7e";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "pavlovbot-lift-" + Guid.NewGuid().ToString("N"));

    private readonly SerializedStore _store;
    private readonly FakeRconServer _server = new();

    public BanLiftTests() =>
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());

    /// <summary>Records what was asked of it, so a lift that skips a step is visible.</summary>
    private sealed class FakeEvidence : IBanEvidence
    {
        private readonly Dictionary<string, string> _accounts;

        public FakeEvidence(params (string Name, string Id)[] known) =>
            _accounts = known.ToDictionary(k => k.Name, k => k.Id, StringComparer.OrdinalIgnoreCase);

        public List<string> Cleared { get; } = [];

        public string? AccountIdFor(string name) => _accounts.GetValueOrDefault(name);

        public Task ClearFlagsAsync(string accountId, CancellationToken ct = default)
        {
            Cleared.Add(accountId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Records exemptions rather than storing them, and never protects anybody.</summary>
    private sealed class RecordingMasters : IMasterNames
    {
        public List<string> Exempted { get; } = [];

        public bool IsMaster(string name) => false;
        public bool IsExempt(string name) => false;

        public Task ExemptAsync(string name, TimeSpan? duration = null, CancellationToken ct = default)
        {
            Exempted.Add(name);
            return Task.CompletedTask;
        }
    }

    /// <summary>The game's ban file, as ModsaveBanlist would rewrite it.</summary>
    private sealed class RecordingBanFile : IBanFileExport
    {
        public int Exports { get; private set; }
        public bool Throw { get; set; }

        public Task<int> ExportAsync(CancellationToken ct = default)
        {
            if (Throw) throw new IOException("the ban file path is wrong");
            Exports++;
            return Task.FromResult(0);
        }
    }

    private BanService Build(RecordingMasters masters, IBanEvidence? evidence, TimeProvider? time = null,
        IBanFileExport? banFile = null)
    {
        var options = new BotOptions
        {
            DiscordToken = "t",
            Servers =
            [
                new RconOptions
                {
                    Name = "server1",
                    Host = "127.0.0.1",
                    Port = _server.Port,
                    Password = _server.Password,
                    CommandSpacing = TimeSpan.Zero,
                    ReadCacheDuration = TimeSpan.Zero,
                    MaxAttempts = 1,
                },
            ],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        };

        var rcon = new RconRegistry(options, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);
        return new BanService(rcon, _store, masters, NullLogger<BanService>.Instance, time, evidence, banFile);
    }

    private Task Seed(BanRecord record) =>
        _store.WriteAsync(Datasets.TempBans, new List<BanRecord> { record });

    private List<BanRecord> Bans() => _store.Read<List<BanRecord>>(Datasets.TempBans, []);

    private static BanRecord TempBan(DateTimeOffset expires, string? uniqueId) => new()
    {
        PlayerId = Name,
        UniqueId = uniqueId,
        Reason = "griefing",
        Moderator = "mod",
        At = expires - TimeSpan.FromDays(2),
        Expires = expires,
        Permanent = false,
        DurationLabel = "2d",
    };

    /// <summary>
    /// An expired temp ban is lifted against the ACCOUNT ID it was banned under.
    /// </summary>
    /// <remarks>
    /// The exact bug. The old expiry path called UnbanEverywhereAsync(ban.PlayerId) with no id
    /// argument at all, so this asserted "Unban Evader" against a ban issued as
    /// "Ban 0002a1...". The server takes it, answers, and lifts nothing.
    /// </remarks>
    [Fact]
    public async Task AnExpiredTempBanIsLiftedByAccountIdNotDisplayName()
    {
        var now = DateTimeOffset.UtcNow;
        await Seed(TempBan(now - TimeSpan.FromMinutes(5), Account));

        var service = Build(new RecordingMasters(), new FakeEvidence());
        var lifted = await service.ProcessExpiredAsync();

        Assert.Single(lifted);
        Assert.Contains($"Unban {Account}", _server.Commands, StringComparer.Ordinal);
        Assert.DoesNotContain($"Unban {Name}", _server.Commands, StringComparer.Ordinal);
    }

    /// <summary>
    /// A record with no id falls back to LOOKING ONE UP, not to the display name.
    /// </summary>
    /// <remarks>
    /// Every ban already on a live server predates BanRecord.UniqueId, so the fallback is not
    /// a corner case - it is what happens to the entire existing ban list on the deploy that
    /// ships this.
    /// </remarks>
    [Fact]
    public async Task ARecordWithNoIdIsLiftedByTheIdTheTrackerKnows()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), uniqueId: null));

        var service = Build(new RecordingMasters(), new FakeEvidence((Name, Account)));
        await service.ProcessExpiredAsync();

        Assert.Contains($"Unban {Account}", _server.Commands, StringComparer.Ordinal);
    }

    /// <summary>
    /// Lifting clears the evasion flags, or the responder re-bans them on their next join.
    /// </summary>
    /// <remarks>
    /// The second half of the report. The expiry sweep removed the record and left the flags,
    /// so a served ban became permanent the moment the one-hour exemption lapsed and they
    /// reconnected.
    /// </remarks>
    [Fact]
    public async Task LiftingClearsTheFlagsTheBanCreated()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), Account));

        var evidence = new FakeEvidence();
        await Build(new RecordingMasters(), evidence).ProcessExpiredAsync();

        Assert.Equal([Account], evidence.Cleared);
    }

    /// <summary>Both ways a ban ends do the same four things.</summary>
    /// <remarks>
    /// The reason LiftAsync exists at all. /unban cleared flags and granted no exemption;
    /// expiry granted an exemption and cleared no flags. Two half-lifts, different halves
    /// missing, so the symptom depended on how the ban ended - which is why this was reported
    /// as two unrelated problems.
    /// </remarks>
    [Fact]
    public async Task AManualLiftAndAnExpiryDoTheSameThing()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow + TimeSpan.FromDays(1), Account));   // still in force

        var masters = new RecordingMasters();
        var evidence = new FakeEvidence();

        var result = await Build(masters, evidence).LiftAsync(Name, Account);

        Assert.Empty(Bans());                                   // record gone
        Assert.Equal([Account], evidence.Cleared);              // flags gone
        Assert.Equal([Name], masters.Exempted);                 // exempt while anything lingers
        Assert.True(result.Landed);
        Assert.Contains($"Unban {Account}", _server.Commands, StringComparer.Ordinal);
    }

    /// <summary>A ban that is still in force is not lifted by the expiry sweep.</summary>
    /// <remarks>
    /// The control. Making every path lift everything would satisfy all of the above and
    /// quietly unban the whole server.
    /// </remarks>
    [Fact]
    public async Task ABanStillInForceIsLeftAlone()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow + TimeSpan.FromDays(1), Account));

        var lifted = await Build(new RecordingMasters(), new FakeEvidence()).ProcessExpiredAsync();

        Assert.Empty(lifted);
        Assert.Single(Bans());
        Assert.DoesNotContain(_server.Commands, c => c.StartsWith("Unban", StringComparison.Ordinal));
    }

    /// <summary>A permanent ban never expires, however old it is.</summary>
    [Fact]
    public async Task APermanentBanIsNeverSweptAway()
    {
        await _store.WriteAsync(Datasets.TempBans, new List<BanRecord>
        {
            new()
            {
                PlayerId = Name,
                UniqueId = Account,
                Reason = "cheating",
                Moderator = "admin",
                At = DateTimeOffset.UtcNow - TimeSpan.FromDays(400),
                Expires = null,
                Permanent = true,
                DurationLabel = "Permanent",
            },
        });

        Assert.Empty(await Build(new RecordingMasters(), new FakeEvidence()).ProcessExpiredAsync());
        Assert.Single(Bans());
    }

    /// <summary>
    /// A ban with no id is ENFORCED against the id the tracker knows, not the display name.
    /// </summary>
    /// <remarks>
    /// The other end of the same bug, and the reason the resolution lives in BanService rather
    /// than at each call site. VpnResponder bans by address and has no account id to pass, so
    /// it called HardEnforceAsync with the display name - accepted by the server, enforcing
    /// nothing, until the sweep happened to catch them online and re-issue it properly.
    ///
    /// The property that matters is not "the ban lands" on its own: it is that the ban and its
    /// lift name the SAME identifier. Resolving both the same way in one place is what makes
    /// that true by construction rather than by two call sites agreeing.
    /// </remarks>
    [Fact]
    public async Task ABanWithNoIdIsEnforcedAgainstTheIdTheTrackerKnows()
    {
        var service = Build(new RecordingMasters(), new FakeEvidence((Name, Account)));

        await service.HardEnforceAsync(Name);

        Assert.Contains($"Ban {Account}", _server.Commands, StringComparer.Ordinal);
        Assert.DoesNotContain($"Ban {Name}", _server.Commands, StringComparer.Ordinal);
    }

    /// <summary>An unknown player is still enforced, by the only identifier there is.</summary>
    /// <remarks>
    /// The control on the lookup. Refusing to act without an id would mean a player the bot
    /// has never seen connect could not be banned at all - which is precisely the player a
    /// pre-emptive ban is for.
    /// </remarks>
    [Fact]
    public async Task AnUnknownPlayerIsStillEnforcedByName()
    {
        var service = Build(new RecordingMasters(), new FakeEvidence());

        await service.HardEnforceAsync("NeverSeen");

        Assert.Contains("Ban NeverSeen", _server.Commands, StringComparer.Ordinal);
    }

    /// <summary>
    /// LIFTING REWRITES THE GAME'S BAN FILE. Without this, bans came back on their own.
    /// </summary>
    /// <remarks>
    /// The server reads that file itself, so a player left listed in it stays banned however
    /// many Unban commands RCON accepts - and ModsaveBanlist's importer, which runs every five
    /// minutes and treats any name in the file that is not in the store as a ban to create,
    /// re-created the record that had just been lifted. The export then wrote it back and the
    /// sweep enforced it.
    /// </remarks>
    [Fact]
    public async Task LiftingRewritesTheGamesBanFile()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow + TimeSpan.FromDays(1), Account));

        var banFile = new RecordingBanFile();
        await Build(new RecordingMasters(), new FakeEvidence(), banFile: banFile).LiftAsync(Name, Account);

        Assert.Equal(1, banFile.Exports);
    }

    /// <summary>
    /// A lift records a tombstone, so the importer cannot resurrect the ban.
    /// </summary>
    /// <remarks>
    /// THE BACKSTOP, and the half that survives what the export cannot fix: a wrong
    /// MODSAVE_BLACKLIST_PATH, a failed write, or a file somebody edits by hand. Each of those
    /// leaves the player listed in a file the importer trusts.
    /// </remarks>
    [Fact]
    public async Task LiftingRecordsATombstoneSoTheImporterCannotResurrectIt()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow + TimeSpan.FromDays(1), Account));

        await Build(new RecordingMasters(), new FakeEvidence()).LiftAsync(Name, Account);

        var tombstones = _store.Read(Datasets.UnbanTombstones,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

        Assert.True(tombstones.ContainsKey(Name));
    }

    /// <summary>
    /// A ban file that cannot be written does not fail the lift.
    /// </summary>
    /// <remarks>
    /// The record is already gone and the native unban has been sent. Throwing here would
    /// leave the caller believing the unban failed when the part that matters succeeded - and
    /// the tombstone means the importer will not undo it either way.
    /// </remarks>
    [Fact]
    public async Task AFailingBanFileExportDoesNotFailTheLift()
    {
        await Seed(TempBan(DateTimeOffset.UtcNow + TimeSpan.FromDays(1), Account));

        var banFile = new RecordingBanFile { Throw = true };
        var result = await Build(new RecordingMasters(), new FakeEvidence(), banFile: banFile).LiftAsync(Name, Account);

        Assert.True(result.Landed);
        Assert.Empty(Bans());
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that will not delete is not a test failure */ }
    }
}
