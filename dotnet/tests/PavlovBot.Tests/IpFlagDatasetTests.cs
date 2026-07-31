using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The two bots share one database, and these two datasets used to share one name.
/// </summary>
/// <remarks>
/// <c>user_blacklist</c> is the Node bot's ARRAY of Discord ids barred from every command
/// (<c>index.js</c>). C# used the same key for an OBJECT of IP/name/account flags. Each
/// bot's write destroyed the other's data, and the C# direction was silent: reading an
/// array where an object is expected yields no flags, so ban-evasion auto-banning simply
/// stops with nothing logged.
/// </remarks>
public class IpFlagDatasetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-flags-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly IpTrackingService _tracking;

    public IpFlagDatasetTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _tracking = new IpTrackingService(_store, new MetricsRegistry(), NullLogger<IpTrackingService>.Instance);
    }

    [Fact]
    public async Task FlagsAreWrittenToIpFlags_NotOverTheNodeBlacklist()
    {
        await _tracking.FlagAddressManuallyAsync("203.0.113.9");

        Assert.Contains("203.0.113.9", _store.Read(Datasets.IpFlags, IpTrackingService.StoredFlags.Empty).ManualIps);

        /* The critical half: user_blacklist must be untouched. Writing an object here left
           the Node bot calling .map on it at module load. */
        Assert.Null(new FileKeyValueBackend(_directory).Read(Datasets.UserBlacklist));
    }

    [Fact]
    public async Task TheNodeBarredUserListSurvivesAFlagWrite()
    {
        var barred = new List<string> { "1014251293159731310", "678362059905171471" };
        await _store.WriteAsync(Datasets.UserBlacklist, barred);

        await _tracking.FlagAddressManuallyAsync("203.0.113.9");

        Assert.Equal(barred, _store.Read(Datasets.UserBlacklist, new List<string>()));
    }

    [Fact]
    public async Task NodesArrayIsNeverMistakenForLegacyFlags()
    {
        /* The migration reads through to the old key. It must not interpret Node's array as
           C# data - deserialising it as StoredFlags fails and yields Empty, which is the
           property being pinned here. */
        await _store.WriteAsync(Datasets.UserBlacklist, new List<string> { "123", "456" });

        var flags = _tracking.LoadFlags();

        Assert.Empty(flags.Ips);
        Assert.Empty(flags.Names);
        Assert.Empty(flags.Ids);
    }

    [Fact]
    public async Task FlagsLeftUnderTheOldNameAreStillHonoured()
    {
        /* An installation that ran the older C# build has its flags under user_blacklist.
           Switching keys without this would read as every flag having been forgotten -
           evaders walk back in and nothing logs a reason. */
        await _store.WriteAsync(Datasets.UserBlacklist, new IpTrackingService.StoredFlags(
            Ips: ["198.51.100.4"], Names: ["evader"], Ids: ["76561198000000000"], ManualIps: []));

        var flags = _tracking.LoadFlags();

        Assert.Contains("198.51.100.4", flags.Ips);
        Assert.Contains("evader", flags.Names);
        Assert.Contains("76561198000000000", flags.Ids);
    }

    [Fact]
    public async Task TheNewDatasetWinsOverTheOldOne()
    {
        await _store.WriteAsync(Datasets.UserBlacklist, new IpTrackingService.StoredFlags(
            Ips: ["198.51.100.4"], Names: [], Ids: [], ManualIps: []));
        await _store.WriteAsync(Datasets.IpFlags, new IpTrackingService.StoredFlags(
            Ips: ["203.0.113.9"], Names: [], Ids: [], ManualIps: []));

        var flags = _tracking.LoadFlags();

        Assert.Contains("203.0.113.9", flags.Ips);
        Assert.DoesNotContain("198.51.100.4", flags.Ips);
    }

    [Fact]
    public async Task MigratedFlagsAreCarriedForwardOnTheNextWrite()
    {
        // The read-through is not enough on its own: the first mutation must preserve what
        // it migrated rather than starting from an empty set.
        await _store.WriteAsync(Datasets.UserBlacklist, new IpTrackingService.StoredFlags(
            Ips: ["198.51.100.4"], Names: [], Ids: [], ManualIps: []));

        await _tracking.FlagAddressManuallyAsync("203.0.113.9");

        var flags = _tracking.LoadFlags();
        Assert.Contains("198.51.100.4", flags.Ips);
        Assert.Contains("203.0.113.9", flags.ManualIps);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
