using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

public class SqliteKeyValueBackendTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-sqlite-" + Guid.NewGuid().ToString("N"));

    private SqliteKeyValueBackend Backend() =>
        new(Path.Combine(_directory, "bot.db"), NullLogger.Instance);

    [Fact]
    public void ARoundTripPreservesTheValue()
    {
        using var backend = Backend();
        backend.Write("tempbans", """[{"playerId":"alice"}]""");
        Assert.Equal("""[{"playerId":"alice"}]""", backend.Read("tempbans"));
    }

    [Fact]
    public void AnAbsentDatasetReadsAsNull_WhichIsNotTheSameAsEmpty()
    {
        // "Never written" and "written as empty" are different states and the seeding
        // logic branches on which one it is.
        using var backend = Backend();
        Assert.Null(backend.Read("never_written"));
        Assert.False(backend.Has("never_written"));

        backend.Write("written_empty", "[]");
        Assert.True(backend.Has("written_empty"));
    }

    [Fact]
    public void DataSurvivesReopeningTheDatabase()
    {
        using (var first = Backend()) first.Write("tempbans", "[1,2,3]");
        using var second = Backend();
        Assert.Equal("[1,2,3]", second.Read("tempbans"));
    }

    [Fact]
    public void SeedingOnlyAppliesToAnAbsentDataset()
    {
        // A seed that overwrote live data would silently wipe every ban on restart.
        using var backend = Backend();
        Assert.True(backend.SeedIfMissing("tempbans", "[]"));
        backend.Write("tempbans", """["real data"]""");
        Assert.False(backend.SeedIfMissing("tempbans", "[]"));
        Assert.Equal("""["real data"]""", backend.Read("tempbans"));
    }

    [Fact]
    public void ACorruptLegacyFileIsNotImported()
    {
        /* Copying it in unvalidated would move the corruption into the database and make
           it authoritative - the one place it must never reach. */
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, "{ this is not json");

        using var backend = Backend();
        Assert.False(backend.ImportJsonFile("legacy", path));
        Assert.Null(backend.Read("legacy"));
    }

    [Fact]
    public void AValidLegacyFileIsImportedOnce()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(path, """{"a":1}""");

        using var backend = Backend();
        Assert.True(backend.ImportJsonFile("legacy", path));
        Assert.Equal("""{"a":1}""", backend.Read("legacy"));

        // Second call is a no-op: the database has moved on and must not be overwritten
        // by a stale file.
        File.WriteAllText(path, """{"a":999}""");
        Assert.False(backend.ImportJsonFile("legacy", path));
        Assert.Equal("""{"a":1}""", backend.Read("legacy"));
    }

    [Fact]
    public void ExportWritesEveryDatasetAsReadableJson()
    {
        // Without the export the .json files stay frozen at migration time, and a file
        // that looks like a backup and is six months stale is a trap.
        using var backend = Backend();
        backend.Write("tempbans", """[{"playerId":"alice"}]""");
        backend.Write("mutes", "{}");

        var output = Path.Combine(_directory, "export");
        Assert.Equal(2, backend.ExportToJson(output));
        Assert.Contains("\n", File.ReadAllText(Path.Combine(output, "tempbans.json")), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));
    }

    [Fact]
    public async Task TheStoreSerialisesOverSqliteToo()
    {
        using var backend = Backend();
        var store = new SerializedStore(backend, new SystemTextJsonCodec());

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            store.UpdateAsync<List<int>>("counter", [], current => [.. current, 1])));

        Assert.Equal(50, store.Read<List<int>>("counter", []).Count);
    }

    [Fact]
    public void EveryDatasetHasASeedAndEverySeedIsValidJson()
    {
        /* A dataset whose natural empty value is [] but which is seeded {} deserialises
           to the fallback on every read - which looks like it works right up until a write
           turns the wrong shape into the stored one. */
        Assert.NotEmpty(Datasets.All);
        foreach (var name in Datasets.All)
        {
            var seed = Datasets.Seeds[name];
            using var document = System.Text.Json.JsonDocument.Parse(seed);
            Assert.True(document.RootElement.ValueKind is System.Text.Json.JsonValueKind.Array
                                                       or System.Text.Json.JsonValueKind.Object,
                $"{name} is seeded with something that is neither an array nor an object");
        }
    }

    [Fact]
    public void DatasetNamesAreUniqueAndFileSafe()
    {
        // They become file names on export and keys in a shared database.
        Assert.Equal(Datasets.All.Count, Datasets.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Datasets.All, n => Assert.Equal(-1, n.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars())));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }
}
