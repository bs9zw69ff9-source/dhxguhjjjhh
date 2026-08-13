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

    /* ─── IN-GAME BANS ARE FILED UNDER AN EOS ID ───────────────────────────────────────

       THE REPORTED FAILURE: "players are still being banned from unreadable number thing."

       Pavlov writes the UniqueID as the block header for a ban issued from the in-game admin
       menu, so the file says 0002abc… where the bot's own bans say a username. The importer
       resolves that id to a name through the account registry, which is what stops the ban
       list showing the same player as two different entries.

       That resolution is where both bugs live, and neither is visible from the ban list:

         THE STORE IS KEYED ON THE RESOLVED NAME, THE FILE ON THE ID. The de-duplication check
         compares the file's id against a set of stored names, never matches, and re-imports
         the same ban every five minutes forever. An /unban removes one record out of however
         many have piled up and the player stays banned.

         THE TOMBSTONE IS KEYED ON WHAT STAFF TYPED. They unban "Bob"; the importer looks up
         "0002abc…". The lift is invisible to it and the ban comes straight back.

       Both need the id and the name treated as one identity. */

    private const string AccountId = "0002abcdef0123456789abcdef012345";

    /// <summary>An importer that knows this id belongs to <see cref="Name"/>.</summary>
    private ModsaveBanlist Resolving() =>
        new(_banFile, _store, NullLogger<ModsaveBanlist>.Instance,
            time: null,
            resolveName: id => string.Equals(id, AccountId, StringComparison.Ordinal) ? Name : null);

    /// <summary>
    /// THE REGRESSION. A resolved in-game ban is imported once, not once per sync.
    /// </summary>
    [Fact]
    public async Task AnInGameBanIsNotReImportedOnEverySync()
    {
        await WriteBanFileAsync(AccountId);
        var modsave = Resolving();

        await modsave.ImportAsync();
        await modsave.ImportAsync();
        await modsave.ImportAsync();

        var ban = Assert.Single(Bans());
        Assert.Equal(Name, ban.PlayerId);
    }

    /// <summary>
    /// THE REGRESSION. Unbanning by name blocks the file entry that names the id.
    /// </summary>
    /// <remarks>
    /// Staff have the name in front of them - it is what the ban list shows and what
    /// /unban autocompletes. Requiring them to know the EOS id to make an unban stick is
    /// not a workaround, it is the bug.
    /// </remarks>
    [Fact]
    public async Task ATombstoneUnderTheNameBlocksTheFileEntryUnderTheId()
    {
        await WriteBanFileAsync(AccountId);
        await TombstoneAsync(Name, DateTimeOffset.UtcNow);

        await Resolving().ImportAsync();

        Assert.Empty(Bans());
    }

    /// <summary>And the other direction: unbanning by id blocks it too.</summary>
    /// <remarks>
    /// A ban the registry cannot resolve shows the id, so that is what staff copy into
    /// /unban. Covering only the name would leave exactly the players whose bans are already
    /// the hardest to read.
    /// </remarks>
    [Fact]
    public async Task ATombstoneUnderTheIdBlocksItAsWell()
    {
        await WriteBanFileAsync(AccountId);
        await TombstoneAsync(AccountId, DateTimeOffset.UtcNow);

        await Resolving().ImportAsync();

        Assert.Empty(Bans());
    }

    /// <summary>
    /// The id is kept on the record, not thrown away once it has been resolved to a name.
    /// </summary>
    /// <remarks>
    /// Pavlov's Ban, Kick and Unban all take a UniqueId. A name is accepted and silently does
    /// nothing, so a record carrying only the resolved name depends on the account registry
    /// still knowing that name at lift time. Keeping the id is what makes the lift work
    /// regardless.
    /// </remarks>
    [Fact]
    public async Task TheResolvedRecordKeepsTheAccountId()
    {
        await WriteBanFileAsync(AccountId);

        await Resolving().ImportAsync();

        Assert.Equal(AccountId, Assert.Single(Bans()).UniqueId);
    }

    /// <summary>An entry that resolves to nothing is still imported, under its id.</summary>
    /// <remarks>
    /// The control. A ban nobody can name is still a ban, and dropping it would be far worse
    /// than showing it awkwardly - which is the whole reason the id survives resolution.
    /// </remarks>
    [Fact]
    public async Task AnUnresolvableIdIsStillImportedUnderTheId()
    {
        await WriteBanFileAsync("0002ffffffffffffffffffffffffffff");

        await Resolving().ImportAsync();

        Assert.Equal("0002ffffffffffffffffffffffffffff", Assert.Single(Bans()).PlayerId);
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
