using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A lifted ban must not come back on its own.
/// </summary>
/// <remarks>
/// THE REPORTED FAILURE: "bans are sticking". Staff ran /unban, it reported success, the
/// record went, the native unban was sent - and a few minutes later the player was banned
/// again, with nobody having done anything.
///
/// THE LOOP. ModsaveBanlist syncs the game's own ban file every five minutes, IMPORTING
/// first, and the importer treats any name in that file which is not in the store as a ban to
/// create. An unban removed the store record and left the FILE untouched, so:
///
///   1. /unban removes the record and sends Unban. The file still lists them.
///   2. Within five minutes, ImportAsync reads the file, does not recognise the name, and
///      re-creates the ban record.
///   3. ExportAsync writes it back to the file.
///   4. The ban sweep sees the record and enforces it.
///
/// Every step behaved exactly as designed. The failure was that no step knew the ban had been
/// lifted deliberately, so the file was treated as the more recent truth when it was the
/// stalest thing in the system.
///
/// The store's <see cref="Datasets.UnbanTombstones"/> is what carries that intent across the
/// two subsystems.
/// </remarks>
public class BanResurrectionTests : IDisposable
{
    private const string Name = "Pardoned";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pavlov-resurrect-{Guid.NewGuid():N}");

    private readonly SerializedStore _store;
    private readonly string _banFile;
    private readonly ModsaveBanlist _modsave;

    public BanResurrectionTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _banFile = Path.Combine(_directory, "blacklist.txt");
        _modsave = new ModsaveBanlist(_banFile, _store, NullLogger<ModsaveBanlist>.Instance);
    }

    /// <summary>The game's ban file, in the format Pavlov writes.</summary>
    private Task WriteBanFileAsync(string name, string unban = "Permanent") =>
        File.WriteAllTextAsync(_banFile, $"{name}\nReason: griefing\nUnban: {unban}\n\n");

    private List<BanRecord> Bans() => _store.Read<List<BanRecord>>(Datasets.TempBans, []);

    private Task TombstoneAsync(string name, DateTimeOffset at) =>
        _store.UpdateAsync(Datasets.UnbanTombstones,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            t => { t[name] = at; return t; });

    /// <summary>
    /// THE REGRESSION. A file entry for a deliberately lifted player is not re-imported.
    /// </summary>
    [Fact]
    public async Task ADeliberatelyLiftedBanIsNotResurrectedByTheImporter()
    {
        await WriteBanFileAsync(Name);
        await TombstoneAsync(Name, DateTimeOffset.UtcNow);

        await _modsave.ImportAsync();

        Assert.Empty(Bans());
    }

    /// <summary>
    /// The control: without the tombstone the importer DOES create it.
    /// </summary>
    /// <remarks>
    /// Keeps the test above honest. An importer that had simply stopped working would satisfy
    /// it, and that would be a far worse bug - bans issued from the in-game admin menu would
    /// silently never be recorded.
    /// </remarks>
    [Fact]
    public async Task AnUntombstonedFileEntryIsStillImported()
    {
        await WriteBanFileAsync(Name);

        await _modsave.ImportAsync();

        Assert.Single(Bans());
        Assert.Equal(Name, Bans()[0].PlayerId);
    }

    /// <summary>
    /// A tombstone expires, so banning somebody again months later still works.
    /// </summary>
    /// <remarks>
    /// A permanent tombstone would mean a player who was once pardoned could never be banned
    /// through the file again - a silent hole that would only be discovered when somebody
    /// needed it closed.
    /// </remarks>
    [Fact]
    public async Task AnExpiredTombstoneNoLongerBlocksTheImport()
    {
        await WriteBanFileAsync(Name);
        await TombstoneAsync(Name, DateTimeOffset.UtcNow - ModsaveBanlist.TombstoneLife - TimeSpan.FromDays(1));

        await _modsave.ImportAsync();

        Assert.Single(Bans());
    }

    /// <summary>A tombstone for one player does not protect anybody else.</summary>
    [Fact]
    public async Task ATombstoneOnlyCoversTheNameItNames()
    {
        await File.WriteAllTextAsync(_banFile,
            $"{Name}\nReason: griefing\nUnban: Permanent\n\nSomebodyElse\nReason: cheating\nUnban: Permanent\n\n");

        await TombstoneAsync(Name, DateTimeOffset.UtcNow);

        await _modsave.ImportAsync();

        Assert.Equal("SomebodyElse", Assert.Single(Bans()).PlayerId);
    }

    /// <summary>
    /// The export then removes them from the file, so the loop cannot restart.
    /// </summary>
    /// <remarks>
    /// The tombstone stops the record being re-created; the export is what stops the GAME
    /// refusing them, since the server reads this file itself. Both halves are needed, and
    /// this is the one that ends the cycle rather than suppressing it.
    /// </remarks>
    [Fact]
    public async Task ExportingRemovesALiftedPlayerFromTheFile()
    {
        await WriteBanFileAsync(Name);
        await TombstoneAsync(Name, DateTimeOffset.UtcNow);

        // A sync is import-then-export: the import must skip them, and the export must then
        // write a file that no longer lists them.
        await _modsave.SyncAsync();

        var contents = await File.ReadAllTextAsync(_banFile);

        Assert.DoesNotContain(Name, contents, StringComparison.Ordinal);
        Assert.Empty(Bans());
    }

    /// <summary>A ban that is still in force survives the sync untouched.</summary>
    /// <remarks>
    /// The other control. Making the sync drop everything would pass every test above and
    /// unban the entire server.
    /// </remarks>
    [Fact]
    public async Task ABanStillInForceSurvivesASync()
    {
        await _store.WriteAsync(Datasets.TempBans, new List<BanRecord>
        {
            new()
            {
                PlayerId = "StillBanned",
                Reason = "cheating",
                Moderator = "mod",
                At = DateTimeOffset.UtcNow,
                Permanent = true,
                DurationLabel = "Permanent",
            },
        });

        await _modsave.SyncAsync();

        Assert.Single(Bans());
        Assert.Contains("StillBanned", await File.ReadAllTextAsync(_banFile), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
