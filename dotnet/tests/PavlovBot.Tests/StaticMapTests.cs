using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Vpn;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Serves one canned binary response and records what was requested.</summary>
internal sealed class MapHandler(HttpStatusCode status, byte[] body, string? mediaType = "image/png") : HttpMessageHandler
{
    public string? LastUrl { get; private set; }
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri?.ToString();
        Calls++;

        var content = new ByteArrayContent(body);
        content.Headers.ContentType = mediaType is null ? null : new MediaTypeHeaderValue(mediaType);
        return Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }
}

public class StaticMapTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];

    private static StaticMap Map(MapHandler handler) =>
        new(new HttpClient(handler), "secret-key", NullLogger<StaticMap>.Instance);

    [Fact]
    public async Task ReturnsTheImageBytes()
    {
        var bytes = await Map(new MapHandler(HttpStatusCode.OK, Png))
            .RenderAsync(43.65, -79.38, CancellationToken.None);

        Assert.Equal(Png, bytes);
    }

    [Fact]
    public async Task CoordinatesAreFormattedInvariantly()
    {
        /* A culture using ',' as the decimal separator would produce
           "lonlat:-79,38,43,65" - four numbers where two belong, which Geoapify rejects
           or, worse, silently reads as a different place. */
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var handler = new MapHandler(HttpStatusCode.OK, Png);
            await Map(handler).RenderAsync(43.65, -79.38, CancellationToken.None);

            Assert.Contains("lonlat:-79.38,43.65", handler.LastUrl, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, 0)]              // several providers return this for "unknown"
    [InlineData(91, 10)]
    [InlineData(-91, 10)]
    [InlineData(10, 181)]
    [InlineData(10, -181)]
    [InlineData(double.NaN, 10)]
    public async Task ImpossibleCoordinatesAreNotRequestedAtAll(double latitude, double longitude)
    {
        var handler = new MapHandler(HttpStatusCode.OK, Png);

        var bytes = await Map(handler).RenderAsync(latitude, longitude, CancellationToken.None);

        Assert.Null(bytes);
        Assert.Equal(0, handler.Calls);   // no quota spent on a coordinate that is not a place
    }

    [Fact]
    public async Task AnErrorPageServedAs200IsNotTreatedAsAnImage()
    {
        /* Uploading this to Discord would render as a broken image with nothing in the log
           to explain it. The content type is the only thing that distinguishes it. */
        var body = Encoding.UTF8.GetBytes("""{"error":"quota exceeded"}""");
        var bytes = await Map(new MapHandler(HttpStatusCode.OK, body, "application/json"))
            .RenderAsync(43.65, -79.38, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task AFailedRequestIsNullRatherThanAnException()
    {
        // The lookup's real content is the VPN verdict. A map outage must not fail it.
        var bytes = await Map(new MapHandler(HttpStatusCode.TooManyRequests, []))
            .RenderAsync(43.65, -79.38, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task AnEmptyBodyIsNotAnImage()
    {
        var bytes = await Map(new MapHandler(HttpStatusCode.OK, []))
            .RenderAsync(43.65, -79.38, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task ShutdownCancellationPropagates()
    {
        // Distinct from a provider failure: the bot is stopping, and swallowing this would
        // hold shutdown open for a picture.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Map(new MapHandler(HttpStatusCode.OK, Png)).RenderAsync(43.65, -79.38, cancelled.Token));
    }

    [Fact]
    public async Task TheApiKeyGoesToGeoapifyAndIsNeverReturnedToTheCaller()
    {
        /* The contract this class exists to enforce: callers get BYTES, never a URL, so
           there is no way for the key to reach a Discord embed. If this method ever starts
           handing back a link, the key becomes visible to everyone who can see the
           message - and Discord keeps that message payload indefinitely. */
        var handler = new MapHandler(HttpStatusCode.OK, Png);
        var result = await Map(handler).RenderAsync(43.65, -79.38, CancellationToken.None);

        Assert.IsType<byte[]>(result);
        Assert.StartsWith("https://maps.geoapify.com/v1/staticmap", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("apiKey=secret-key", handler.LastUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", Encoding.UTF8.GetString(result!), StringComparison.Ordinal);
    }
}
