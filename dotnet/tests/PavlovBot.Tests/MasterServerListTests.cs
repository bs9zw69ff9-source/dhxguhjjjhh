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

/// <summary>Answers with one server, or with an empty list once switched.</summary>
/// <remarks>
/// Both responses are HTTP 200. That is the whole point: a version mismatch is a SUCCESSFUL
/// request returning nothing, which is why it was indistinguishable from an empty platform
/// and why it was allowed to overwrite a good cache.
/// </remarks>
internal sealed class SwitchableHandler : HttpMessageHandler
{
    public bool ReturnEmpty { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = ReturnEmpty
            ? @"{""servers"":[]}"
            : @"{""servers"":[{""version"":""1.0.28"",""name"":""one"",""ip"":""1.2.3.4"",""port"":7777," +
              @"""slots"":3,""maxSlots"":24,""mapLabel"":""datacenter"",""mapId"":""UGC1""," +
              @"""gameMode"":""SND"",""gameModeLabel"":""SND"",""bPasswordProtected"":false,""bSecured"":true}]}";

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

    /* ─── AN EMPTY ANSWER MUST NOT EVICT A GOOD LIST ───────────────────────────────────

       THE REPORTED FAILURE: searching the server browser for "Little" answered "nothing
       matched" while "Little Italy [#1]" was visible in the list directly above it.

       A version mismatch is a SUCCESSFUL request returning zero servers, so it replaced a
       cache holding hundreds. The rendered list came from the session snapshot and kept
       showing what it had; the search re-read the cache, found it empty, and was right to say
       nothing matched. Two correct components and an impossible result between them.

       The same eviction publishes "Pavlov Shack: 0" to the voice channel. */

    [Fact]
    public async Task AnEmptySuccessfulAnswerKeepsTheServersWeAlreadyHad()
    {
        var handler = new SwitchableHandler();
        var master = new MasterServerList(new HttpClient(handler), NullLogger<MasterServerList>.Instance);

        var first = await master.GetAsync(force: true);
        Assert.Single(first.Servers);

        // The game updates: the URL still answers 200, with nothing in it.
        handler.ReturnEmpty = true;
        var second = await master.GetAsync(force: true);

        Assert.Single(second.Servers);                 // the good list survives
        Assert.Equal("one", second.Servers[0].Name);
        Assert.True(second.Failed);                    // and is honestly flagged as stale
    }

    /// <summary>Recovery is automatic once the version is right again.</summary>
    /// <remarks>
    /// The control. Keeping the old list forever would be its own bug - a server that really
    /// did leave the platform must eventually disappear, and a bumped PAVLOV_VERSION has to
    /// take effect without a restart.
    /// </remarks>
    [Fact]
    public async Task AGoodAnswerAfterAnEmptyOneReplacesTheList()
    {
        var handler = new SwitchableHandler();
        var master = new MasterServerList(new HttpClient(handler), NullLogger<MasterServerList>.Instance);

        await master.GetAsync(force: true);
        handler.ReturnEmpty = true;
        await master.GetAsync(force: true);

        handler.ReturnEmpty = false;
        var recovered = await master.GetAsync(force: true);

        Assert.False(recovered.Failed);
        Assert.Single(recovered.Servers);
    }

    /// <summary>With nothing cached, an empty answer is simply an empty list.</summary>
    /// <remarks>
    /// There is nothing to protect, so this must not report a failure that did not happen -
    /// "could not be reached" would send somebody looking at the network when the platform
    /// genuinely had no servers, or when the version has been wrong since startup.
    /// </remarks>
    [Fact]
    public async Task AnEmptyAnswerWithNothingCachedIsNotReportedAsAFailure()
    {
        var handler = new SwitchableHandler { ReturnEmpty = true };
        var master = new MasterServerList(new HttpClient(handler), NullLogger<MasterServerList>.Instance);

        var snapshot = await master.GetAsync(force: true);

        Assert.Empty(snapshot.Servers);
        Assert.False(snapshot.Failed);
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
