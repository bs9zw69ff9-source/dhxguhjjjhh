using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Events;

namespace PavlovBot.Host.Events;

/// <summary>Appending to and reading from the timeline.</summary>
/// <remarks>
/// AN INTERFACE so the recorder, the commands and the tests do not depend on SQLite, and so a
/// deployment with the timeline switched off gets a no-op rather than a null check at every
/// call site.
/// </remarks>
public interface IEventStore
{
    /// <summary>Append events. They are never edited afterwards.</summary>
    Task AppendAsync(IReadOnlyCollection<ServerEvent> events, CancellationToken ct = default);

    /// <summary>Read the timeline, newest first.</summary>
    IReadOnlyList<ServerEvent> Query(EventQuery query);

    /// <summary>How many events are stored.</summary>
    long Count();

    /// <summary>Delete events older than the cutoff. Returns how many went.</summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default);

    /// <summary>
    /// Counts grouped by one column, computed IN SQL.
    /// </summary>
    /// <remarks>
    /// THE WHOLE REASON THE TABLE EXISTS. "How many unique players in thirty days" answered by
    /// fetching thirty days of rows and counting them in memory is the full scan this store was
    /// built to avoid - it would be a slower version of the document store with extra steps.
    /// GROUP BY runs against the index and returns tens of rows instead of hundreds of
    /// thousands.
    /// </remarks>
    IReadOnlyList<(string Key, long Count)> CountBy(EventGrouping grouping, EventQuery query);

    /// <summary>How many DISTINCT values of a column match, computed in SQL.</summary>
    long CountDistinct(EventGrouping grouping, EventQuery query);
}

/// <summary>Which column an aggregate groups by.</summary>
/// <remarks>
/// AN ENUM, NOT A STRING. The column name goes into SQL text that cannot be parameterised -
/// SQLite does not accept a parameter where an identifier belongs - so the only safe way to
/// build it is from a closed set the caller cannot extend. A string parameter here would be
/// an injection point wearing a helpful name.
/// </remarks>
public enum EventGrouping
{
    Kind,
    Category,
    Player,
    Server,
    Actor,

    /// <summary>The UTC day, as yyyy-MM-dd. For "players per day" and retention.</summary>
    Day,

    /// <summary>The UTC hour, as yyyy-MM-dd HH. For "players per hour".</summary>
    Hour,
}

/// <summary>
/// The timeline, as a real table in the existing SQLite file.
/// </summary>
/// <remarks>
/// WHY THIS IS NOT A DATASET IN SerializedStore, which is where everything else in this bot
/// lives. That store holds one JSON document per dataset and rewrites the WHOLE document on
/// every write. For bans and warnings that is correct and its own docs explain why: the
/// queries are all "give me the whole dataset", and a schema would buy nothing.
///
/// A timeline breaks both halves of that. It is append-heavy - every join, leave, ban and map
/// change - so a document rewrite makes each append cost O(rows already stored). At ten
/// thousand events, ingesting one join means serialising ten thousand. And the queries are
/// emphatically not "give me everything": they are "this player, last six hours", which is an
/// index lookup and cannot be anything else without reading the lot.
///
/// SAME DATABASE FILE, deliberately. One file to back up, one WAL, one set of pragmas, and
/// the export that keeps the JSON mirror current still sees a consistent snapshot. A second
/// database beside the first would be a second thing to lose.
///
/// THE INDEXES ARE THE POINT. Each covers one filter paired with time, because every query
/// this serves is time-bounded - a timeline with no time bound is a table scan wearing a
/// filter. Without them this is a slower version of the document store it replaced.
/// </remarks>
public sealed class SqliteEventStore : IEventStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    /// <summary>
    /// One connection, guarded. SQLite serialises writers anyway and the write path here is a
    /// single batched transaction from one background drain, so a pool would add moving parts
    /// without adding throughput.
    /// </summary>
    private readonly Lock _sync = new();

    public SqliteEventStore(string databasePath, ILogger<SqliteEventStore> logger)
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

        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA busy_timeout = 5000");
        Execute("PRAGMA synchronous = NORMAL");

        /* CREATE IF NOT EXISTS, and nothing else. This is additive to a database that already
           holds the kv table, so an existing installation gains the table on first start with
           no migration step and no manual action - and a rollback to a build without this
           leaves a table nothing reads, which is harmless. That is the whole migration story
           and it is deliberately this boring. */
        Execute("""
            CREATE TABLE IF NOT EXISTS events (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                at       INTEGER NOT NULL,
                category INTEGER NOT NULL,
                kind     TEXT    NOT NULL,
                player   TEXT,
                server   TEXT,
                actor    TEXT,
                detail   TEXT
            )
            """);

        /* AT IS LAST IN EVERY COMPOSITE, which is the part that is easy to get backwards.
           The filter column is the equality test and time is the range, so the index has to
           be (filter, at) - the other order makes SQLite scan a time slice and test each row.

           COLLATE NOCASE on player and actor, because names are compared case-insensitively
           everywhere else in this bot and an index built with the default collation is simply
           not used by a NOCASE comparison. That failure is silent: the query is correct and
           quietly scans. */
        Execute("CREATE INDEX IF NOT EXISTS ix_events_at ON events(at DESC)");
        Execute("CREATE INDEX IF NOT EXISTS ix_events_player_at ON events(player COLLATE NOCASE, at DESC)");
        Execute("CREATE INDEX IF NOT EXISTS ix_events_server_at ON events(server, at DESC)");
        Execute("CREATE INDEX IF NOT EXISTS ix_events_category_at ON events(category, at DESC)");
        Execute("CREATE INDEX IF NOT EXISTS ix_events_actor_at ON events(actor COLLATE NOCASE, at DESC)");
    }

    private void Execute(string sql)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Append a batch inside ONE transaction.
    /// </summary>
    /// <remarks>
    /// The batching is the performance story. A transaction per event means an fsync per
    /// event, and a full server reconnecting after a map change produces a burst of them.
    /// One transaction for the batch turns that into a single commit.
    /// </remarks>
    public Task AppendAsync(IReadOnlyCollection<ServerEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return Task.CompletedTask;

        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO events (at, category, kind, player, server, actor, detail)
                VALUES ($at, $category, $kind, $player, $server, $actor, $detail)
                """;

            // Parameters created once and rebound per row: preparing this statement 5,000
            // times for a backfill is most of the cost of a backfill.
            var at = command.Parameters.Add("$at", SqliteType.Integer);
            var category = command.Parameters.Add("$category", SqliteType.Integer);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var player = command.Parameters.Add("$player", SqliteType.Text);
            var server = command.Parameters.Add("$server", SqliteType.Text);
            var actor = command.Parameters.Add("$actor", SqliteType.Text);
            var detail = command.Parameters.Add("$detail", SqliteType.Text);

            foreach (var raw in events)
            {
                ct.ThrowIfCancellationRequested();
                if (raw is null) continue;

                var e = raw.Normalised();

                at.Value = e.At.ToUnixTimeMilliseconds();
                category.Value = (int)e.Category;
                kind.Value = e.Kind;
                player.Value = (object?)e.Player ?? DBNull.Value;
                server.Value = (object?)e.Server ?? DBNull.Value;
                actor.Value = (object?)e.Actor ?? DBNull.Value;
                detail.Value = (object?)e.Detail ?? DBNull.Value;

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The WHERE clauses and their parameters for a query.
    /// </summary>
    /// <remarks>
    /// SHARED BY THE ROW READ AND THE AGGREGATES, so a filter cannot mean one thing when
    /// listing and another when counting - which would make a total disagree with the rows
    /// it supposedly totals.
    ///
    /// BUILT FROM PARAMETERS, never concatenated values. Every filter originates in a Discord
    /// option somebody typed, and a player name is exactly the sort of attacker-influenced
    /// string that ends up in a WHERE clause.
    /// </remarks>
    private static (List<string> Where, List<(string Name, object Value)> Parameters) Filters(EventQuery query)
    {
        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        void Filter(string clause, string name, object value)
        {
            where.Add(clause);
            parameters.Add((name, value));
        }

        if (query.Since is { } since) Filter("at >= $since", "$since", since.ToUnixTimeMilliseconds());
        if (query.Until is { } until) Filter("at < $until", "$until", until.ToUnixTimeMilliseconds());
        if (query.Category is { } category) Filter("category = $category", "$category", (int)category);
        if (!string.IsNullOrWhiteSpace(query.Player)) Filter("player = $player COLLATE NOCASE", "$player", query.Player.Trim());
        if (!string.IsNullOrWhiteSpace(query.Server)) Filter("server = $server", "$server", query.Server.Trim());
        if (!string.IsNullOrWhiteSpace(query.Actor)) Filter("actor = $actor COLLATE NOCASE", "$actor", query.Actor.Trim());

        return (where, parameters);
    }

    public IReadOnlyList<ServerEvent> Query(EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (where, parameters) = Filters(query);

        var sql = "SELECT at, category, kind, player, server, actor, detail FROM events" +
                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  " ORDER BY at DESC, id DESC LIMIT $limit";

        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.Parameters.AddWithValue("$limit", query.EffectiveLimit);

            using var reader = command.ExecuteReader();

            var results = new List<ServerEvent>();
            while (reader.Read())
            {
                results.Add(new ServerEvent(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    (EventCategory)reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
            return results;
        }
    }

    /// <summary>The SQL expression for a grouping. Never built from caller input.</summary>
    /// <remarks>
    /// A switch over a closed enum, so every value this can produce is written here in the
    /// source. See <see cref="EventGrouping"/> for why it cannot be a string.
    /// </remarks>
    private static string Column(EventGrouping grouping) => grouping switch
    {
        EventGrouping.Kind => "kind",
        EventGrouping.Category => "category",
        EventGrouping.Player => "player",
        EventGrouping.Server => "server",
        EventGrouping.Actor => "actor",
        // at is milliseconds; SQLite's date functions want seconds.
        EventGrouping.Day => "strftime('%Y-%m-%d', at / 1000, 'unixepoch')",
        EventGrouping.Hour => "strftime('%Y-%m-%d %H', at / 1000, 'unixepoch')",
        _ => throw new ArgumentOutOfRangeException(nameof(grouping)),
    };

    public IReadOnlyList<(string Key, long Count)> CountBy(EventGrouping grouping, EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var column = Column(grouping);
        var (where, parameters) = Filters(query);

        /* NULLS EXCLUDED. Grouping by player over a set that includes server restarts would
           report a bucket of nameless events as though it were a player, and it would usually
           be the largest one. */
        var sql = $"SELECT {column} AS k, COUNT(*) FROM events" +
                  Clause(where, $"{column} IS NOT NULL") +
                  $" GROUP BY k ORDER BY COUNT(*) DESC LIMIT $limit";

        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.Parameters.AddWithValue("$limit", query.EffectiveLimit);

            using var reader = command.ExecuteReader();

            var results = new List<(string, long)>();
            while (reader.Read())
            {
                var key = reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "";
                results.Add((key, reader.GetInt64(1)));
            }
            return results;
        }
    }

    public long CountDistinct(EventGrouping grouping, EventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var column = Column(grouping);
        var (where, parameters) = Filters(query);

        var sql = $"SELECT COUNT(DISTINCT {column}) FROM events" + Clause(where, $"{column} IS NOT NULL");

        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string Clause(IReadOnlyList<string> where, params string[] extra) =>
        " WHERE " + string.Join(" AND ", where.Concat(extra));

    public long Count()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM events";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Drop events past the retention horizon.
    /// </summary>
    /// <remarks>
    /// THE ONLY THING THAT DELETES A ROW. There is no edit path and no targeted delete, so a
    /// timeline entry cannot be made to disappear because somebody would rather it had not
    /// happened - which is the property that lets this be read as evidence.
    ///
    /// Retention is not optional at any real scale: a busy server produces tens of thousands
    /// of joins a month, and a table nothing ever prunes eventually becomes the largest thing
    /// in the file and the slowest thing to query.
    /// </remarks>
    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM events WHERE at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", olderThan.ToUnixTimeMilliseconds());

            var removed = command.ExecuteNonQuery();
            if (removed > 0) _logger.LogInformation("Pruned {Count} timeline event(s) older than {Cutoff}", removed, olderThan);
            return Task.FromResult(removed);
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
    }
}

/// <summary>The timeline switched off. Records nothing and answers nothing.</summary>
/// <remarks>
/// A NULL OBJECT rather than a nullable dependency, so nothing that records an event needs a
/// null check. A forgotten null check on a recording call is a NullReferenceException on a
/// player join, which is the path where an exception costs the most.
/// </remarks>
public sealed class NullEventStore : IEventStore
{
    public Task AppendAsync(IReadOnlyCollection<ServerEvent> events, CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<ServerEvent> Query(EventQuery query) => [];
    public long Count() => 0;
    public Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct = default) => Task.FromResult(0);
    public IReadOnlyList<(string Key, long Count)> CountBy(EventGrouping grouping, EventQuery query) => [];
    public long CountDistinct(EventGrouping grouping, EventQuery query) => 0;
}
