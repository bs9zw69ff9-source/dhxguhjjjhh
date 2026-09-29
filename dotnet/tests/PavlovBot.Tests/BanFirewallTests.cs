using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;
using BanRecord = PavlovBot.Core.Moderation.BanRecord;

namespace PavlovBot.Tests;

/// <summary>
/// Banned players' addresses are denied at the firewall, and lifted when the ban ends - and the
/// addresses that must never be denied are not.
/// </summary>
public sealed class BanFirewallTests : IAsyncDisposable
{
    private const string BannedIp = "203.0.113.9";
    private const string OtherIp = "198.51.100.7";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-banfw-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly RconRegistry _rcon;
    private readonly IpTrackingService _tracking;
    private readonly MasterNames _masters;
    private readonly BanService _bans;
    private readonly RecordingFirewall _firewall = new();

    public BanFirewallTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        var metrics = new MetricsRegistry();
        _rcon = new RconRegistry(new BotOptions
        {
            DiscordToken = "t",
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = 1, Password = "x" }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        }, metrics, NullLogger<RconRegistry>.Instance);

        _masters = new MasterNames(["OwnerAlt"], _store);
        _tracking = new IpTrackingService(_store, metrics, NullLogger<IpTrackingService>.Instance);
        _bans = new BanService(_rcon, _store, _masters, NullLogger<BanService>.Instance);
    }

    private BanFirewall Firewall(bool enabled = true, params string[] neverBlock) =>
        new(_bans, _tracking, _masters, _firewall, _store, enabled, neverBlock, NullLogger<BanFirewall>.Instance);

    private async Task SeenAsync(string id, string name, params string[] ips)
    {
        var accounts = _store.Read(Datasets.KnownPlayers, new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase));
        accounts[id] = new AccountRecord(id, ips, [], [name]);
        await _store.WriteAsync(Datasets.KnownPlayers, accounts);
    }

    private async Task BanAsync(string name, string? id = null, TimeSpan? length = null)
    {
        var now = DateTimeOffset.UtcNow;
        await _store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            bans.Add(new BanRecord
            {
                PlayerId = name,
                UniqueId = id,
                At = now,
                Permanent = length is null,
                Expires = length is { } l ? now + l : null,
            });
            return bans;
        });
    }

    private Task UnbanAsync(string name) =>
        _store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            bans.RemoveAll(b => b.PlayerId == name);
            return bans;
        });

    [Fact]
    public async Task ABannedPlayersAddressesAreDenied()
    {
        await SeenAsync("acct1", "Cheater", BannedIp, OtherIp);
        await BanAsync("Cheater", "acct1");

        var pass = await Firewall().SyncAsync();

        Assert.Equal(2, pass.Denied);
        Assert.Equal([OtherIp, BannedIp], _firewall.Denied.Order(StringComparer.Ordinal));
        Assert.Equal("Cheater", Firewall().Managed()[BannedIp].Player);
    }

    [Fact]
    public async Task ATempBanIsDeniedToo()
    {
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1", TimeSpan.FromDays(2));

        await Firewall().SyncAsync();

        Assert.Equal([BannedIp], _firewall.Denied);
    }

    [Fact]
    public async Task TheRuleIsLiftedWhenTheBanEnds()
    {
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1");
        await Firewall().SyncAsync();

        await UnbanAsync("Cheater");
        var pass = await Firewall().SyncAsync();

        Assert.Equal(1, pass.Lifted);
        Assert.Equal([BannedIp], _firewall.Undenied);
        Assert.Empty(Firewall().Managed());
    }

    [Fact]
    public async Task AnExpiredTempBanIsLifted()
    {
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1", TimeSpan.FromDays(1));
        await Firewall().SyncAsync();

        // Served: rewrite the record as already expired.
        await _store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
            [.. bans.Select(b => b with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) })]);

        await Firewall().SyncAsync();

        Assert.Equal([BannedIp], _firewall.Undenied);
    }

    [Fact]
    public async Task ASecondPassChangesNothing()
    {
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1");
        await Firewall().SyncAsync();

        var pass = await Firewall().SyncAsync();

        Assert.Equal(new BanFirewallPass(0, 0, 0, 0), pass);
        Assert.Single(_firewall.Denied);
    }

    [Fact]
    public async Task AnAddressLearnedAfterTheBanIsDeniedOnTheNextPass()
    {
        // Banned while online: their address is confirmed by the disconnect the ban causes.
        await BanAsync("Cheater", "acct1");
        await Firewall().SyncAsync();
        Assert.Empty(_firewall.Denied);

        await SeenAsync("acct1", "Cheater", BannedIp);
        await Firewall().SyncAsync();

        Assert.Equal([BannedIp], _firewall.Denied);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("100.64.3.4")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::5")]
    public async Task NonPublicAddressesAreNeverDenied(string ip)
    {
        // Loopback above all: RCON runs over it, so denying it takes the bot off every server.
        await SeenAsync("acct1", "Cheater", ip);
        await BanAsync("Cheater", "acct1");

        await Firewall().SyncAsync();

        Assert.Empty(_firewall.Denied);
    }

    [Fact]
    public async Task AnAddressAMasterNameUsesIsNeverDenied()
    {
        // A banned player on the owner's own connection must not firewall the owner out.
        await SeenAsync("owner", "OwnerAlt", BannedIp);
        await SeenAsync("acct1", "Cheater", BannedIp, OtherIp);
        await BanAsync("Cheater", "acct1");

        await Firewall().SyncAsync();

        Assert.Equal([OtherIp], _firewall.Denied);
    }

    [Fact]
    public async Task NeverBlockAddressesAreNeverDenied()
    {
        await SeenAsync("acct1", "Cheater", BannedIp, OtherIp);
        await BanAsync("Cheater", "acct1");

        await Firewall(enabled: true, BannedIp).SyncAsync();

        Assert.Equal([OtherIp], _firewall.Denied);
    }

    [Fact]
    public async Task AManualBlacklistRuleIsNeverTouched()
    {
        await _tracking.FlagAddressManuallyAsync(BannedIp);
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1");

        await Firewall().SyncAsync();
        await UnbanAsync("Cheater");
        await Firewall().SyncAsync();

        Assert.Empty(_firewall.Denied);
        Assert.Empty(_firewall.Undenied);
    }

    [Fact]
    public async Task AProtectedPlayersBanIsNotDenied()
    {
        await _masters.ProtectAsync("Cheater");
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1");

        await Firewall().SyncAsync();

        Assert.Empty(_firewall.Denied);
    }

    [Fact]
    public async Task TurningItOffLiftsEveryRuleItAdded()
    {
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1");
        await Firewall().SyncAsync();

        await Firewall(enabled: false).SyncAsync();

        Assert.Equal([BannedIp], _firewall.Undenied);
        Assert.Empty(Firewall().Managed());
    }

    [Fact]
    public async Task AFailedDenyIsNotRecordedAndWaitsBeforeRetrying()
    {
        await SeenAsync("acct1", "Cheater", BannedIp);
        await BanAsync("Cheater", "acct1");
        _firewall.Fail = true;
        var firewall = Firewall();

        var first = await firewall.SyncAsync();
        var second = await firewall.SyncAsync();

        Assert.Equal(1, first.Failed);
        Assert.Equal(new BanFirewallPass(0, 0, 0, 0), second);   // backing off, not hammering ufw
        Assert.Single(_firewall.Denied);
        Assert.Empty(firewall.Managed());
    }

    [Fact]
    public async Task AFloodOfBansIsSpreadOverPasses()
    {
        for (var i = 0; i < BanFirewall.MaxChangesPerPass + 5; i++)
        {
            await SeenAsync($"acct{i}", $"Player{i}", $"203.0.113.{i + 1}");
            await BanAsync($"Player{i}", $"acct{i}");
        }

        var first = await Firewall().SyncAsync();
        var second = await Firewall().SyncAsync();

        Assert.Equal(BanFirewall.MaxChangesPerPass, first.Denied);
        Assert.Equal(5, first.Deferred);
        Assert.Equal(5, second.Denied);
    }

    public async ValueTask DisposeAsync()
    {
        await _rcon.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Records what would have been sent to ufw.</summary>
    private sealed class RecordingFirewall : IFirewall
    {
        public List<string> Denied { get; } = [];
        public List<string> Undenied { get; } = [];
        public bool Fail { get; set; }

        public Task<FirewallResult> DenyAsync(string ip, CancellationToken ct = default)
        {
            Denied.Add(ip);
            return Task.FromResult(new FirewallResult(!Fail, Fail ? "ufw is not installed" : "Rule inserted"));
        }

        public Task<FirewallResult> UndenyAsync(string ip, CancellationToken ct = default)
        {
            Undenied.Add(ip);
            return Task.FromResult(new FirewallResult(!Fail, "Rule deleted"));
        }

        public Task<FirewallResult> StatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new FirewallResult(true, ""));
    }
}
