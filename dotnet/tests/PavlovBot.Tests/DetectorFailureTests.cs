using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Vpn;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Why a detector gave no verdict, which the bot could not previously say.
/// </summary>
/// <remarks>
/// THE BUG THESE EXIST FOR: every path that failed to produce a verdict returned a bare
/// null, and the reason went only to the application log - at Warning, and for a rejected
/// key only ONCE per process. So <c>/vpncheck</c>, the command whose entire job is telling
/// you what is wrong with the detectors, printed the same two words - "no verdict" - for an
/// invalid key, an exhausted quota, a blocked outbound port and a provider that simply
/// omitted the field. Four different fixes behind one blank answer.
/// </remarks>
public class DetectorFailureTests
{
    private static ResilientJsonClient Client(CannedHandler handler) =>
        new(new HttpClient(handler), NullLogger.Instance);

    private static VpnKeys Keyed => new(IpHub: "k", Ipqs: "k", ProxyCheck: "k", VpnApi: "k", IpapiIs: "k", Sentinel: "k");

    // ---- the transport ----

    [Fact]
    public async Task ARejectedKeyIsNamedAsOne()
    {
        /* The single most likely cause of "no verdict on everything", and the one the
           operator can actually fix. It has to reach the screen, not just the log. */
        var fetch = await Client(new CannedHandler(HttpStatusCode.Unauthorized, """{"error":"invalid api key"}"""))
            .FetchAsync("https://example.test/x", "test");

        Assert.Null(fetch.Document);
        Assert.NotNull(fetch.Failure);
        Assert.Contains("401", fetch.Failure!, StringComparison.Ordinal);
        Assert.Contains("check the API key", fetch.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid api key", fetch.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpentQuotaSaysSoAndSaysForHowLong()
    {
        var fetch = await Client(new CannedHandler(HttpStatusCode.TooManyRequests, "{}"))
            .FetchAsync("https://example.test/x", "test");

        Assert.Contains("429", fetch.Failure!, StringComparison.Ordinal);
        Assert.Contains("quota or rate limit", fetch.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRateLimitPauseIsVisibleRatherThanSilent()
    {
        /* The second lookup never leaves the process - it is suppressed by the rate-limit
           memory. That suppression used to log at Debug and return null, so at the default
           level a paused detector said NOTHING AT ALL for a full minute. */
        var handler = new CannedHandler(HttpStatusCode.TooManyRequests, "{}");
        var client = Client(handler);

        await client.FetchAsync("https://example.test/x", "test");
        var second = await client.FetchAsync("https://example.test/y", "test");

        Assert.Equal(1, handler.Calls);                 // genuinely not re-requested
        Assert.Contains("rate limited", second.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not retrying", second.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnHtmlErrorPageIsNotMistakenForANetworkFailure()
    {
        // A captive portal or a proxy in the way returns 200 with HTML. It looks like
        // success to everything except the parser.
        var fetch = await Client(new CannedHandler(HttpStatusCode.OK, "<html>Access denied</html>"))
            .FetchAsync("https://example.test/x", "test");

        Assert.Null(fetch.Document);
        Assert.Contains("not JSON", fetch.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AKeyIsNeverEchoedIntoTheReason()
    {
        /* The reason is rendered into a Discord channel. Providers quote the URL - and
           therefore the key - back in their error bodies, so a reason that helped you fix
           the problem by leaking the credential would be a bad trade. */
        const string secret = "sk_live_abcdef123456";
        var fetch = await Client(new CannedHandler(HttpStatusCode.Forbidden, $$"""{"error":"bad key {{secret}}"}"""))
            .FetchAsync($"https://example.test/x?key={secret}", "test", secrets: [secret]);

        Assert.DoesNotContain(secret, fetch.Failure!, StringComparison.Ordinal);
        Assert.Contains("***", fetch.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulFetchHasNoFailureText()
    {
        var fetch = await Client(new CannedHandler(HttpStatusCode.OK, """{"ok":true}"""))
            .FetchAsync("https://example.test/x", "test");

        Assert.NotNull(fetch.Document);
        Assert.Null(fetch.Failure);
        fetch.Document!.Dispose();
    }

    // ---- the detectors ----

    [Fact]
    public async Task EveryDetectorReportsARejectedKeyRatherThanShrugging()
    {
        /* All six, because the failure the user hit was "every detector except one says
           nothing" - a per-detector fix would have left the next one silent. */
        foreach (var detector in All(new CannedHandler(HttpStatusCode.Forbidden, """{"error":"nope"}""")))
        {
            var outcome = await detector.LookupAsync("8.8.8.8", CancellationToken.None);

            Assert.Null(outcome.Reading);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Failure), $"{detector.Name} gave no reason");
            Assert.Contains("403", outcome.Failure!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AProviderThatAnsweredWithoutAVerdictSaysThat()
    {
        /* Distinct from a transport failure and fixed differently: the request worked, the
           body just had nothing in it. On the keyless providers this is what a spent quota
           actually looks like. */
        foreach (var detector in All(new CannedHandler(HttpStatusCode.OK, """{"unexpected":true}""")))
        {
            var outcome = await detector.LookupAsync("8.8.8.8", CancellationToken.None);

            Assert.Null(outcome.Reading);
            Assert.StartsWith("answered, but", outcome.Failure!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AGoodResponseStillProducesAReading()
    {
        // The reason plumbing must not have cost the verdict itself.
        var handler = new CannedHandler(HttpStatusCode.OK, """
            {"ip":"8.8.8.8","is_vpn":true,"is_proxy":false,"is_tor":false,"is_abuser":false,
             "is_datacenter":true,"is_mobile":false,
             "company":{"name":"Google LLC","type":"hosting"},
             "asn":{"asn":15169,"org":"Google LLC"},
             "location":{"country":"United States","country_code":"US","city":"Mountain View"}}
            """);

        var outcome = await new IpapiIsDetector(Client(handler), Keyed).LookupAsync("8.8.8.8", CancellationToken.None);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.Reading);
        Assert.True(outcome.Reading!.Flagged);
        Assert.True(outcome.Reading.Vpn);
        Assert.Equal("AS15169", outcome.Reading.Asn);
    }

    [Fact]
    public void AnUnconfiguredDetectorIsDisabled_NotFailing()
    {
        /* The distinction /vpncheck now draws. "No key set" is a different sentence from
           "this one is broken", and showing neither is how somebody spends an evening
           debugging a detector they never configured. */
        var none = new VpnKeys();
        var handler = new CannedHandler(HttpStatusCode.OK, "{}");

        Assert.False(new IpHubDetector(Client(handler), none).Enabled);
        Assert.False(new VpnApiDetector(Client(handler), none).Enabled);
        Assert.False(new IpqsDetector(Client(handler), none).Enabled);
        Assert.False(new SentinelDetector(Client(handler), none, NullLogger<SentinelDetector>.Instance).Enabled);

        // These two work without a key, which is why a bot with no configuration still
        // screens at all.
        Assert.True(new IpapiIsDetector(Client(handler), none).Enabled);
        Assert.True(new ProxyCheckDetector(Client(handler), none).Enabled);
    }

    [Fact]
    public void TheProbeLineNeverReadsAsABareNoVerdict()
    {
        // Whatever happened, the operator gets a sentence they can act on.
        Assert.Equal("no API key set - not checked",
            new DetectorProbe("iphub", 1, false, null, null, 0, null, Configured: false).Outcome);

        Assert.Equal("HTTP 401 - check the API key",
            new DetectorProbe("iphub", 1, false, null, null, 12, "HTTP 401 - check the API key").Outcome);

        Assert.Equal("vpn:true", new DetectorProbe("iphub", 1, true, true, "vpn:true", 12, null).Outcome);
    }

    private static IVpnDetector[] All(CannedHandler handler)
    {
        var client = Client(handler);
        return
        [
            new IpHubDetector(client, Keyed),
            new VpnApiDetector(client, Keyed),
            new IpapiIsDetector(client, Keyed),
            new ProxyCheckDetector(client, Keyed),
            new IpqsDetector(client, Keyed),
            new SentinelDetector(client, Keyed, NullLogger<SentinelDetector>.Instance),
        ];
    }
}
