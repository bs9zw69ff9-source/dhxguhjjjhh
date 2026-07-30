namespace PavlovBot.Rcon;

/// <summary>Connection and behaviour settings for one Pavlov RCON server.</summary>
public sealed record RconOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Password { get; init; }

    /// <summary>Label used in logs and metrics, e.g. "server1".</summary>
    public string Name { get; init; } = "server1";

    /// <summary>How long a single command may take before it is abandoned.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Attempts per command, including the first.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Minimum gap between commands on one connection. The Pavlov RCON docs advise 100ms
    /// between successive commands to prevent drops - the server can silently discard a
    /// command that arrives too close behind the last one.
    /// </summary>
    public TimeSpan CommandSpacing { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long an idempotent read is reused. Several independent timers ask for the same
    /// roster seconds apart; without this each one costs its own round trip on the game
    /// thread. Zero disables coalescing entirely.
    /// </summary>
    public TimeSpan ReadCacheDuration { get; init; } = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Drop and rebuild the connection after this long with no traffic. Pavlov does not
    /// document an idle timeout, so rather than discover it as a mysterious failure we
    /// recycle first.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
