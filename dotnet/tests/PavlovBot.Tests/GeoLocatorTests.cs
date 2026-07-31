using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Vpn;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Serves one canned body, and records the URL it was asked for.</summary>
internal sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public string? LastUrl { get; private set; }
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastUrl = request.RequestUri?.ToString();
        Calls++;
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>Answers with a fixed result, or throws, without touching the network.</summary>
internal sealed class StubGeoLocator(GeoLocation? result, Exception? throws = null) : IGeoLocator
{
    public int Calls { get; private set; }

    public Task<GeoLocation?> LocateAsync(string ip, CancellationToken ct)
    {
        Calls++;
        return throws is not null ? Task.FromException<GeoLocation?>(throws) : Task.FromResult(result);
    }
}

public class GeoapifyGeoLocatorTests
{
    private static GeoapifyGeoLocator Locator(CannedHandler handler) =>
        new(new ResilientJsonClient(new HttpClient(handler), NullLogger.Instance), "test-key");

    /// <summary>The documented shape: every field nested under its own object.</summary>
    private const string NestedBody = """
    {
      "ip": "8.8.8.8",
      "city": { "name": "Mountain View" },
      "country": { "name": "United States", "iso_code": "US" },
      "state": { "name": "California" },
      "postcode": "94035",
      "location": { "latitude": 37.386, "longitude": -122.084 },
      "timezone": { "name": "America/Los_Angeles" }
    }
    """;

    [Fact]
    public async Task ReadsTheNestedResponse()
    {
        var located = await Locator(new CannedHandler(HttpStatusCode.OK, NestedBody))
            .LocateAsync("8.8.8.8", CancellationToken.None);

        Assert.NotNull(located);
        Assert.Equal("Mountain View", located.City);
        Assert.Equal("California", located.Region);
        Assert.Equal("United States", located.Country);
        Assert.Equal("US", located.CountryCode);
        Assert.Equal("94035", located.Zip);
        Assert.Equal("America/Los_Angeles", located.Timezone);
        Assert.Equal(37.386, located.Latitude);
        Assert.Equal(-122.084, located.Longitude);
    }

    [Fact]
    public async Task RegionFallsBackToSubdivisions()
    {
        // Some countries carry the region under `subdivisions` and have no `state`.
        const string body = """
        {
          "city": { "name": "Toronto" },
          "country": { "name": "Canada", "iso_code": "CA" },
          "subdivisions": [ { "iso_code": "ON", "name": "Ontario" } ]
        }
        """;

        var located = await Locator(new CannedHandler(HttpStatusCode.OK, body))
            .LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Equal("Ontario", located?.Region);
    }

    [Fact]
    public async Task AcceptsBareStringsWhereObjectsAreExpected()
    {
        /* The parser reads either shape. A provider flattening its JSON would otherwise
           turn every lookup into a silent null, and nothing in this bot would report it -
           the location just stops appearing in the admin feed. */
        const string body = """{ "city": "Berlin", "country": "Germany" }""";

        var located = await Locator(new CannedHandler(HttpStatusCode.OK, body))
            .LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Equal("Berlin", located?.City);
        Assert.Equal("Germany", located?.Country);
    }

    [Fact]
    public async Task AResponseWithNoUsableFieldIsNotAnAnswer()
    {
        /* Must be null, not an empty GeoLocation. The chain stops at the first non-null
           result, so an empty object would count as success and permanently prevent
           ip-api from being consulted. */
        var located = await Locator(new CannedHandler(HttpStatusCode.OK, """{ "ip": "1.1.1.1" }"""))
            .LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Null(located);
    }

    [Fact]
    public async Task TheKeyGoesToGeoapifyAndNowhereElse()
    {
        var handler = new CannedHandler(HttpStatusCode.OK, NestedBody);
        await Locator(handler).LocateAsync("8.8.8.8", CancellationToken.None);

        Assert.StartsWith("https://api.geoapify.com/v1/ipinfo", handler.LastUrl, StringComparison.Ordinal);
        Assert.Contains("apiKey=test-key", handler.LastUrl, StringComparison.Ordinal);
    }
}

public class GeoLocatorChainTests
{
    private static GeoLocatorChain Chain(params IGeoLocator[] providers) =>
        new(providers, NullLogger<GeoLocatorChain>.Instance);

    [Fact]
    public async Task TheFirstProviderThatAnswersWins_AndTheRestAreNotCalled()
    {
        /* Order is the policy, not an implementation detail: ip-api supplies the ISP and
           ASN that the evasion scorer matches against banned players' networks, and
           Geoapify supplies neither. A chain that consulted the second provider while the
           first was healthy would spend quota for a strictly worse answer. */
        var first = new StubGeoLocator(new GeoLocation(City: "Toronto", Isp: "Bell", Asn: "AS577"));
        var second = new StubGeoLocator(new GeoLocation(City: "Nowhere"));

        var located = await Chain(first, second).LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Equal("Toronto", located?.City);
        Assert.Equal("AS577", located?.Asn);
        Assert.Equal(1, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task ANullAnswerFallsThroughToTheNextProvider()
    {
        var first = new StubGeoLocator(null);
        var second = new StubGeoLocator(new GeoLocation(City: "Berlin"));

        var located = await Chain(first, second).LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Equal("Berlin", located?.City);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task AThrowingProviderDoesNotEndTheChain()
    {
        // These are third-party endpoints on the join path. One failing badly must not
        // cost the lookup entirely.
        var first = new StubGeoLocator(null, throws: new HttpRequestException("boom"));
        var second = new StubGeoLocator(new GeoLocation(City: "Berlin"));

        var located = await Chain(first, second).LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Equal("Berlin", located?.City);
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAsAProviderFailure()
    {
        /* A cancelled lookup means the bot is shutting down. Treating it as "this provider
           failed, try the next" would work through every remaining provider during
           shutdown instead of stopping. */
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var first = new StubGeoLocator(null, throws: new OperationCanceledException(cancelled.Token));
        var second = new StubGeoLocator(new GeoLocation(City: "Berlin"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Chain(first, second).LocateAsync("1.1.1.1", cancelled.Token));

        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task NoProviderAnsweringIsNullRatherThanAnEmptyLocation()
    {
        var located = await Chain(new StubGeoLocator(null), new StubGeoLocator(null))
            .LocateAsync("1.1.1.1", CancellationToken.None);

        Assert.Null(located);
    }
}
