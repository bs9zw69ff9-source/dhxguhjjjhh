using PavlovBot.Host.Rcon;

namespace PavlovBot.Host.Monitoring;

/// <summary>
/// The two things the monitor needs from the RCON layer: which servers exist, and how to drop a
/// server's session before a reconnect. Kept behind an interface so the loop is tested without a
/// real <see cref="RconRegistry"/> or a socket.
/// </summary>
public interface IMonitorTargets
{
    IReadOnlyCollection<string> Servers { get; }

    /// <summary>Drop a wedged RCON session so the next send genuinely reconnects. Never throws.</summary>
    Task ResetSessionAsync(string server, CancellationToken ct = default);
}

/// <summary>The real targets, over the RCON registry.</summary>
public sealed class RconMonitorTargets(RconRegistry rcon) : IMonitorTargets
{
    public IReadOnlyCollection<string> Servers => rcon.Servers;

    public async Task ResetSessionAsync(string server, CancellationToken ct = default)
    {
        if (rcon.Client(server) is not { } client) return;
        try { await client.ResetSessionAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best effort */ }
    }
}
