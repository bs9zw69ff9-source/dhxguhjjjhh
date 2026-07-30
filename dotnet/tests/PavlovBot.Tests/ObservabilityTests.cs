using PavlovBot.Host.Observability;
using Xunit;

namespace PavlovBot.Tests;

public class MetricsRegistryTests
{
    [Fact]
    public void LabelOrderDoesNotCreateASecondSeries()
    {
        /* {a,b} and {b,a} must be ONE series. Getting this wrong does not fail loudly - it
           silently doubles a counter's time series and every dashboard built on it is
           wrong in a way nobody notices for months. */
        var metrics = new MetricsRegistry();
        metrics.Increment("things", MetricLabels.Of("a", "1", "b", "2"));
        metrics.Increment("things", MetricLabels.Of("b", "2", "a", "1"));

        Assert.Equal(1, metrics.Stats().Series);
        Assert.Contains("bot_things{a=\"1\",b=\"2\"} 2", metrics.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void CountersAccumulateAndGaugesReplace()
    {
        var metrics = new MetricsRegistry();
        metrics.Increment("hits");
        metrics.Increment("hits", by: 4);
        metrics.Gauge("depth", 10);
        metrics.Gauge("depth", 3);

        var rendered = metrics.Render();
        Assert.Contains("bot_hits 5", rendered, StringComparison.Ordinal);
        Assert.Contains("bot_depth 3", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HistogramBucketsAreCumulative()
    {
        // Prometheus semantics: le="100" counts EVERYTHING <= 100, not just the 50-100 band.
        var metrics = new MetricsRegistry();
        metrics.Observe("latency", 7, buckets: [5, 10, 100]);
        metrics.Observe("latency", 60, buckets: [5, 10, 100]);

        var rendered = metrics.Render();
        Assert.Contains("bot_latency_bucket{le=\"5\"} 0", rendered, StringComparison.Ordinal);
        Assert.Contains("bot_latency_bucket{le=\"10\"} 1", rendered, StringComparison.Ordinal);
        Assert.Contains("bot_latency_bucket{le=\"100\"} 2", rendered, StringComparison.Ordinal);
        Assert.Contains("bot_latency_bucket{le=\"+Inf\"} 2", rendered, StringComparison.Ordinal);
        Assert.Contains("bot_latency_count 2", rendered, StringComparison.Ordinal);
        Assert.Contains("bot_latency_sum 67", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelValuesAreEscaped()
    {
        // Label values come from player and command names. An unescaped quote produces a
        // line the scraper rejects, which drops the whole scrape, not just that series.
        var metrics = new MetricsRegistry();
        metrics.Increment("x", MetricLabels.Of("name", "he said \"hi\"\nand left"));
        Assert.Contains("name=\"he said \\\"hi\\\"\\nand left\"", metrics.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSeriesCeilingIsEnforcedAndCounted()
    {
        // Labels are fed from user-influenced values; unbounded is a memory leak.
        var metrics = new MetricsRegistry();
        for (var i = 0; i < MetricsRegistry.MaxSeries + 50; i++)
            metrics.Increment("explode", MetricLabels.Of("i", i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var stats = metrics.Stats();
        Assert.Equal(MetricsRegistry.MaxSeries, stats.Series);
        Assert.Equal(50, stats.Dropped);
    }

    [Fact]
    public async Task TimeAsyncLabelsTheOutcomeAndRethrows()
    {
        var metrics = new MetricsRegistry();
        await metrics.TimeAsync("op", MetricLabels.Of("kind", "read"), () => Task.FromResult(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            metrics.TimeAsync<int>("op", MetricLabels.Of("kind", "read"), () => throw new InvalidOperationException("no")));

        var rendered = metrics.Render();
        Assert.Contains("outcome=\"success\"", rendered, StringComparison.Ordinal);
        Assert.Contains("outcome=\"failure\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMetricRendersItsHelpAndType()
    {
        var metrics = new MetricsRegistry();
        metrics.Increment("commands_total", help: "Commands run");
        Assert.Contains("# HELP bot_commands_total Commands run", metrics.Render(), StringComparison.Ordinal);
        Assert.Contains("# TYPE bot_commands_total counter", metrics.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentIncrementsDoNotLoseCounts()
    {
        // Counters are read by alerting rules. A lost increment under load is a missed page.
        var metrics = new MetricsRegistry();
        Parallel.For(0, 1000, _ => metrics.Increment("races", MetricLabels.Of("shared", "yes")));
        Assert.Contains("bot_races{shared=\"yes\"} 1000", metrics.Render(), StringComparison.Ordinal);
    }
}

public class HealthRegistryTests
{
    private static Func<CancellationToken, Task<HealthResult>> Returns(HealthResult result) => _ => Task.FromResult(result);

    [Fact]
    public async Task WorstStatusWins()
    {
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        health.Register("a", Returns(HealthResult.Healthy()));
        health.Register("b", Returns(HealthResult.Degraded("slow")));

        Assert.Equal(HealthStatus.Degraded, (await health.ReportAsync()).Status);

        health.Register("c", Returns(HealthResult.Unhealthy("down")));
        Assert.Equal(HealthStatus.Unhealthy, (await health.ReportAsync()).Status);
    }

    [Fact]
    public async Task ANonCriticalFailureDoesNotFailTheRollup()
    {
        /* An unconfigured webhook must not make the bot look dead to an orchestrator that
           restarts on a 503. The component is still REPORTED as unhealthy - it is just not
           counted in the verdict. */
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        health.Register("core", Returns(HealthResult.Healthy()));
        health.Register("webhook", Returns(HealthResult.Unhealthy("no URL set")), critical: false);

        var report = await health.ReportAsync();
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal(HealthStatus.Unhealthy, report.Components["webhook"].Status);
    }

    [Fact]
    public async Task AThrowingCheckIsUnhealthy_NotAnException()
    {
        // The framework must not be able to take down the thing it is monitoring.
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        health.Register("boom", _ => throw new InvalidOperationException("kaboom"));

        var report = await health.ReportAsync();
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal("kaboom", report.Components["boom"].Detail);
    }

    [Fact]
    public async Task AHangingCheckIsUnhealthyAfterTheTimeout()
    {
        /* An unbounded await inside a health endpoint turns a scrape into a hang: the
           monitoring causes the outage it was meant to detect. The check here ignores its
           cancellation token on purpose - a well-behaved one is not the dangerous case. */
        var health = new HealthRegistry(timeout: TimeSpan.FromMilliseconds(100), cacheDuration: TimeSpan.Zero);
        health.Register("stuck", _ => new TaskCompletionSource<HealthResult>().Task);

        var report = await health.ReportAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Contains("timed out", report.Components["stuck"].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultsAreCachedSoAScrapeLoopIsNotItselfLoad()
    {
        var calls = 0;
        var health = new HealthRegistry(cacheDuration: TimeSpan.FromMinutes(1));
        health.Register("counted", _ => { calls++; return Task.FromResult(HealthResult.Healthy()); });

        await health.ReportAsync();
        await health.ReportAsync();
        await health.ReportAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UnregisteringStopsAComponentBeingReported()
    {
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        var handle = health.Register("temporary", Returns(HealthResult.Unhealthy("x")));
        Assert.Equal(HealthStatus.Unhealthy, (await health.ReportAsync()).Status);

        handle.Dispose();
        var report = await health.ReportAsync();
        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.DoesNotContain("temporary", report.Components.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task AnEmptyRegistryIsHealthy()
    {
        // Nothing registered yet is not a failure - it is a bot that has not finished
        // starting, and a 503 there would restart-loop it before it ever comes up.
        Assert.Equal(HealthStatus.Healthy, (await new HealthRegistry().ReportAsync()).Status);
    }
}
