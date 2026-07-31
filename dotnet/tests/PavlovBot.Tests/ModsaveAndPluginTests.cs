using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Plugins;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The game's ban-list file: the message a banned player sees, and the only way bans made
/// from the in-game admin menu ever reach the bot.
/// </summary>
public class ModsaveBanlistTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-modsave-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly SerializedStore _store;
    private readonly ModsaveBanlist _modsave;

    /* A FIXED clock. TimeLeft truncates rather than rounds, so with the real clock the
       milliseconds between capturing "now" and the export reading it turn "3d 4h" into
       "3d 3h" - a flaky test asserting on correct behaviour. */
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public ModsaveBanlistTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "banlist.txt");
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _modsave = new ModsaveBanlist(_path, _store, NullLogger<ModsaveBanlist>.Instance, new FixedClock());
    }

    [Fact]
    public void BlocksAreParsedIntoNameReasonAndUnban()
    {
        var entries = ModsaveBanlist.Parse("Alice\nReason: Cheating\nUnban: 3d 4h\n\nBob\nReason: Griefing\nUnban: Permanent\n");

        Assert.Equal(2, entries.Count);
        Assert.Equal("Alice", entries[0].Name);
        Assert.Equal("Cheating", entries[0].Reason);
        Assert.Equal("3d 4h", entries[0].Unban);
        Assert.Equal("Permanent", entries[1].Unban);
    }

    [Fact]
    public void ABlockWhoseFirstLineIsAFieldIsSkipped()
    {
        /* A trailing blank line or a hand-edit produces one of these, and treating it as a
           player creates a ban on somebody called "Reason". */
        var entries = ModsaveBanlist.Parse("Reason: orphaned\nUnban: Permanent\n\nAlice\nReason: Cheating\nUnban: Permanent\n");

        Assert.Single(entries);
        Assert.Equal("Alice", entries[0].Name);
    }

    [Fact]
    public void AReasonContainingAColonSurvivesIntact()
    {
        // "Cheating: aimbot" splits on the FIRST colon only.
        var entries = ModsaveBanlist.Parse("Alice\nReason: Cheating: aimbot\nUnban: Permanent\n");
        Assert.Equal("Cheating: aimbot", entries[0].Reason);
    }

    [Fact]
    public async Task ExportWritesTheTimeLeftAsASpan_NotADate()
    {
        /* A player reading "3d 4h" knows what it means; one reading a timestamp has to work
           out the timezone and gets it wrong. */
        await _store.WriteAsync<List<BanRecord>>(Datasets.TempBans,
        [
            new BanRecord { PlayerId = "Alice", Reason = "Cheating", Expires = Now.AddDays(3).AddHours(4) },
            new BanRecord { PlayerId = "Bob", Reason = "Griefing", Permanent = true },
        ]);

        Assert.Equal(2, await _modsave.ExportAsync());

        var written = await File.ReadAllTextAsync(_path);
        Assert.Contains("Unban: 3d 4h", written, StringComparison.Ordinal);
        Assert.Contains("Unban: Permanent", written, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredBansAreNotExported()
    {
        await _store.WriteAsync<List<BanRecord>>(Datasets.TempBans,
            [new BanRecord { PlayerId = "Served", Reason = "x", Expires = Now.AddHours(-1) }]);

        Assert.Equal(0, await _modsave.ExportAsync());
    }

    [Fact]
    public async Task ANewlineInAReasonIsFlattened()
    {
        // The format is line-oriented; a newline would turn the rest into a bogus field.
        await _store.WriteAsync<List<BanRecord>>(Datasets.TempBans,
            [new BanRecord { PlayerId = "Alice", Reason = "line one\nUnban: Permanent", Permanent = true }]);

        await _modsave.ExportAsync();
        var entries = ModsaveBanlist.Parse(await File.ReadAllTextAsync(_path));

        Assert.Single(entries);
        Assert.Equal("line one Unban: Permanent", entries[0].Reason);
    }

    [Fact]
    public async Task ImportPicksUpInGameBansTheBotNeverIssued()
    {
        await File.WriteAllTextAsync(_path, "Alice\nReason: Cheating\nUnban: 3d\n\n");

        Assert.Equal(1, await _modsave.ImportAsync());

        var ban = _store.Read<List<BanRecord>>(Datasets.TempBans, []).Single();
        Assert.Equal("Alice", ban.PlayerId);
        Assert.Equal("in-game", ban.Moderator);
        Assert.False(ban.Permanent);
    }

    [Fact]
    public async Task ImportIsIdempotent()
    {
        // It runs every few minutes forever.
        await File.WriteAllTextAsync(_path, "Alice\nReason: Cheating\nUnban: 3d\n\n");

        Assert.Equal(1, await _modsave.ImportAsync());
        Assert.Equal(0, await _modsave.ImportAsync());
        Assert.Single(_store.Read<List<BanRecord>>(Datasets.TempBans, []));
    }

    [Fact]
    public async Task AnUnreadableUnbanValueBecomesPermanentWithTheValueRecorded()
    {
        /* Guessing a duration would either free somebody early or hold them longer than
           staff intended. The note makes it fixable by hand. */
        await File.WriteAllTextAsync(_path, "Alice\nReason: Cheating\nUnban: next tuesday\n\n");
        await _modsave.ImportAsync();

        var ban = _store.Read<List<BanRecord>>(Datasets.TempBans, []).Single();
        Assert.True(ban.Permanent);
        Assert.Contains("unreadable unban value: next tuesday", ban.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportNeverREMOVESABan()
    {
        /* The store is the source of truth. A truncated or mid-write file would otherwise
           lift every ban on the server. */
        await _store.WriteAsync<List<BanRecord>>(Datasets.TempBans,
            [new BanRecord { PlayerId = "Existing", Reason = "x", Permanent = true }]);

        await File.WriteAllTextAsync(_path, "");
        await _modsave.ImportAsync();

        Assert.Single(_store.Read<List<BanRecord>>(Datasets.TempBans, []));
    }

    [Fact]
    public async Task SyncImportsBeforeExporting()
    {
        /* The order is load-bearing. Exporting first rewrites the file from the store and
           destroys the in-game entries before they have been read - so a ban issued from
           the admin menu vanishes and is never recorded. */
        await File.WriteAllTextAsync(_path, "FromTheMenu\nReason: Cheating\nUnban: Permanent\n\n");

        await _modsave.SyncAsync();

        Assert.Contains(_store.Read<List<BanRecord>>(Datasets.TempBans, []), b => b.PlayerId == "FromTheMenu");
        Assert.Contains("FromTheMenu", await File.ReadAllTextAsync(_path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnconfiguredPathIsANoOp()
    {
        var disabled = new ModsaveBanlist(null, _store, NullLogger<ModsaveBanlist>.Instance);
        Assert.False(disabled.Enabled);
        Assert.Equal(0, await disabled.ExportAsync());
        Assert.Equal(0, await disabled.ImportAsync());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}

public class PluginHostTests
{
    private sealed class FakePlugin(string name, string[]? dependsOn = null, bool throwOnStart = false) : IPlugin
    {
        public string Name => name;
        public string Version => "1.0.0";
        public string Description => "test";
        public IReadOnlyList<string> DependsOn => dependsOn ?? [];
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            if (throwOnStart) throw new InvalidOperationException("plugin is broken");
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    private static PluginHost New() =>
        new(new ServiceCollection().BuildServiceProvider(),
            NullLogger<PluginHost>.Instance);

    [Fact]
    public void DependenciesDecideStartOrder()
    {
        var ordered = PluginHost.Order([
            new FakePlugin("c", ["b"]),
            new FakePlugin("a"),
            new FakePlugin("b", ["a"]),
        ]);

        Assert.Equal(["a", "b", "c"], ordered.Select(p => p.Name));
    }

    [Fact]
    public void ACycleIsReportedWithItsParticipants()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PluginHost.Order([
            new FakePlugin("a", ["b"]),
            new FakePlugin("b", ["a"]),
        ]));

        Assert.Contains("circular", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMissingDependencyIsReported_NotSilentlyReordered()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PluginHost.Order([new FakePlugin("a", ["ghost"])]));

        Assert.Contains("\"ghost\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenPluginIsDisabledAndTheRestStillRun()
    {
        /* The whole reason it is a plugin and not a class in the host: one broken optional
           feature must never be a dead bot. */
        var host = New();
        var good = new FakePlugin("good");
        var bad = new FakePlugin("bad", throwOnStart: true);

        await host.StartAllAsync([good, bad]);

        Assert.True(good.Started);
        Assert.Equal(PluginState.Running, host.Status().Single(p => p.Name == "good").State);

        var failed = host.Status().Single(p => p.Name == "bad");
        Assert.Equal(PluginState.Failed, failed.State);
        Assert.Equal("plugin is broken", failed.Error);
    }

    [Fact]
    public async Task ABadDependencyGraphDisablesThePluginSystem_NotTheBot()
    {
        // Starting an arbitrary subset would be worse: a plugin would run without what it needs.
        var host = New();
        var plugin = new FakePlugin("a", ["ghost"]);

        await host.StartAllAsync([plugin]);

        Assert.False(plugin.Started);
        Assert.Empty(host.Status());
    }

    [Fact]
    public async Task PluginsStopInReverseOrder()
    {
        var host = New();
        var first = new FakePlugin("a");
        var second = new FakePlugin("b", ["a"]);

        await host.StartAllAsync([second, first]);
        await host.StopAllAsync();

        Assert.True(first.Stopped);
        Assert.True(second.Stopped);
        Assert.All(host.Status(), p => Assert.Equal(PluginState.Stopped, p.State));
    }

    [Fact]
    public async Task AFailedPluginIsNotStopped()
    {
        // It never started, so its stop hook has nothing to release and might well throw.
        var host = New();
        var bad = new FakePlugin("bad", throwOnStart: true);

        await host.StartAllAsync([bad]);
        await host.StopAllAsync();

        Assert.False(bad.Stopped);
    }

    [Fact]
    public void AMissingPluginDirectoryIsNotAnError()
    {
        Assert.Empty(New().Discover(Path.Combine(Path.GetTempPath(), "no-such-plugins-" + Guid.NewGuid().ToString("N"))));
    }
}
