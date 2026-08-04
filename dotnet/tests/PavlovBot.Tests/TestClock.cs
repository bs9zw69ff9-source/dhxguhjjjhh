namespace PavlovBot.Tests;

/// <summary>
/// A clock the test drives.
/// </summary>
/// <remarks>
/// Several things in this bot make decisions from elapsed time - a payroll period, a warning
/// decay window, a money window, a restart cooldown - and every one of them has a rule that
/// only shows up once time moves. Those rules are the whole reason the features are safe, so
/// they have to be testable without a test that sleeps.
///
/// Hand-rolled rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>: this is the
/// entire surface those tests need, and it saves a package reference. There is already a
/// private <c>FixedClock</c> in ModsaveAndPluginTests, but it cannot advance.
///
/// <see cref="GetTimestamp"/> is derived from the same instant as <see cref="GetUtcNow"/>, so
/// anything measuring with <c>Stopwatch.GetElapsedTime</c> moves when this does. Leaving it on
/// the real high-resolution counter is a trap: the code under test would see a wall clock
/// jumping days while its stopwatch reported microseconds.
/// </remarks>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public TestClock() : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _now.UtcTicks;

    /// <summary>Move the clock forward. Never backwards - nothing under test expects that.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now += by;
    }
}
