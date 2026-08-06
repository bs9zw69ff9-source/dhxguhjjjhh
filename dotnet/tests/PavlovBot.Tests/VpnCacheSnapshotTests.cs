using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using PavlovBot.Host.Vpn;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The VPN cache must not be decoded from scratch for every lookup.
/// </summary>
/// <remarks>
/// Cached(ip) called LoadCache(), which deserialised the whole vpn_checks dataset - one entry
/// per distinct address ever seen - built a dictionary of all of it, read one key and threw
/// the rest away. FeedBridge screens every confirmed connection, so the cost of admitting one
/// player grew with the number of players the server had EVER seen.
///
/// The backend already caches the raw string, so this was never a database round trip. It was
/// the parse and the allocations, which a string cache does nothing about - which is why the
/// counting here is done at the CODEC, not at the backend.
/// </remarks>
public class VpnCacheSnapshotTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"vpnsnap-{Guid.NewGuid():N}");

    /// <summary>Counts how often a dataset is actually deserialised.</summary>
    private sealed class CountingCodec : IJsonCodec
    {
        private readonly IJsonCodec _inner = new SystemTextJsonCodec();
        public int Deserialisations;

        public string Serialize<T>(T value) => _inner.Serialize(value);

        public bool TryDeserialize<T>(string json, out T? value)
        {
            Interlocked.Increment(ref Deserialisations);
            return _inner.TryDeserialize(json, out value);
        }
    }

    private (VpnScreeningService Service, CountingCodec Codec) Build()
    {
        Directory.CreateDirectory(_directory);
        var codec = new CountingCodec();
        var backend = new FileKeyValueBackend(_directory);

        /* SEEDED, and the test is worthless without it. SerializedStore.Read returns the
           fallback before touching the codec when the dataset is empty or absent, so an
           unseeded cache decodes zero times whether the snapshot exists or not - which is a
           test that passes against the bug it was written for. Verified: with the snapshot
           disabled this now fails, and unseeded it did not. */
        backend.Write(Datasets.VpnChecks,
            """{"203.0.113.1":{"Ip":"203.0.113.1","ScreenHits":0,"ScreenAnswered":1}}""");

        var store = new SerializedStore(backend, codec);

        return (new VpnScreeningService(
            [],                                        // no detectors: the cache is what is under test
            store,
            new StubGeoLocator(null),
            new MetricsRegistry(),
            NullLogger<VpnScreeningService>.Instance), codec);
    }

    [Fact]
    public void RepeatedLookupsDoNotRedecodeTheWholeCache()
    {
        var (service, codec) = Build();

        // Prime it, then look up many times as a joining lobby would.
        service.Cached("203.0.113.1");
        var afterFirst = codec.Deserialisations;

        for (var i = 0; i < 200; i++) service.Cached($"203.0.113.{i % 24}");

        /* THE POINT. Before, this was 200 more full decodes of the entire dataset. The
           snapshot holds for its TTL, so a burst of joins - a map change reconnecting a full
           server - costs one decode rather than one per player. */
        Assert.Equal(afterFirst, codec.Deserialisations);
    }

    [Fact]
    public void TheCacheIsStillReadCorrectly()
    {
        // Cheap to make fast and wrong, so pin that the lookup still answers.
        var (service, _) = Build();

        Assert.Null(service.Cached("198.51.100.7"));
        Assert.Empty(service.LoadCache());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory that will not delete is not a test failure */ }
        GC.SuppressFinalize(this);
    }
}
