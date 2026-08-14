using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Serves a different player count on each call, so caching is observable.</summary>
internal sealed class CountingMasterHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    /// <summary>Each call reports one more player than the last, starting at 1.</summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        /* THE API'S OWN FIELD NAMES. It calls the live count "slots" and the capacity
           "maxSlots", which is the opposite of what both words suggest - a fake that invents
           "players" here would pass while testing nothing the parser does. */
        var body = $$"""
            {"servers":[{"version":"1.0.27","name":"one","ip":"1.2.3.4","port":7777,
             "slots":{{Calls}},"maxSlots":24,"mapLabel":"datacenter","mapId":"UGC1",
             "gameMode":"SND","gameModeLabel":"SND","bPasswordProtected":false,"bSecured":true}]}
            """;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Records the URL it was asked for, and answers with an empty list.</summary>
internal sealed class UrlRecordingHandler : HttpMessageHandler
{
    public Uri? Requested { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requested = request.RequestUri;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(@"{""servers"":[]}", Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// The master-server cache, and who is allowed to be served out of it.
/// </summary>
/// <remarks>
/// The cache exists to protect the platform's endpoint from bursts of interactive commands -
/// four people running <c>/server lookup</c> at once should cost one request. It is not there
/// to spare a five-minute background tick a single HTTP call, and serving that tick out of it
/// is what put a stale number in the sidebar.
/// </remarks>
public class MasterServerListTests
{
    private static MasterServerList New(CountingMasterHandler handler) =>
        new(new HttpClient(handler), NullLogger<MasterServerList>.Instance);

    [Fact]
    public async Task ASecondReaderInsideTheWindowIsServedFromTheCache()
    {
        // The property the cache exists for, kept.
        var handler = new CountingMasterHandler();
        var master = New(handler);

        var first = await master.GetAsync();
        var second = await master.GetAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(first.Servers[0].Players, second.Servers[0].Players);
    }

    [Fact]
    public async Task AForcedReadAlwaysGoesToTheNetwork()
    {
        /* THE FIX FOR THE "TICK BEHIND" SYMPTOM. The player-count channel publishes a number
           and then sits on it for five minutes, so it has to be the number that is true when
           it publishes - not whatever a /server lookup happened to cache forty seconds ago.

           The observed shape was two renames a moment apart: 112 -> 113 out of the cache,
           then 113 -> 127 as soon as something actually asked. */
        var handler = new CountingMasterHandler();
        var master = New(handler);

        await master.GetAsync();                                  // warms the cache with 1
        var forced = await master.GetAsync(force: true);          // must not be served 1

        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, forced.Servers[0].Players);
        Assert.Equal(TimeSpan.Zero, forced.Age);
    }

    [Fact]
    public async Task AForcedReadThatFailsKeepsTheLastGoodList()
    {
        /* Forcing must not mean "discard what we have on failure". An empty list would send
           the channel to "Pavlov Shack: 0", a number that was never true, and the caller
           cannot tell the difference without the Failed flag. */
        var master = new MasterServerList(
            new HttpClient(new CannedHandler(HttpStatusCode.ServiceUnavailable, "{}")),
            NullLogger<MasterServerList>.Instance);

        var snapshot = await master.GetAsync(force: true);

        Assert.True(snapshot.Failed);
        Assert.Empty(snapshot.Servers);   // nothing was ever cached, so there is nothing to keep
    }

    /* ─── THE VERSION IN THE URL ────────────────────────────────────────────────────────

       Vankrupt version the ENDPOINT, not the payload, so a game update does not return
       something misshapen - it returns nothing at all. The symptom is the Pavlov Shack
       channel reading 0 and the server browser being empty while the game is plainly fine,
       which looks like the bot being broken and is a one-character fix.

       These pin the two things that make that fix work: the version reaches the URL, and
       PAVLOV_VERSION overrides it without a deploy. Neither was covered, so a version bump
       could have been made to a constant that nothing read. */

    [Fact]
    public async Task TheConfiguredVersionIsTheOneRequested()
    {
        var handler = new UrlRecordingHandler();
        var master = new MasterServerList(new HttpClient(handler), NullLogger<MasterServerList>.Instance, "9.9.9");

        await master.GetAsync(force: true);

        Assert.NotNull(handler.Requested);
        Assert.Contains("/list/9.9.9/", handler.Requested!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultVersionIsUsedWhenNoneIsConfigured()
    {
        var handler = new UrlRecordingHandler();
        var master = new MasterServerList(new HttpClient(handler), NullLogger<MasterServerList>.Instance);

        await master.GetAsync(force: true);

        Assert.Contains($"/list/{MasterServerList.DefaultVersion}/", handler.Requested!.AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>An empty PAVLOV_VERSION falls back rather than building a broken URL.</summary>
    /// <remarks>
    /// <c>PAVLOV_VERSION=</c> with nothing after it is how somebody turns the override off,
    /// and it is what an unset variable looks like once trimmed. Passing it straight through
    /// would request <c>/list//oculus</c> and get a 404 that reads as the platform being down.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyOverrideFallsBackToTheDefault(string configured)
    {
        var handler = new UrlRecordingHandler();
        var master = new MasterServerList(new HttpClient(handler), NullLogger<MasterServerList>.Instance, configured);

        await master.GetAsync(force: true);

        Assert.Contains($"/list/{MasterServerList.DefaultVersion}/", handler.Requested!.AbsoluteUri, StringComparison.Ordinal);
    }
}
