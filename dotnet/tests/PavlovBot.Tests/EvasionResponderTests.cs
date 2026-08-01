using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using BanRecord = PavlovBot.Core.Moderation.BanRecord;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The ban-evasion response, and every way it must refuse to act.
/// </summary>
/// <remarks>
/// The tracker detected flagged joins and raised an event nothing subscribed to, so
/// detection worked and produced no consequence at all. These cover the response - and
/// mostly the refusals, because each one is a way this feature bans somebody it must not,
/// and all of them are reachable on an ordinary server.
/// </remarks>
public class EvasionResponderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-evasion-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly IpTrackingService _tracking;
    private readonly MasterNames _masters;
    private readonly BanService _bans;
    private readonly EvasionResponder _responder;

    public EvasionResponderTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());

        var options = new BotOptions
        {
            DiscordToken = "t",
            // Port 1 so no enforcement can reach a real server: the assertions are about the
            // RECORD and the decision, never about RCON succeeding.
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 1, Password = "x" }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        };

        var metrics = new MetricsRegistry();
        var rcon = new RconRegistry(options, metrics, NullLogger<RconRegistry>.Instance);

        _masters = new MasterNames(["OwnerAlt"], _store);
        _tracking = new IpTrackingService(_store, metrics, NullLogger<IpTrackingService>.Instance);
        _bans = new BanService(rcon, _store, _masters, NullLogger<BanService>.Instance);

        _responder = new EvasionResponder(
            _tracking, _bans, _masters, _store, new AuditLog(_store),
            new FeedWebhooks(NullLogger<FeedWebhooks>.Instance, metrics),
            metrics, NullLogger<EvasionResponder>.Instance);
    }

    private static FlaggedJoin Join(string name, string account = "76561198000000001") =>
        new(account, name, "203.0.113.9",
            new FlagVerdict(FlagMatch.Ip, "blacklisted ip 203.0.113.9"), DateTimeOffset.UtcNow);

    private List<BanRecord> Bans() => _store.Read<List<BanRecord>>(Datasets.TempBans, []);

    [Fact]
    public async Task AFlaggedJoinIsBannedAndRecorded()
    {
        var outcome = await _responder.RespondAsync(Join("Evader"), CancellationToken.None);

        Assert.Equal(AutoBanOutcome.Banned, outcome);

        var record = Assert.Single(Bans());
        Assert.Equal("Evader", record.PlayerId);
        Assert.True(record.Permanent);
        Assert.Equal("auto", record.Moderator);
        Assert.Contains("Ban evasion", record.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMasterAccountIsNeverAutoBanned()
    {
        /* Households share addresses. An owner playing from the same address as somebody
           they banned matches an address flag, and locking yourself out of your own server
           takes console access to undo. */
        var outcome = await _responder.RespondAsync(Join("OwnerAlt"), CancellationToken.None);

        Assert.Equal(AutoBanOutcome.Master, outcome);
        Assert.Empty(Bans());
    }

    [Fact]
    public async Task MasterProtectionIsCaseInsensitive()
    {
        // Pavlov display names are not case-stable, and a protection that a capital letter
        // defeats is not a protection.
        Assert.Equal(AutoBanOutcome.Master,
            await _responder.RespondAsync(Join("owneralt"), CancellationToken.None));
        Assert.Empty(Bans());
    }

    [Fact]
    public async Task SomebodyWhoJustServedABanIsNotRebanned()
    {
        /* Their flags linger until the clean-up sweep. Without the exemption the first
           reconnection after a temp ban expires turns it into a permanent one - the worst
           outcome this system can produce. */
        await _masters.ExemptAsync("Served");

        var outcome = await _responder.RespondAsync(Join("Served"), CancellationToken.None);

        Assert.Equal(AutoBanOutcome.Exempt, outcome);
        Assert.Empty(Bans());
    }

    [Fact]
    public async Task AnActiveTempBanIsNotSilentlyPromotedToPermanent()
    {
        /* An evader retrying would otherwise rewrite their own record on every attempt, and
           a 24-hour ban would quietly become permanent because they kept knocking. */
        var expires = DateTimeOffset.UtcNow.AddHours(24);
        await _store.WriteAsync(Datasets.TempBans, new List<BanRecord>
        {
            new()
            {
                PlayerId = "Evader", Permanent = false, Expires = expires,
                Moderator = "Dana", Reason = "Cheating", At = DateTimeOffset.UtcNow,
            },
        });

        var outcome = await _responder.RespondAsync(Join("Evader"), CancellationToken.None);

        Assert.Equal(AutoBanOutcome.EnforcedExisting, outcome);

        var record = Assert.Single(Bans());
        Assert.False(record.Permanent);
        Assert.Equal("Dana", record.Moderator);
        Assert.Equal("Cheating", record.Reason);
    }

    [Fact]
    public async Task AJoinWithNoKnownNameIsNotRecordedAsABan()
    {
        /* RCON bans by name. A record with nothing usable behind it is a ban list entry
           that never removes anybody, which reads as a working ban forever. */
        var outcome = await _responder.RespondAsync(
            new FlaggedJoin("76561198000000009", null, "203.0.113.9",
                new FlagVerdict(FlagMatch.Ip, "blacklisted ip"), DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal(AutoBanOutcome.NoName, outcome);
        Assert.Empty(Bans());
    }

    [Fact]
    public async Task AnAutoBanCarriesNoStaffTier()
    {
        // An automated ban must not outrank a human, or a mod cannot lift a false positive
        // without fetching an owner.
        await _responder.RespondAsync(Join("Evader"), CancellationToken.None);

        Assert.Null(Assert.Single(Bans()).Tier);
    }

    [Fact]
    public async Task TheNewAccountIsFlaggedSoTheNextAltIsCaught()
    {
        /* Otherwise only the address is known, and an address is the one thing an evader
           can trivially change. The account needs a CONFIRMED address for the flag to apply
           immediately - without one the tracker defers, which the next test covers. */
        await _store.WriteAsync(Datasets.KnownPlayers, new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["76561198000000042"] = new("76561198000000042", ["203.0.113.9"], [], ["Evader"]),
        });

        await _responder.RespondAsync(Join("Evader", account: "76561198000000042"), CancellationToken.None);

        Assert.Contains("76561198000000042", _tracking.LoadFlags().Ids);
    }

    [Fact]
    public async Task AnAccountWithNoConfirmedAddressDefersItsFlagRatherThanLosingIt()
    {
        /* The evader is still online, so the line that confirms their address is the
           disconnect this ban is about to cause. Flagging nothing and moving on would let
           them reconnect from the same address with nothing to catch them. */
        await _responder.RespondAsync(Join("Evader", account: "76561198000000099"), CancellationToken.None);

        var pending = _store.Read(Datasets.AutobanExempt + "_pending",
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains("76561198000000099", pending.Keys);
    }

    [Fact]
    public void ConstructingTheResponderSubscribesToFlagged()
    {
        /* The defect itself, asserted directly. The tracker raised Flagged and the event had
           no subscriber, so detection logged a warning and produced no consequence. Reading
           the delegate is the only way to prove the wire exists without a parseable log line
           and a live RCON server. */
        var field = typeof(IpTrackingService).GetField("Flagged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        var handler = field!.GetValue(_tracking) as Delegate;

        Assert.NotNull(handler);
        Assert.Contains(handler!.GetInvocationList(),
            d => d.Target is EvasionResponder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
