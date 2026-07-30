namespace PavlovBot.Core.Moderation;

public enum MenuClaimAction
{
    /// <summary>Proceed with the grant.</summary>
    Grant,

    /// <summary>This Discord account is bound to a different in-game name. Refuse.</summary>
    Locked,

    /// <summary>That in-game name belongs to a different Discord account. Refuse.</summary>
    Taken,

    /// <summary>They re-entered their own name while holding a menu - strip it.</summary>
    Release,
}

/// <param name="BoundName">The name they are bound to, on <see cref="MenuClaimAction.Locked"/>.</param>
/// <param name="OwnerId">The account that owns the name, on <see cref="MenuClaimAction.Taken"/>.</param>
public sealed record MenuClaimDecision(MenuClaimAction Action, string? BoundName = null, string? OwnerId = null);

/// <summary>
/// One Discord account, one in-game name - forever.
/// </summary>
/// <remarks>
/// The rule stops menu access being cycled onto alt accounts. A member binds to exactly one
/// in-game name the first time they claim a menu, and THE BINDING OUTLIVES THE MENU:
/// releasing the menu frees the access, never the name. Only an admin can break a binding
/// (<c>/unlinkname</c>), for a genuine Pavlov name change.
/// </remarks>
public static class MenuLink
{
    public static string Normalise(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <param name="boundName">The name this account is already bound to, or null.</param>
    /// <param name="requestedName">The name they just typed.</param>
    /// <param name="ownerId">The Discord id already bound to <paramref name="requestedName"/>, or null.</param>
    /// <param name="selfId">The claiming member's Discord id.</param>
    /// <param name="holdsMenu">True when they currently hold a granted menu.</param>
    public static MenuClaimDecision Decide(
        string? boundName, string? requestedName, string? ownerId, string selfId, bool holdsMenu)
    {
        var want = Normalise(requestedName);
        var bound = Normalise(boundName);

        /* CHECKED FIRST, and the order is the whole rule. A member bound to another name
           can do nothing here, menu or not - it must hold even when they currently have no
           menu, which is precisely the state somebody would engineer (release, then
           re-claim as an alt) to slip past a check that only ran while holding one. */
        if (bound.Length > 0 && want != bound)
            return new MenuClaimDecision(MenuClaimAction.Locked, BoundName: boundName);

        // The reverse direction: two Discord accounts must not converge on one in-game name.
        if (ownerId is not null && !string.Equals(ownerId, selfId, StringComparison.Ordinal))
            return new MenuClaimDecision(MenuClaimAction.Taken, OwnerId: ownerId);

        if (holdsMenu && bound.Length > 0 && want == bound)
            return new MenuClaimDecision(MenuClaimAction.Release);

        return new MenuClaimDecision(MenuClaimAction.Grant);
    }
}
