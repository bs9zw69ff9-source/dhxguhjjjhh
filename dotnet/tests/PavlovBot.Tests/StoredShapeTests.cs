using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// What comes back out of the database when the row was not written by this bot.
/// </summary>
/// <remarks>
/// THIS IS THE log-tail NullReferenceException. <c>/health</c> showed one every few minutes,
/// with no stack and no player attached, and it is this: nullable reference types are a
/// COMPILE-TIME promise that System.Text.Json does not honour. A row missing an array field
/// deserializes with that property set to null, the compiler believes it cannot be null, and
/// the first <c>.Count</c> or <c>.ToList()</c> throws.
///
/// This database is SHARED WITH THE NODE BOT, which is the rollback target and writes its own
/// shape into the same keys. So "a row this bot did not write" is the normal case, not an
/// exotic one, and it lands in the hottest path there is: every login and every disconnect
/// goes through RecordAsync, and every join goes through the flag check.
///
/// The consequence was much larger than a lost card. The throw escaped into the log poll,
/// which had already advanced its file offset, so the REST OF THAT BATCH was discarded -
/// which is what made joins duplicate, leaves vanish and connection cards never appear.
/// </remarks>
public class StoredShapeTests
{
    private static SerializedStore Store(string key, string json, out MemoryBackend backend)
    {
        backend = new MemoryBackend();
        backend.Seed(key, json);
        return new SerializedStore(backend, new SystemTextJsonCodec());
    }

    // ---- the shapes that used to throw ----

    [Fact]
    public void AnAccountRowMissingItsArraysReadsAsEmpty_NotNull()
    {
        var store = Store(Datasets.KnownPlayers, """{"abc":{"id":"abc"}}""", out _);

        var accounts = store.Read(Datasets.KnownPlayers,
            new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase));

        var account = accounts["abc"];

        /* Every one of these is a dereference the live code performs. Before the fix each
           was a NullReferenceException, and the annotations said it could not be. */
        Assert.Empty(account.Names);
        Assert.Empty(account.ConfirmedIps);
        Assert.Empty(account.GuessedIps);
        Assert.Null(account.Name);
    }

    [Fact]
    public void AnAccountRowWithExplicitNullsReadsAsEmpty()
    {
        var store = Store(Datasets.KnownPlayers,
            """{"abc":{"id":"abc","names":null,"confirmedIps":null,"guessedIps":null}}""", out _);

        var account = store.Read(Datasets.KnownPlayers,
            new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase))["abc"];

        Assert.Empty(account.Names);
        Assert.Empty(account.ConfirmedIps);
        Assert.Empty(account.GuessedIps);
    }

    [Fact]
    public void TheExactCallsRecordAsyncMakesDoNotThrow()
    {
        /* RecordAsync runs on EVERY login and EVERY disconnect and opens with
           existing.Names.ToList(), existing.ConfirmedIps.ToList(), existing.GuessedIps
           .ToList(). Named here because that is the frame the exception came from. */
        var store = Store(Datasets.KnownPlayers, """{"abc":{"id":"abc"}}""", out _);
        var existing = store.Read(Datasets.KnownPlayers,
            new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase))["abc"];

        var names = existing.Names.ToList();
        var confirmed = existing.ConfirmedIps.ToList();
        var guessed = existing.GuessedIps.ToList();

        Assert.Empty(names);
        Assert.Empty(confirmed);
        Assert.Empty(guessed);
    }

    [Fact]
    public void FlagsMissingTheirArraysReadAsEmpty_NotNull()
    {
        // LoadFlags runs on every join and copies all four straight into a HashSet.
        var store = Store(Datasets.IpFlags, """{"ips":["1.2.3.4"]}""", out _);

        var flags = store.Read(Datasets.IpFlags, IpTrackingService.StoredFlags.Empty);

        Assert.Equal(["1.2.3.4"], flags.Ips);
        Assert.Empty(flags.Names);
        Assert.Empty(flags.Ids);
        Assert.Empty(flags.ManualIps);
    }

    [Fact]
    public void AFlagSetBuiltFromNullsIsEmpty_NotNull()
    {
        var set = new FlagSet(null!, null!, null!, null!);

        Assert.Empty(set.Ips);
        Assert.Empty(set.Names);
        Assert.Empty(set.Ids);
        Assert.Empty(set.ManualIps);
    }

    [Fact]
    public void AnAccountRecordBuiltFromNullsIsEmpty_NotNull()
    {
        // Constructed directly as well as deserialized: the guard belongs to the type.
        var account = new AccountRecord(null!, null!, null!, null!);

        Assert.Empty(account.Names);
        Assert.Empty(account.ConfirmedIps);
        Assert.Empty(account.GuessedIps);
        Assert.Equal("", account.Id);
    }

    [Fact]
    public void WithExpressionsKeepTheGuard()
    {
        /* RecordAsync writes back with `existing with { Names = names, ... }`. A copy
           constructor that bypassed the initializers would reintroduce the null one layer
           down, where it would be even harder to find. */
        var account = AccountRecord.Empty("abc") with { Names = null!, ConfirmedIps = null! };

        Assert.Empty(account.Names);
        Assert.Empty(account.ConfirmedIps);
    }
}
