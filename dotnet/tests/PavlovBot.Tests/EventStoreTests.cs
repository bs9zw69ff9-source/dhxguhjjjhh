using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Events;
using PavlovBot.Host.Events;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The timeline store: append-only, indexed, and bounded by retention.
/// </summary>
/// <remarks>
/// WHY THIS IS NOT A DATASET IN SerializedStore, which is where every other thing this bot
/// persists lives. That store rewrites a whole JSON document per write, which is right for
/// bans and warnings and catastrophic for a timeline: appending one join at ten thousand
/// events would serialise ten thousand. <see cref="AppendCostDoesNotGrowWithTheTableSize"/>
/// is the test that would fail if somebody moved this back.
/// </remarks>
public class EventStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pavlov-events-{Guid.NewGuid():N}");

    private readonly SqliteEventStore _store;

    public EventStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteEventStore(Path.Combine(_directory, "bot.db"), NullLogger<SqliteEventStore>.Instance);
    }

    private static ServerEvent Event(
        string kind = "player.join",
        EventCategory category = EventCategory.Player,
        string? player = "Alice",
        string? server = "server1",
        string? actor = null,
        DateTimeOffset? at = null) =>
        new(at ?? DateTimeOffset.UtcNow, category, kind, player, server, actor, "detail");

    private Task Append(params ServerEvent[] events) => _store.AppendAsync(events);

    // ---- appending and reading back ----

    [Fact]
    public async Task AnAppendedEventReadsBack()
    {
        await Append(Event());

        var found = Assert.Single(_store.Query(new EventQuery(10)));

        Assert.Equal("player.join", found.Kind);
        Assert.Equal("Alice", found.Player);
        Assert.Equal("server1", found.Server);
    }

    /// <summary>Newest first, which is the order an investigation reads in.</summary>
    [Fact]
    public async Task EventsComeBackNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;

        await Append(
            Event(kind: "first", at: now.AddMinutes(-10)),
            Event(kind: "second", at: now.AddMinutes(-5)),
            Event(kind: "third", at: now));

        var found = _store.Query(new EventQuery(10));

        Assert.Equal(["third", "second", "first"], found.Select(e => e.Kind));
    }

    /// <summary>
    /// Events at the same instant still come back in a stable order.
    /// </summary>
    /// <remarks>
    /// A batch written together shares a millisecond, and ordering by time alone would leave
    /// SQLite free to return them in any order - so a timeline could show a ban before the
    /// join that caused it, differently on each refresh. The id breaks the tie.
    /// </remarks>
    [Fact]
    public async Task EventsSharingAnInstantKeepInsertionOrder()
    {
        var instant = DateTimeOffset.UtcNow;

        await Append(
            Event(kind: "join", at: instant),
            Event(kind: "warn", at: instant),
            Event(kind: "ban", at: instant));

        var found = _store.Query(new EventQuery(10));

        Assert.Equal(["ban", "warn", "join"], found.Select(e => e.Kind));
    }

    // ---- filtering ----

    [Fact]
    public async Task FilteringByPlayerIsCaseInsensitive()
    {
        await Append(Event(player: "Alice"), Event(player: "Bob"));

        var found = _store.Query(new EventQuery(10, Player: "alice"));

        Assert.Equal("Alice", Assert.Single(found).Player);
    }

    /// <summary>The staff view asks who DID it, not who it was done to.</summary>
    [Fact]
    public async Task FilteringByActorFindsWhatOneStaffMemberDid()
    {
        await Append(
            Event(player: "Alice", actor: "ModOne", kind: "staff.ban", category: EventCategory.Staff),
            Event(player: "Bob", actor: "ModTwo", kind: "staff.kick", category: EventCategory.Staff));

        var found = _store.Query(new EventQuery(10, Actor: "modone"));

        Assert.Equal("staff.ban", Assert.Single(found).Kind);
    }

    [Fact]
    public async Task FilteringByCategoryAndServerNarrows()
    {
        await Append(
            Event(category: EventCategory.Security, server: "server1"),
            Event(category: EventCategory.Player, server: "server1"),
            Event(category: EventCategory.Security, server: "server2"));

        Assert.Single(_store.Query(new EventQuery(10, Category: EventCategory.Security, Server: "server1")));
        Assert.Equal(2, _store.Query(new EventQuery(10, Category: EventCategory.Security)).Count);
    }

    [Fact]
    public async Task TheTimeWindowIsRespected()
    {
        var now = DateTimeOffset.UtcNow;

        await Append(
            Event(kind: "old", at: now.AddHours(-48)),
            Event(kind: "recent", at: now.AddMinutes(-30)));

        var found = _store.Query(new EventQuery(10, Since: now.AddHours(-6)));

        Assert.Equal("recent", Assert.Single(found).Kind);
    }

    /// <summary>
    /// A player name cannot break out of the query.
    /// </summary>
    /// <remarks>
    /// Every filter originates in a Discord option somebody typed, and an in-game name is
    /// about as attacker-influenced as a string gets. Parameterised throughout; this is what
    /// asserts it stays that way.
    /// </remarks>
    [Fact]
    public async Task AHostileNameIsAParameterNotSql()
    {
        await Append(Event(player: "Alice"));

        var hostile = _store.Query(new EventQuery(10, Player: "'; DROP TABLE events; --"));

        Assert.Empty(hostile);

        // The table is still there, which is the actual assertion.
        Assert.Equal(1, _store.Count());
    }

    // ---- limits ----

    [Fact]
    public async Task TheLimitIsHonouredAndCapped()
    {
        await _store.AppendAsync([.. Enumerable.Range(0, 50).Select(i => Event(kind: $"e{i}"))]);

        Assert.Equal(5, _store.Query(new EventQuery(5)).Count);

        // Asking for more than the ceiling gets the ceiling, not everything.
        Assert.Equal(50, _store.Query(new EventQuery(EventQuery.MaxLimit * 10)).Count);
        Assert.Equal(EventQuery.MaxLimit, new EventQuery(EventQuery.MaxLimit * 10).EffectiveLimit);
    }

    // ---- retention ----

    [Fact]
    public async Task PruningRemovesOnlyWhatIsPastTheHorizon()
    {
        var now = DateTimeOffset.UtcNow;

        await Append(
            Event(kind: "ancient", at: now.AddDays(-120)),
            Event(kind: "recent", at: now.AddDays(-1)));

        var removed = await _store.PruneAsync(now.AddDays(-90));

        Assert.Equal(1, removed);
        Assert.Equal("recent", Assert.Single(_store.Query(new EventQuery(10))).Kind);
    }

    // ---- normalisation ----

    /// <summary>
    /// An over-long detail is truncated rather than rejected.
    /// </summary>
    /// <remarks>
    /// Losing a timeline entry entirely because its detail was long is worse than losing the
    /// tail of a sentence, and Discord could not have rendered the rest anyway.
    /// </remarks>
    [Fact]
    public async Task OverlongFieldsAreTruncatedNotRejected()
    {
        await Append(Event() with { Detail = new string('x', ServerEvent.MaxDetail * 3) });

        var found = Assert.Single(_store.Query(new EventQuery(10)));

        Assert.Equal(ServerEvent.MaxDetail, found.Detail!.Length);
    }

    /// <summary>Empty strings are stored as null, so a query need only test for one.</summary>
    [Fact]
    public async Task BlankFieldsBecomeNull()
    {
        await Append(Event(player: "   ", server: "", actor: null));

        var found = Assert.Single(_store.Query(new EventQuery(10)));

        Assert.Null(found.Player);
        Assert.Null(found.Server);
        Assert.Null(found.Actor);
    }

    // ---- the reason this is a table ----

    /// <summary>
    /// APPENDING STAYS FLAT AS THE TABLE GROWS. The whole point of the storage change.
    /// </summary>
    /// <remarks>
    /// In the document store an append rewrites the whole dataset, so the cost of recording
    /// one join is proportional to every join already recorded. Here it is an indexed insert
    /// and does not care how much is already there.
    ///
    /// A TIMING TEST, WHICH IS NORMALLY A BAD IDEA, so the threshold is enormous: it fails
    /// only if appends have become super-linear, which is what a return to document storage
    /// would look like. A tight bound here would flake on a loaded CI box and get deleted,
    /// which would be worse than not having it.
    /// </remarks>
    [Fact]
    public async Task AppendCostDoesNotGrowWithTheTableSize()
    {
        async Task<double> BatchMillis()
        {
            var batch = Enumerable.Range(0, 200).Select(i => Event(kind: $"k{i}")).ToArray();
            var clock = Stopwatch.StartNew();
            await _store.AppendAsync(batch);
            return clock.Elapsed.TotalMilliseconds;
        }

        var first = await BatchMillis();

        // Fill it out so a document-store implementation would be rewriting 10k rows.
        for (var i = 0; i < 50; i++) await BatchMillis();

        var last = await BatchMillis();

        Assert.True(_store.Count() > 10_000, "the table should be large enough for this to mean something");
        Assert.True(last < Math.Max(first, 50) * 10,
            $"appending got {last / Math.Max(first, 0.01):F1}x slower as the table grew - is this still an indexed insert?");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _store.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
