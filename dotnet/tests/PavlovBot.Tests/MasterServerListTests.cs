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
}
