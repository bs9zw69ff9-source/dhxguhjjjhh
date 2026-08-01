using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// End to end over the real store: log lines in, account knowledge and flags out.
/// </summary>
public class IpTrackingServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-ip-" + Guid.NewGuid().ToString("N"));
    private readonly IpTrackingService _service;
    private const string File = "server1.log";

    public IpTrackingServiceTests()
    {
        var store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _service = new IpTrackingService(store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);
    }

    private Task Feed(string text) => _service.IngestAsync(new LogLine(File, text));

    private const string Accept = "[2026.07.15-12.00.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
    private const string Login = "[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Alice userId: EOS:0002abc";
    private const string Close = "[2026.07.15-12.30.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";

    [Fact]
    public async Task AJoinIsRecordedWithItsGuessedAddress()
    {
        await Feed(Accept);
        await Feed(Login);

        var account = _service.Account("0002abc");
        Assert.NotNull(account);
        Assert.Equal("Alice", account!.Name);
        Assert.Equal(["203.0.113.5"], account.GuessedIps);
        Assert.Empty(account.ConfirmedIps);   // a correlated address is NOT confirmed
    }

    [Fact]
    public async Task ADisconnectConfirmsTheAddressAndPromotesItOutOfTheGuessList()
    {
        /* Two lists disagreeing about the same address is how a display ends up showing it
           twice with different confidence. */
        await Feed(Accept);
        await Feed(Login);
        await Feed(Close);

        var account = _service.Account("0002abc")!;
        Assert.Equal(["203.0.113.5"], account.ConfirmedIps);
        Assert.Empty(account.GuessedIps);
    }

    [Fact]
    public async Task AnAmbiguousJoinRecordsNoAddressAtAll()
    {
        // Two people connecting at once. Recording either one attributes a stranger's
        // address to this account - and a flag built on it would ban them.
        await Feed(Accept);
        await Feed("[2026.07.15-12.00.00:500]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.6:7777");
        await Feed(Login);

        Assert.Empty(_service.Account("0002abc")!.GuessedIps);
    }

    [Fact]
    public async Task NamesAreKeptMostRecentFirst()
    {
        // The display name staff search by is the one the player used last.
        await Feed(Login);
        await Feed("[2026.07.15-13.00.00:000]LogNet: Login request: ?Name=AliceV2 userId: EOS:0002abc");

        Assert.Equal(["AliceV2", "Alice"], _service.Account("0002abc")!.Names);
    }

    [Fact]
    public async Task AnAccountIsFindableByAnyNameItHasUsed()
    {
        await Feed(Login);
        await Feed("[2026.07.15-13.00.00:000]LogNet: Login request: ?Name=AliceV2 userId: EOS:0002abc");

        Assert.Equal("0002abc", _service.AccountByName("Alice")!.Id);
        Assert.Equal("0002abc", _service.AccountByName("alicev2")!.Id);
    }

    [Fact]
    public async Task AnUntrackedNameLeavesNoTrail()
    {
        // Master accounts must leave no address trail at all.
        _service.Untracked.Add("Owner");
        await Feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Owner userId: EOS:0009owner");

        Assert.Null(_service.Account("0009owner"));
    }

    [Fact]
    public async Task AMasterStillAppearsInTheJoinLog()
    {
        /* Untracked means UNRECORDED, not invisible. The owner is usually the one testing
           the join log, and a log that silently omits them reads as a dead feed - while the
           line itself still carries no address, which is the whole point of the exemption. */
        var store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        var service = new IpTrackingService(store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance,
            masters: new OnlyMaster("Owner"));
        service.Untracked.Add("Owner");

        PlayerJoined? seen = null;
        service.Joined += join => { seen = join; return Task.CompletedTask; };

        await service.IngestAsync(new LogLine(File, Accept));
        await service.IngestAsync(new LogLine(File,
            "[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Owner userId: EOS:0009owner"));

        Assert.NotNull(seen);
        Assert.Equal("Owner", seen!.Name);
        Assert.Null(seen.Ip);                          // no address, ever
        Assert.Null(service.Account("0009owner"));     // and still nothing recorded
    }

    [Fact]
    public async Task AnIgnoredNameThatIsNotAMasterStaysSilent()
    {
        // The ignore list has other uses - a bot account, a server-side observer. Those
        // asked to be invisible and must stay that way.
        _service.Untracked.Add("Watcher");

        var raised = false;
        _service.Joined += _ => { raised = true; return Task.CompletedTask; };

        await Feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Watcher userId: EOS:0009watch");

        Assert.False(raised);
    }

    private sealed class OnlyMaster(string name) : PavlovBot.Host.Moderation.IMasterNames
    {
        public bool IsMaster(string who) => string.Equals(who, name, StringComparison.OrdinalIgnoreCase);
        public bool IsExempt(string who) => IsMaster(who);
    }

    [Fact]
    public async Task APlaceholderIdIsNotTracked()
    {
        await Feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Alice userId: INVALID");
        Assert.Null(_service.Account("INVALID"));
    }

    [Fact]
    public async Task BanningAnOnlinePlayerDefersTheFlagUntilTheirDisconnectConfirmsIt()
    {
        /* THE case this mechanism exists for. Their address is confirmed by the disconnect
           the ban is about to cause, so at ban time there is nothing to flag. Without the
           deferral they reconnect from the same address and nothing catches them. */
        await Feed(Accept);
        await Feed(Login);

        var outcome = await _service.RequestFlagAsync("0002abc", flagAccountId: true);
        Assert.True(outcome.Pending);
        Assert.Empty(outcome.Ips);
        Assert.Empty(_service.LoadFlags().Ips);

        // The kick produces the disconnect, which resolves it.
        await Feed(Close);
        Assert.Contains("203.0.113.5", _service.LoadFlags().Ips);
    }

    [Fact]
    public async Task ATempBanFlagsTheAddressButNotTheAccountId()
    {
        /* An account-id flag has no expiry of its own, so branding the id on a temp ban
           would keep catching them after the ban was served. */
        await Feed(Close);
        await _service.RequestFlagAsync("0002abc", flagAccountId: false);

        var flags = _service.LoadFlags();
        Assert.Contains("203.0.113.5", flags.Ips);
        Assert.DoesNotContain("0002abc", flags.Ids);
    }

    [Fact]
    public async Task APermanentBanFlagsTheAccountIdToo()
    {
        await Feed(Close);
        await _service.RequestFlagAsync("0002abc", flagAccountId: true);
        Assert.Contains("0002abc", _service.LoadFlags().Ids);
    }

    [Fact]
    public async Task AFlaggedJoinRaisesTheEventOnce()
    {
        var hits = 0;
        _service.Flagged += _ => { hits++; return Task.CompletedTask; };

        await Feed(Close);
        await _service.RequestFlagAsync("0002abc", flagAccountId: true);

        await Feed("[2026.07.16-12.00.01:000]LogNet: Login request: ?Name=Alice userId: EOS:0002abc");
        Assert.Equal(1, hits);

        /* Debounced. A flagged player retrying produces a login line every few seconds, and
           without this each one fires a fresh ban, audit entry and feed message - a
           self-inflicted flood exactly when staff are watching. */
        await Feed("[2026.07.16-12.00.05:000]LogNet: Login request: ?Name=Alice userId: EOS:0002abc");
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task ClearingFlagsUndoesWhatTheBanCreated()
    {
        await Feed(Close);
        await _service.RequestFlagAsync("0002abc", flagAccountId: true);

        await _service.ClearFlagsAsync("0002abc");
        var flags = _service.LoadFlags();
        Assert.Empty(flags.Ips);
        Assert.Empty(flags.Ids);
    }

    [Fact]
    public async Task ClearingFlagsLeavesAManualBlockAlone()
    {
        /* An unban lifts what THAT ban created; it must not silently undo a standing
           administrative block somebody set through /configure. */
        await Feed(Close);
        await _service.FlagAddressManuallyAsync("203.0.113.5");
        await _service.RequestFlagAsync("0002abc", flagAccountId: true);

        await _service.ClearFlagsAsync("0002abc");
        Assert.Contains("203.0.113.5", _service.LoadFlags().ManualIps);
    }

    [Fact]
    public async Task AKillRecordSplitAcrossLinesIsAssembled()
    {
        KillEventCapture? captured = null;
        _service.Kill += k => { captured = new KillEventCapture(k.Killer, k.Killed, k.KilledBy); return Task.CompletedTask; };

        await Feed("""LogTemp: KillData {"Killer": "Alice",""");
        Assert.Null(captured);   // incomplete: nothing reported yet

        await Feed("""  "Killed": "Bob", "KilledBy": "AK47"}""");
        Assert.Equal(new KillEventCapture("Alice", "Bob", "AK47"), captured);
    }

    private sealed record KillEventCapture(string? Killer, string? Killed, string? KilledBy);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
