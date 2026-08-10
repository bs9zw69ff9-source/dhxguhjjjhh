namespace PavlovBot.Core.Events;

/// <summary>
/// Which lens a staff action belongs to.
/// </summary>
/// <remarks>
/// PURE, AND SEPARATE FROM THE AUDIT LOG, so the routing is testable without a store and so
/// adding a category later is one table to change rather than a search through call sites.
///
/// EVERY ACTION LANDS SOMEWHERE. The fallback is <see cref="EventCategory.Staff"/> rather
/// than a "misc" bucket or a dropped event: an action recorded in the audit log and missing
/// from the timeline is the failure this whole feature exists to remove, and a new command
/// added next year must appear without anybody remembering to extend this table.
///
/// THE TABLE IS EXPLICIT, unlike ChannelStaffLog's substring test for bans. That rule works
/// there because "ban" is a distinctive word and the cost of being wrong is a line in the
/// wrong channel. Here a mis-categorised event is invisible to the filter somebody is
/// investigating with, so it is worth naming them.
/// </remarks>
public static class EventMapping
{
    private static readonly Dictionary<string, EventCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- security: detections, not decisions ----
        ["autoban"] = EventCategory.Security,
        ["vpnban"] = EventCategory.Security,
        ["evasion"] = EventCategory.Security,
        ["flag"] = EventCategory.Security,

        // ---- economy ----
        ["givecaps"] = EventCategory.Economy,
        ["adjustcaps"] = EventCategory.Economy,
        ["payroll"] = EventCategory.Economy,
        ["money-alert"] = EventCategory.Economy,

        // ---- faction ----
        ["whitelist"] = EventCategory.Faction,
        ["whitelist-add"] = EventCategory.Faction,
        ["whitelist-remove"] = EventCategory.Faction,
        ["promotion"] = EventCategory.Faction,
        ["demotion"] = EventCategory.Faction,
        ["subclass"] = EventCategory.Faction,
        ["suspendrank"] = EventCategory.Faction,
        ["unsuspendrank"] = EventCategory.Faction,
        ["tester-add"] = EventCategory.Faction,
        ["tester-remove"] = EventCategory.Faction,

        // ---- server: things done TO a server rather than to a player ----
        ["auto-restart"] = EventCategory.Server,
        ["auto-restart-stopped"] = EventCategory.Server,
        ["manual"] = EventCategory.Server,
        ["rotatemap"] = EventCategory.Server,
        ["serverswitch"] = EventCategory.Server,
        ["restart"] = EventCategory.Server,
    };

    /// <summary>The category an audit action belongs to.</summary>
    public static EventCategory CategoryOf(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return EventCategory.Staff;

        return Categories.TryGetValue(action.Trim(), out var category) ? category : EventCategory.Staff;
    }

    /// <summary>
    /// The stable slug an audit action is stored under.
    /// </summary>
    /// <remarks>
    /// PREFIXED WITH ITS CATEGORY, so a kind read out of a database browser says what it is
    /// without a lookup, and so two systems cannot collide on a bare word like "restart".
    /// </remarks>
    public static string KindOf(string? action)
    {
        var name = string.IsNullOrWhiteSpace(action) ? "unknown" : action.Trim().ToLowerInvariant();
        return $"{CategoryOf(action).ToString().ToLowerInvariant()}.{name}";
    }

    /// <summary>
    /// Whether an automated system produced this rather than a person.
    /// </summary>
    /// <remarks>
    /// Used by the staff-activity view, which is about what PEOPLE did. Counting the VPN
    /// responder's bans towards a moderator called "auto" would put a phantom at the top of
    /// every leaderboard and make the anomaly detection meaningless.
    /// </remarks>
    public static bool IsAutomated(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ||
        string.Equals(actor.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(actor.Trim(), "system", StringComparison.OrdinalIgnoreCase);
}
