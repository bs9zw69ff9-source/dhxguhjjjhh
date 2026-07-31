using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The two bots share one bot.db. These are the tests that make that survivable.
/// </summary>
/// <remarks>
/// The failure this guards against is not a crash. The C# side would read a ban list it
/// could not parse, <c>SerializedStore</c> would hand back the caller's fallback of
/// <c>[]</c>, and the first command that wrote a ban would persist that empty list plus one
/// entry - silently wiping every ban on the server, with nothing in any log saying so.
/// </remarks>
public class NodeCompatibilityTests
{
    private static readonly SystemTextJsonCodec Codec = new();

    /// <summary>A ban list exactly as the RUNNING Node bot writes it.</summary>
    private const string NodeBanList = """
        [{"playerId":"Alice","reason":"Cheating","moderator":"Dana","at":1753900000000,"expires":1754500000000,"durationLabel":"7d"},
         {"playerId":"Bob","reason":"Griefing","moderator":"Sam","at":1753900000000,"permanent":true}]
        """;

    [Fact]
    public void NodesBanListIsReadable()
    {
        Assert.True(Codec.TryDeserialize<List<BanRecord>>(NodeBanList, out var bans));
        Assert.Equal(2, bans!.Count);

        var alice = bans[0];
        Assert.Equal("Alice", alice.PlayerId);
        Assert.Equal("Cheating", alice.Reason);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1754500000000), alice.Expires);
        Assert.True(alice.IsValid);
        Assert.False(alice.Permanent);

        Assert.True(bans[1].Permanent);
        Assert.Null(bans[1].Expires);
    }

    [Fact]
    public void CSharpWritesTheFormatNodeCanReadBack()
    {
        /* WRITING milliseconds is what keeps a rollback possible. If C# emitted ISO
           strings, Node's `Number(b.expires)` would be NaN, its validator would reject the
           record as corrupt, and the ban would vanish from a bot that used to hold it. */
        var json = Codec.Serialize(new List<BanRecord>
        {
            new() { PlayerId = "Alice", Reason = "Cheating", Expires = DateTimeOffset.FromUnixTimeMilliseconds(1754500000000) },
        });

        Assert.Contains("1754500000000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2025-", json, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ARoundTripThroughBothFormatsPreservesTheInstant()
    {
        Assert.True(Codec.TryDeserialize<List<BanRecord>>(NodeBanList, out var first));
        var rewritten = Codec.Serialize(first);

        Assert.True(Codec.TryDeserialize<List<BanRecord>>(rewritten, out var second));
        Assert.Equal(first![0].Expires, second![0].Expires);
        Assert.Equal(first[0].PlayerId, second[0].PlayerId);
    }

    [Fact]
    public void AnIsoTimestampFromAnEarlierBuildIsStillReadable()
    {
        // Written by a build predating the converter. It has to keep loading.
        const string iso = """[{"playerId":"Alice","reason":"x","expires":"2026-08-06T20:26:40+00:00"}]""";

        Assert.True(Codec.TryDeserialize<List<BanRecord>>(iso, out var bans));
        Assert.Equal(new DateTimeOffset(2026, 8, 6, 20, 26, 40, TimeSpan.Zero), bans![0].Expires);
    }

    [Fact]
    public void AnUnreadableTimestampBecomesNull_NotEpochZero()
    {
        /* Load-bearing. A ban with a null Expires and Permanent=false is INVALID and is
           skipped; one at epoch zero is "expired in 1970" and gets LIFTED on the next
           sweep. Collapsing null into a default quietly unbans people. */
        const string broken = """[{"playerId":"Alice","reason":"x","expires":{"nested":"nonsense"},"permanent":true}]""";

        Assert.True(Codec.TryDeserialize<List<BanRecord>>(broken, out var bans));
        Assert.Null(bans![0].Expires);
        Assert.True(bans[0].Permanent);
        Assert.True(bans[0].IsValid);   // permanent, so still enforceable
    }

    [Fact]
    public void CamelCaseKeysFromNodeBind()
    {
        /* Without case-insensitive binding every field reads as its default and the list
           deserialises into empty records - worse than failing, because it looks like data. */
        Assert.True(Codec.TryDeserialize<List<BanRecord>>(
            """[{"playerId":"Alice","permanent":true,"durationLabel":"Permanent"}]""", out var bans));

        Assert.Equal("Alice", bans![0].PlayerId);
        Assert.Equal("Permanent", bans[0].DurationLabel);
    }

    [Fact]
    public void TheStoreServesNodesDataRatherThanTheFallback()
    {
        /* The end-to-end version of the whole problem: an unparseable dataset is
           indistinguishable from an empty one at the call site, so this asserts on the
           thing that actually mattered - the caller gets the real bans. */
        var backend = new MemoryBackend();
        backend.Write(Datasets.TempBans, NodeBanList);

        var store = new SerializedStore(backend, Codec);
        var bans = store.Read<List<BanRecord>>(Datasets.TempBans, []);

        Assert.Equal(2, bans.Count);
        Assert.NotEmpty(BanRules.Active(bans, DateTimeOffset.FromUnixTimeMilliseconds(1753950000000)));
    }

    [Fact]
    public async Task AWriteDoesNotDestroyRecordsItCouldNotParse()
    {
        // The exact wipe path: read, add one, write back.
        var backend = new MemoryBackend();
        backend.Write(Datasets.TempBans, NodeBanList);

        var store = new SerializedStore(backend, Codec);
        await store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            bans.Add(new BanRecord { PlayerId = "Carol", Reason = "New", Permanent = true });
            return bans;
        });

        Assert.Equal(3, store.Read<List<BanRecord>>(Datasets.TempBans, []).Count);
    }

    private sealed class MemoryBackend : IKeyValueBackend
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        public string? Read(string key) => _data.GetValueOrDefault(key);
        public void Write(string key, string json) => _data[key] = json;
    }
}
