using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Services;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// What went wrong, not just how many times.
/// </summary>
/// <remarks>
/// /health reported "12 ticks, 3 failed" and nothing else. The registry had tracked
/// LastError since it was written and the command never displayed it - and even that was
/// only ever ONE message, overwritten by the next failure, so a service failing for two
/// different reasons looked exactly like one failing for the same reason twice.
/// </remarks>
public class HealthErrorTests
{
    private static ServiceRegistry Build() =>
        new(new MetricsRegistry(), NullLogger.Instance);

    private static ServiceDefinition Failing(string name, Func<Exception> error) => new()
    {
        Name = name,
        Interval = TimeSpan.FromMilliseconds(15),
        RunOnStart = true,
        Tick = _ => throw error(),
    };

    private static async Task<IReadOnlyList<ServiceStatus>> RunUntilFailures(ServiceRegistry registry, int atLeast)
    {
        await registry.StartAllAsync();

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var status = registry.Status();
            if (status.Sum(s => s.Errors.Count) >= atLeast) return status;
            await Task.Delay(15);
        }

        return registry.Status();
    }

    [Fact]
    public async Task AFailingTickRecordsWhatWentWrong()
    {
        var registry = Build();
        registry.Register(Failing("boom", () => new InvalidOperationException("RCON refused the connection")));

        var status = await RunUntilFailures(registry, 1);
        await registry.DisposeAsync();

        var service = status.Single(s => s.Name == "boom");
        Assert.NotEmpty(service.Errors);
        Assert.Contains("RCON refused the connection", service.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExceptionTypeIsKeptAlongsideTheMessage()
    {
        /* "Object reference not set to an instance of an object" identifies nothing on its
           own, and it is exactly the message a null deref in a tick produces. */
        var registry = Build();
        registry.Register(Failing("npe", () => new NullReferenceException()));

        var status = await RunUntilFailures(registry, 1);
        await registry.DisposeAsync();

        Assert.Contains("NullReferenceException", status.Single().Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanOneFailureIsKept()
    {
        // The point of the change: the previous shape held exactly one message.
        var count = 0;
        var registry = Build();
        registry.Register(Failing("varied", () => new InvalidOperationException($"failure {Interlocked.Increment(ref count)}")));

        var status = await RunUntilFailures(registry, 3);
        await registry.DisposeAsync();

        var errors = status.Single().Errors;
        Assert.True(errors.Count >= 3, $"kept only {errors.Count}");

        // Newest first, which is the order somebody reads them in.
        Assert.True(errors[0].At >= errors[^1].At);
    }

    [Fact]
    public async Task TheHistoryIsBounded()
    {
        /* A service failing every tick for a week must not grow without limit, and the
           useful question is "what is going wrong now" rather than "what ever went wrong". */
        var registry = Build();
        registry.Register(Failing("always", () => new InvalidOperationException("nope")));

        var status = await RunUntilFailures(registry, 20);
        await registry.DisposeAsync();

        Assert.True(status.Single().Errors.Count <= 5, $"kept {status.Single().Errors.Count}");
    }

    // ---- rendering ----

    private static ServiceStatus WithErrors(string name, params (int AgoSeconds, string Message)[] errors) =>
        new(name, ServiceState.Running, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1),
            Ticks: 10, Failures: errors.Length, ConsecutiveFailures: 1, Overruns: 0,
            LastError: errors.FirstOrDefault().Message, LastTickAt: DateTimeOffset.UtcNow, LastDurationMs: 5,
            RecentErrors: [.. errors.Select(e =>
                new ServiceFailure(DateTimeOffset.UtcNow.AddSeconds(-e.AgoSeconds), e.Message))]);

    [Fact]
    public void NoFailuresRendersNothing()
    {
        // An empty "Recent errors" field on a healthy bot is noise that trains people to
        // ignore the field.
        Assert.Equal("", HealthCommand.Errors([WithErrors("clean")]));
    }

    [Fact]
    public void FailuresFromDifferentServicesAreInterleavedByTime()
    {
        /* When two services fail together, seeing them next to each other is usually the
           whole diagnosis - grouping by service would hide that. */
        var rendered = HealthCommand.Errors([
            WithErrors("older", (300, "first thing")),
            WithErrors("newer", (5, "second thing")),
        ]);

        Assert.True(
            rendered.IndexOf("second thing", StringComparison.Ordinal) <
            rendered.IndexOf("first thing", StringComparison.Ordinal),
            "the newest failure must come first");
    }

    [Fact]
    public void AMultiLineMessageIsFlattened()
    {
        // A stack trace would otherwise break the one-line-per-failure layout entirely.
        var rendered = HealthCommand.Errors([WithErrors("stacky", (1, "boom\n   at Thing.Method()\n   at Other()"))]);

        Assert.Equal(1, rendered.Count(c => c == '\n'));
    }

    [Fact]
    public void TheFieldStaysInsideDiscordsLimit()
    {
        /* An over-long field makes Discord reject the WHOLE message - so a bot failing badly
           enough to fill this would lose the health reply that explains why, which is the
           one moment it is needed. */
        var noisy = Enumerable.Range(0, 40)
            .Select(i => WithErrors($"service{i}", (i, new string('x', 300))))
            .ToList();

        var rendered = HealthCommand.Errors(noisy);

        Assert.True(rendered.Length <= 1024, $"field is {rendered.Length} characters");
        Assert.Contains("more", rendered, StringComparison.Ordinal);   // and it says what it dropped
    }
}
