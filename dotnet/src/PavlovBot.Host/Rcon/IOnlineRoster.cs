namespace PavlovBot.Host.Rcon;

/// <summary>
/// Who is on a server right now, and whether that answer can be relied on.
/// </summary>
/// <remarks>
/// AN INTERFACE BECAUSE OF WHAT DEPENDS ON IT. <see cref="Economy.Payroll"/> pays real
/// currency to whoever this says is online. Depending on <see cref="RconRegistry"/> directly
/// would mean every payroll test needed a live RCON server: the roster cache is private, and
/// freshness is measured against <c>DateTimeOffset.UtcNow</c> inside the registry, so there
/// is no way to construct "the roster is an hour stale" from outside. That case is the single
/// most important one to test, and it was untestable.
///
/// <see cref="IsTrustworthy"/> IS THE WHOLE POINT OF THE SECOND MEMBER.
/// <see cref="RconRegistry.AllOnlinePlayers"/> serves the last SUCCESSFUL refresh, so when
/// RCON has been down for an hour it returns an hour-old list rather than failing. That is
/// right for a dashboard and wrong for anything that acts on it - a caller has to be able to
/// ask whether the list means anything before spending money against it.
///
/// Same shape and same reason as <see cref="IServerLifecycle"/>, <c>IAutoPostTarget</c> and
/// <c>IBalanceStore</c>.
/// </remarks>
public interface IOnlineRoster
{
    /// <summary>
    /// Every player name on any server, deduplicated - INCLUDING a server whose roster is stale.
    /// </summary>
    /// <remarks>
    /// The conservative answer for a GUARD: "do not overwrite this player's ledger, they may be
    /// in game" should still hold for somebody on a server RCON cannot currently reach.
    /// </remarks>
    IReadOnlyList<string> Online { get; }

    /// <summary>
    /// Players on servers whose roster is FRESH - the only list that may be PAID against.
    /// </summary>
    /// <remarks>
    /// The opposite of <see cref="Online"/>'s bias. <see cref="IsTrustworthy"/> is true when ANY
    /// server is fresh, and <see cref="Online"/> still carries the frozen roster of one that
    /// crashed - so payroll kept paying everybody who was on it for as long as it stayed down.
    /// Defaults to <see cref="Online"/> for implementations with no notion of staleness.
    /// </remarks>
    IReadOnlyList<string> ConfirmedOnline => Online;

    /// <summary>
    /// Whether at least one server's roster is recent enough to act on.
    /// </summary>
    /// <remarks>
    /// False means "do not decide anything from <see cref="Online"/>" - not "nobody is
    /// online". The two are opposite instructions and used to be the same value.
    /// </remarks>
    bool IsTrustworthy { get; }
}
