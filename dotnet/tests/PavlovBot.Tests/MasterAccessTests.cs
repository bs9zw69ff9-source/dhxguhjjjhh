using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Security;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Master names: in <c>mods.txt</c> by checking the file, menu and access manager over RCON on join.
/// </summary>
public sealed class MasterAccessTests : IAsyncDisposable
{
    private const string Master = "fki6";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlovbot-masters-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRconServer _server = new();
    private readonly RconRegistry _rcon;
    private readonly ServerLabels _labels = new();
    private readonly string _install;
    private readonly string _log;
    private readonly MasterAccess _access;
    private readonly RosterService _rosters;
    private readonly string _rosterDir;

    public MasterAccessTests()
    {
        _install = Path.Combine(_root, "pavlovserver");
        Directory.CreateDirectory(Path.Combine(_install, "Pavlov", "Saved", "Config"));
        _log = PavlovInstalls.LogPath(_install);
        _labels.Assign([_log]);

        _rcon = new RconRegistry(new BotOptions
        {
            DiscordToken = "t",
            Servers = [new RconOptions { Name = "server1", Host = "127.0.0.1", Port = _server.Port, Password = _server.Password }],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _root,
        }, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

        _rosterDir = Path.Combine(_install, "Pavlov", "Saved", "Config", "ModSave", "FactionRoles");
        Directory.CreateDirectory(_rosterDir);
        _rosters = new RosterService(_rosterDir, NullLogger<RosterService>.Instance,
            backupDirectory: Path.Combine(_root, "roster_bak"));

        var store = new SerializedStore(new FileKeyValueBackend(Path.Combine(_root, "data")), new SystemTextJsonCodec());

        _access = new MasterAccess(
            new MasterNames([Master], store),
            tracking: null,
            _rcon,
            _labels,
            [_install],
            new WhitelistFile(NullLogger<WhitelistFile>.Instance),
            NullLogger<MasterAccess>.Instance,
            grantDelay: TimeSpan.Zero,
            rosters: _rosters);
    }

    private string Mods => MasterAccess.ModsPath(_install);
    private string Whitelist => PavlovInstalls.WhitelistPath(_install);

    private PlayerJoined Join(string name, string? file = null) =>
        new(file ?? _log, name, name, null, false, DateTimeOffset.UtcNow);

    /// <summary>Wait for the background grant to reach the fake server.</summary>
    private async Task<IReadOnlyList<string>> CommandsAfterGrantAsync(int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_server.Commands.Count < expected && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        // Anything more would have to arrive now; give it a moment to prove it does not.
        await Task.Delay(200);
        return _server.Commands;
    }

    [Fact]
    public void TheGrantIsTheFullMenuWithNoBitcode()
    {
        Assert.Equal([$"GiveMenu {Master}"], MasterAccess.GrantCommands(Master));
    }

    [Fact]
    public async Task AMasterJoiningGetsTheFullMenuOnTheWire()
    {
        await _access.OnJoinedAsync(Join(Master));

        Assert.Equal([$"GiveMenu {Master}"], await CommandsAfterGrantAsync(1));
    }

    [Fact]
    public async Task MasterNamesMatchIgnoringCase()
    {
        await _access.OnJoinedAsync(Join("FKI6"));

        Assert.Single(await CommandsAfterGrantAsync(1));
    }

    [Fact]
    public async Task AnybodyElseJoiningGetsNothing()
    {
        await _access.OnJoinedAsync(Join("RandomPlayer"));

        Assert.Empty(await CommandsAfterGrantAsync(0));
        Assert.False(File.Exists(Mods));
    }

    [Fact]
    public async Task AReconnectFlurryGrantsOnce()
    {
        await _access.OnJoinedAsync(Join(Master));
        await _access.OnJoinedAsync(Join(Master));
        await _access.OnJoinedAsync(Join(Master));

        await CommandsAfterGrantAsync(1);
        await Task.Delay(300);   // long enough for a duplicate grant to land if one were sent
        Assert.Single(_server.Commands);
    }

    [Fact]
    public async Task AJoinOnALogThatIsNotAServerSendsNothing()
    {
        await _access.OnJoinedAsync(Join(Master, file: "/somewhere/else/Pavlov.log"));

        Assert.Empty(await CommandsAfterGrantAsync(0));
    }

    [Fact]
    public async Task MissingMastersAreAppendedAndEverythingElseIsKept()
    {
        await File.WriteAllTextAsync(Mods, "# staff\nSomeMod\n");

        await _access.EnsureFilesAsync(CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(Mods);
        Assert.Equal("# staff", lines[0]);
        Assert.Equal("SomeMod", lines[1]);
        Assert.Contains(Master, lines);
    }

    [Fact]
    public async Task APresentMasterIsNotWrittenTwice()
    {
        await File.WriteAllTextAsync(Mods, "FKI6\n");
        var before = await File.ReadAllTextAsync(Mods);

        await _access.EnsureFilesAsync(CancellationToken.None);
        await _access.EnsureFilesAsync(CancellationToken.None);

        Assert.Equal(before, await File.ReadAllTextAsync(Mods));
    }

    [Fact]
    public async Task AJoinChecksThatServersModsFile()
    {
        await _access.GrantAsync(1, Master, CancellationToken.None);

        Assert.Contains(Master, await File.ReadAllLinesAsync(Mods));
    }

    [Fact]
    public async Task MastersAreOnEveryWhitelist()
    {
        await File.WriteAllTextAsync(Path.Combine(_rosterDir, "ncrtrooper.txt"), "PlayerOne\n");
        await File.WriteAllTextAsync(Whitelist, "# friends\nPlayerOne\n");

        await _access.EnsureFilesAsync(CancellationToken.None);

        var files = RosterService.RosterFilesOf(_rosters.Factions);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(_rosterDir, file));
            Assert.Contains(Master, lines);
        }

        // Existing members and hand-written lines are kept.
        Assert.Contains("PlayerOne", await File.ReadAllLinesAsync(Path.Combine(_rosterDir, "ncrtrooper.txt")));
        var whitelist = await File.ReadAllLinesAsync(Whitelist);
        Assert.Equal(["# friends", "PlayerOne"], whitelist[..2]);
        Assert.Contains(Master, whitelist);
    }

    [Fact]
    public async Task ARosterAlreadyHoldingTheMastersIsNotRewritten()
    {
        var path = Path.Combine(_rosterDir, "ncrtrooper.txt");
        await File.WriteAllTextAsync(path, "fki6\n");
        await _access.EnsureFilesAsync(CancellationToken.None);
        var written = File.GetLastWriteTimeUtc(path);
        var before = await File.ReadAllTextAsync(path);

        await _access.EnsureFilesAsync(CancellationToken.None);

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task AJoinPutsAWipedMasterBackOnTheRosters()
    {
        var path = Path.Combine(_rosterDir, "ncrtrooper.txt");
        await File.WriteAllTextAsync(path, "PlayerOne\n");   // a wipe or hand edit dropped them

        await _access.GrantAsync(1, Master, CancellationToken.None);

        Assert.Contains(Master, await File.ReadAllLinesAsync(path));
        Assert.Contains(Master, await File.ReadAllLinesAsync(Whitelist));
    }

    [Fact]
    public async Task AMissingConfigDirectoryIsNotCreated()
    {
        // A wrong install path must not grow a tree the game never reads.
        var bogus = Path.Combine(_root, "not-an-install");
        var store = new SerializedStore(new FileKeyValueBackend(Path.Combine(_root, "data2")), new SystemTextJsonCodec());
        var access = new MasterAccess(new MasterNames([Master], store), null, _rcon, _labels, [bogus],
            new WhitelistFile(NullLogger<WhitelistFile>.Instance), NullLogger<MasterAccess>.Instance);

        await access.EnsureFilesAsync(CancellationToken.None);

        Assert.False(Directory.Exists(bogus));
        await access.DisposeAsync();
    }

    // ---- a name removed from MASTER_NAMES ----

    private MasterAccess Access(SerializedStore store, params string[] masters) =>
        new(new MasterNames(masters, store), null, _rcon, _labels, [_install],
            new WhitelistFile(NullLogger<WhitelistFile>.Instance), NullLogger<MasterAccess>.Instance,
            grantDelay: TimeSpan.Zero, rosters: _rosters, store: store);

    private SerializedStore GrantStore() =>
        new(new FileKeyValueBackend(Path.Combine(_root, "grants")), new SystemTextJsonCodec());

    [Fact]
    public async Task ARemovedMasterLosesEverythingItWasGiven()
    {
        /* THE BUG. Dropping a name from .env only stopped the bot granting it again; the mods.txt
           line, the whitelist lines and every roster entry stayed behind. */
        await File.WriteAllTextAsync(Path.Combine(_rosterDir, "ncrtrooper.txt"), "PlayerOne\n");
        var store = GrantStore();

        var before = Access(store, Master, "OldAlt", "KeptMaster");
        await before.StartAsync(CancellationToken.None);
        await before.StopAsync(CancellationToken.None);
        Assert.Contains("OldAlt", await File.ReadAllLinesAsync(Mods));

        var after = Access(store, Master, "KeptMaster");   // OldAlt taken out of MASTER_NAMES, restart
        await after.StartAsync(CancellationToken.None);
        await after.StopAsync(CancellationToken.None);

        Assert.DoesNotContain("OldAlt", await File.ReadAllLinesAsync(Mods));
        Assert.DoesNotContain("OldAlt", await File.ReadAllLinesAsync(Whitelist));
        foreach (var file in RosterService.RosterFilesOf(_rosters.Factions))
        {
            var lines = await File.ReadAllLinesAsync(Path.Combine(_rosterDir, file));
            Assert.DoesNotContain("OldAlt", lines);
            Assert.Contains("KeptMaster", lines);
        }

        // Everybody else's lines are untouched.
        Assert.Contains("PlayerOne", await File.ReadAllLinesAsync(Path.Combine(_rosterDir, "ncrtrooper.txt")));
        var recorded = store.Read(Datasets.MasterGrants, new List<string>());
        Assert.Contains("KeptMaster", recorded);
        Assert.DoesNotContain("OldAlt", recorded);

        await before.DisposeAsync();
        await after.DisposeAsync();
    }

    [Fact]
    public async Task ARemovedMastersLiveMenuIsTakenOnTheWire()
    {
        var store = GrantStore();
        await store.WriteAsync(Datasets.MasterGrants, new List<string> { "OldAlt" });

        var after = Access(store);
        await after.StartAsync(CancellationToken.None);
        var commands = await CommandsAfterGrantAsync(3);   // sent in the background, so startup is not held up

        Assert.Contains("RemoveMenu OldAlt", commands);
        Assert.Contains("RemoveAccessManager OldAlt", commands);
        await after.StopAsync(CancellationToken.None);
        await after.DisposeAsync();
    }

    [Fact]
    public async Task AnUnrecordedMasterLeftOnEveryRosterIsPurged()
    {
        /* Granted before the grants were recorded, then taken out of .env: nothing remembered
           it. Being on every roster is the record - nobody but a master is ever put there. */
        await File.WriteAllTextAsync(Mods, "OldMaster\n");
        await File.WriteAllTextAsync(Whitelist, "OldMaster\nPlayerOne\n");
        foreach (var file in RosterService.RosterFilesOf(_rosters.Factions))
            await File.WriteAllTextAsync(Path.Combine(_rosterDir, file), "OldMaster\n");
        await File.WriteAllTextAsync(Path.Combine(_rosterDir, "ncrtrooper.txt"), "OldMaster\nPlayerOne\n");

        var access = Access(GrantStore(), Master);
        await access.StartAsync(CancellationToken.None);
        await access.StopAsync(CancellationToken.None);

        Assert.DoesNotContain("OldMaster", await File.ReadAllLinesAsync(Mods));
        Assert.Equal(["PlayerOne", Master], (await File.ReadAllLinesAsync(Whitelist)).Where(l => l.Length > 0));
        foreach (var file in RosterService.RosterFilesOf(_rosters.Factions))
            Assert.DoesNotContain("OldMaster", await File.ReadAllLinesAsync(Path.Combine(_rosterDir, file)));
        Assert.Contains("PlayerOne", await File.ReadAllLinesAsync(Path.Combine(_rosterDir, "ncrtrooper.txt")));
        await access.DisposeAsync();
    }

    [Fact]
    public async Task TheJoinedUpNameFromASpaceIsPurgedAndBothRealNamesGranted()
    {
        /* MASTER_NAMES=fki6 Holosight1 used to be ONE name, "fki6 Holosight1", written into
           every roster and whitelist. Read as two names now, the joined-up one is a leftover. */
        const string joined = "fki6 Holosight1";
        await File.WriteAllTextAsync(Mods, joined + "\n");
        await File.WriteAllTextAsync(Whitelist, joined + "\n");
        foreach (var file in RosterService.RosterFilesOf(_rosters.Factions))
            await File.WriteAllTextAsync(Path.Combine(_rosterDir, file), joined + "\n");

        var access = Access(GrantStore(), "fki6", "Holosight1");
        await access.StartAsync(CancellationToken.None);
        await access.StopAsync(CancellationToken.None);

        foreach (var path in new[] { Mods, Whitelist }.Concat(
                     RosterService.RosterFilesOf(_rosters.Factions).Select(f => Path.Combine(_rosterDir, f))))
        {
            var lines = await File.ReadAllLinesAsync(path);
            Assert.DoesNotContain(joined, lines);
            Assert.Contains("fki6", lines);
            Assert.Contains("Holosight1", lines);
        }
        await access.DisposeAsync();
    }

    [Fact]
    public async Task AMemberOfOneFactionIsNeverTakenForALeftover()
    {
        await File.WriteAllTextAsync(Path.Combine(_rosterDir, "ncrtrooper.txt"), "PlayerOne\n");

        var access = Access(GrantStore(), Master);
        await access.StartAsync(CancellationToken.None);
        await access.StopAsync(CancellationToken.None);

        Assert.Contains("PlayerOne", await File.ReadAllLinesAsync(Path.Combine(_rosterDir, "ncrtrooper.txt")));
        await access.DisposeAsync();
    }

    [Fact]
    public async Task AStillListedMasterIsLeftAlone()
    {
        var store = GrantStore();
        var access = Access(store, Master);
        await access.StartAsync(CancellationToken.None);
        await access.StopAsync(CancellationToken.None);

        var again = Access(store, Master);
        await again.StartAsync(CancellationToken.None);
        await again.StopAsync(CancellationToken.None);

        Assert.Contains(Master, await File.ReadAllLinesAsync(Mods));
        Assert.DoesNotContain(_server.Commands, c => c.StartsWith("Remove", StringComparison.Ordinal));
        await access.DisposeAsync();
        await again.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _access.StopAsync(CancellationToken.None);
        await _access.DisposeAsync();
        await _rcon.DisposeAsync();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
