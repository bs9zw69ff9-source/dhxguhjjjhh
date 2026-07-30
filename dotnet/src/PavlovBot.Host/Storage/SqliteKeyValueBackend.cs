using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Every dataset as a row in one SQLite file, with an in-memory read cache.
/// </summary>
/// <remarks>
/// Why SQLite rather than the JSON files it replaced: a JSON file is not atomic across a
/// crash, and the bot writes constantly. WAL mode gives durability the file layout never
/// had, and one file is one thing to back up.
///
/// Why a key-value table rather than real tables: the datasets are documents with different
/// shapes that change as features land, and a schema per dataset would mean a migration per
/// feature. The queries this bot runs are all "give me the whole dataset", so a schema would
/// buy nothing it does not already have.
///
/// The JSON files are still written, by <see cref="ExportToJson"/> - SQLite is the source of
/// truth, and the files are a current, human-readable backup. Without the export they would
/// be frozen at migration time, which is worse than not having them: a file that looks like
/// a backup and is six months stale is a trap.
/// </remarks>
public sealed class SqliteKeyValueBackend : IKeyValueBackend, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();

    /// <summary>
    /// Read cache. Reads are far more common than writes - every command reads several
    /// datasets - and going to SQLite for each would be a syscall per read of data that
    /// changed minutes ago.
    /// </summary>
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public SqliteKeyValueBackend(string databasePath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _logger = logger;

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _connection.Open();

        Execute("PRAGMA journal_mode = WAL");     // survive a crash mid-write
        Execute("PRAGMA busy_timeout = 5000");    // wait for a lock rather than failing the command
        Execute("PRAGMA synchronous = NORMAL");   // WAL makes FULL redundant for this workload
        Execute("CREATE TABLE IF NOT EXISTS kv (file TEXT PRIMARY KEY, data TEXT NOT NULL)");
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public string? Read(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;

            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT data FROM kv WHERE file = $file";
            command.Parameters.AddWithValue("$file", key);

            if (command.ExecuteScalar() is not string data) return null;
            _cache[key] = data;
            return data;
        }
    }

    public void Write(string key, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "INSERT INTO kv(file, data) VALUES($file, $data) " +
                "ON CONFLICT(file) DO UPDATE SET data = excluded.data";
            command.Parameters.AddWithValue("$file", key);
            command.Parameters.AddWithValue("$data", json);
            command.ExecuteNonQuery();

            // Cache only AFTER the write lands. Caching first would serve a value that
            // was never persisted, and the divergence would survive until restart.
            _cache[key] = json;
        }
    }

    /// <summary>Whether a dataset exists at all - distinct from it being empty.</summary>
    public bool Has(string key) => Read(key) is not null;

    public IReadOnlyList<string> Keys()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT file FROM kv ORDER BY file";
            using var reader = command.ExecuteReader();

            var keys = new List<string>();
            while (reader.Read()) keys.Add(reader.GetString(0));
            return keys;
        }
    }

    /// <summary>
    /// Seed a dataset only if it is absent. Used at startup so a fresh install has valid
    /// empty datasets rather than every read falling through to a fallback.
    /// </summary>
    public bool SeedIfMissing(string key, string json)
    {
        lock (_sync)
        {
            if (Has(key)) return false;
            Write(key, json);
            return true;
        }
    }

    /// <summary>
    /// Import a legacy JSON file into the database, once.
    /// </summary>
    /// <remarks>
    /// The file is PARSED before being stored. Copying it in unvalidated would move a
    /// corrupt file into the database and make the corruption authoritative - the one
    /// place it must never reach.
    /// </remarks>
    public bool ImportJsonFile(string key, string filePath)
    {
        if (Has(key) || !File.Exists(filePath)) return false;
        try
        {
            var raw = File.ReadAllText(filePath);
            using (JsonDocument.Parse(raw)) { }
            Write(key, raw);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("Could not migrate {File}: {Message}", filePath, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Write every dataset out as pretty-printed JSON, atomically.
    /// </summary>
    /// <remarks>
    /// Temp-then-rename per file, so a reader (a person with an editor, or a backup job)
    /// sees either the old file or the new one. One failing dataset does not abort the
    /// rest - a single unwritable path must not cost you every other backup.
    /// </remarks>
    public int ExportToJson(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);

        var exported = 0;
        foreach (var key in Keys())
        {
            var data = Read(key);
            if (data is null) continue;

            var path = Path.Combine(directory, $"{key}.json");
            var temp = $"{path}.exp.{Environment.ProcessId}.tmp";
            try
            {
                string pretty;
                try
                {
                    using var document = JsonDocument.Parse(data);
                    pretty = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (JsonException)
                {
                    pretty = data;   // export it as-is rather than losing it entirely
                }

                File.WriteAllText(temp, pretty);
                File.Move(temp, path, overwrite: true);
                exported++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("JSON export failed for {Key}: {Message}", key, ex.Message);
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
        return exported;
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
