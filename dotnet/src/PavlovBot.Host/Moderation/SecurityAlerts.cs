using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Moderation;

/// <summary>What kind of thing was caught.</summary>
public enum SecurityAlertKind
{
    /// <summary>A join matched a standing flag - somebody coming back on a new account.</summary>
    Evasion,

    /// <summary>A connection from an account that shares an address with other accounts.</summary>
    Alts,

    /// <summary>A connection screened as a VPN, proxy, Tor exit or hosting range.</summary>
    Vpn,
}

/// <summary>
/// One thing worth waking somebody up for.
/// </summary>
/// <param name="Headline">What was detected, in one line.</param>
/// <param name="Action">
/// What the bot DID about it. Never omitted: "VPN detected" and "VPN detected, banned" call
/// for completely different responses from whoever reads this, and an alert that leaves that
/// out makes every recipient go and look it up.
/// </param>
/// <param name="Alts">Other accounts seen on the same address, when there are any.</param>
public sealed record SecurityAlert(
    SecurityAlertKind Kind,
    string Player,
    string Headline,
    string Action,
    string? AccountId = null,
    string? Ip = null,
    string? Server = null,
    IReadOnlyList<string>? Alts = null,
    DateTimeOffset? At = null);

/// <summary>How one alert reached (or failed to reach) one person.</summary>
/// <param name="Problem">Why it did not arrive. Null when it did.</param>
public sealed record AlertDelivery(ulong User, bool Delivered, string? Problem = null);

/// <summary>
/// Somewhere a security detection is delivered as it happens.
/// </summary>
/// <remarks>
/// AN INTERFACE FOR THE SAME REASON AS <see cref="IStaffLogSink"/>: delivering a DM needs the
/// gateway, the gateway is built from every command, and the detectors are built long before
/// either. The implementation is attached afterwards, in the composition root.
/// </remarks>
public interface ISecurityAlertSink
{
    /// <summary>
    /// Deliver one alert. MUST NOT THROW.
    /// </summary>
    /// <remarks>
    /// Every caller is mid-way through a moderation decision it has already committed to.
    /// A sink that throws would turn a failed DM into a lost ban, which is exactly the wrong
    /// way round.
    /// </remarks>
    Task<IReadOnlyList<AlertDelivery>> PostAsync(SecurityAlert alert, CancellationToken ct = default);
}

/// <summary>
/// The detectors' way of raising an alert, with no idea how it is delivered.
/// </summary>
/// <remarks>
/// A HOLDER RATHER THAN THE SINK ITSELF, and it is what keeps the container acyclic: the
/// evasion responder, the VPN responder and the log bridge all depend on THIS, which depends
/// on nothing, and the gateway-backed sink is attached once after the host is built.
///
/// NO SINK IS A NO-OP, not an error. A deployment that configures no recipients keeps every
/// detection it already had - the logs, the feed lines, the ban itself - and simply does not
/// DM anybody.
/// </remarks>
public sealed class SecurityAlerts(ILogger<SecurityAlerts> logger)
{
    private ISecurityAlertSink? _sink;

    /// <summary>Attach the delivery mechanism. See <see cref="ISecurityAlertSink"/>.</summary>
    public void UseSink(ISecurityAlertSink sink) => _sink = sink;

    /// <summary>True once a sink is attached. False means alerts go nowhere.</summary>
    public bool Enabled => _sink is not null;

    /// <summary>
    /// Raise one alert. Never throws, whatever the sink does.
    /// </summary>
    public async Task<IReadOnlyList<AlertDelivery>> PostAsync(SecurityAlert alert, CancellationToken ct = default)
    {
        if (_sink is not { } sink) return [];

        try
        {
            return await sink.PostAsync(alert, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* The contract says a sink must not throw. This is the belt to that braces: the
               callers are all part-way through a ban, and none of them can afford to lose it
               because a direct message failed. */
            logger.LogWarning(ex, "Could not deliver a {Kind} alert for {Player}", alert?.Kind, alert?.Player);
            return [];
        }
    }
}
