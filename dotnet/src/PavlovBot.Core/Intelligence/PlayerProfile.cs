namespace PavlovBot.Core.Intelligence;

/// <param name="Current">The name they are known by now.</param>
/// <param name="AccountId">The EOS id, the only identifier they cannot change.</param>
/// <param name="PreviousNames">Every other name recorded, most recent first.</param>
public sealed record ProfileIdentity(
    string Current,
    string? AccountId,
    IReadOnlyList<string> PreviousNames)
{
    public static ProfileIdentity Unknown(string name) => new(name, null, []);
}

/// <param name="FirstSeen">Null when the bot has never seen them connect.</param>
/// <param name="PlaytimeMinutes">Recorded playtime. Not a session count.</param>
/// <param name="Online">Whether they are in a game right now.</param>
/// <param name="Servers">Servers they are on now, when online.</param>
public sealed record ProfileActivity(
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    long PlaytimeMinutes,
    bool Online,
    IReadOnlyList<string> Servers);

/// <param name="ConfirmedIps">Addresses seen on a disconnect line. Reliable.</param>
/// <param name="GuessedIps">
/// Addresses correlated from a nearby log line. NOT reliable, and labelled so.
/// </param>
/// <param name="Vpn">Null when never screened, rather than false.</param>
public sealed record ProfileNetwork(
    IReadOnlyList<string> ConfirmedIps,
    IReadOnlyList<string> GuessedIps,
    bool? Vpn,
    bool? Proxy,
    string? Asn,
    string? Country)
{
    public static ProfileNetwork Empty { get; } = new([], [], null, null, null, null);

    public int KnownAddresses => ConfirmedIps.Count + GuessedIps.Count;
}

/// <param name="ActiveBan">The ban in force now, if any.</param>
/// <param name="Warnings">Warnings inside the decay window.</param>
/// <param name="TotalWarnings">Every warning ever, including decayed ones.</param>
/// <param name="Actions">Staff actions recorded against them, newest first.</param>
public sealed record ProfileModeration(
    string? ActiveBan,
    DateTimeOffset? BanExpires,
    bool BanIsPermanent,
    int Warnings,
    int TotalWarnings,
    int Kicks,
    IReadOnlyList<string> Actions)
{
    public static ProfileModeration Empty { get; } = new(null, null, false, 0, 0, 0, []);

    public bool Banned => ActiveBan is not null;
}

/// <param name="Name">The faction, or null when they are on no roster.</param>
/// <param name="Rank">Their rank within it. "Member" for a spawn-only faction.</param>
/// <param name="DiscordId">The linked Discord account, when one is recorded.</param>
public sealed record ProfileFaction(string? Name, string? Rank, ulong? DiscordId)
{
    public static ProfileFaction None { get; } = new(null, null, null);
}

/// <param name="Balance">Null when they have no ledger yet - which is not the same as zero.</param>
/// <param name="OwedWages">Earned on duty and not yet banked.</param>
public sealed record ProfileEconomy(long? Balance, long OwedWages, long LifetimeWages)
{
    public static ProfileEconomy Empty { get; } = new(null, 0, 0);
}

/// <param name="Name">Their name, for display.</param>
/// <param name="AccountId">The account this association is really about.</param>
/// <param name="Reason">Why they are linked. A shared address, today.</param>
/// <param name="Banned">Whether the associated account is itself banned.</param>
/// <remarks>
/// A LINK, NOT AN ACCUSATION. Association is by CONFIRMED shared address, and households,
/// phone tethering and student halls all put unrelated people on one address. The profile
/// shows the link and says how it was made; deciding what it means is a person's job.
/// </remarks>
public sealed record ProfileAssociation(string Name, string AccountId, string Reason, bool Banned);

/// <summary>
/// One player, assembled from every system that knows something about them.
/// </summary>
/// <remarks>
/// AN AGGREGATE, NOT A STORE. Nothing here is persisted: every field is read from the system
/// that already owns it - the IP tracker, the ban list, the warning service, the rosters, the
/// ledger. A second copy of any of this would be a second source of truth that goes stale,
/// and the bot already has enough places a player can be recorded.
///
/// The point of the type is that staff answering "who is this" currently open six commands
/// and join the results in their head.
/// </remarks>
/// <param name="Redacted">
/// True when network detail was withheld for the viewer. Shown, rather than silently omitted:
/// a moderator should know that something exists and they cannot see it, or they will read
/// an empty network section as "clean".
/// </param>
public sealed record PlayerProfile(
    ProfileIdentity Identity,
    ProfileActivity Activity,
    ProfileNetwork Network,
    ProfileModeration Moderation,
    ProfileFaction Faction,
    ProfileEconomy Economy,
    IReadOnlyList<ProfileAssociation> Associations,
    RiskAssessment Risk,
    bool Redacted = false)
{
    /// <summary>Whether the bot has ever actually seen this player.</summary>
    /// <remarks>
    /// A profile is returned for any name asked about, so this is what separates "here is
    /// what we know" from "we have never heard of them" - and the reply has to say which,
    /// because an empty profile otherwise reads as a clean record.
    /// </remarks>
    public bool Known =>
        Identity.AccountId is not null ||
        Activity.LastSeen is not null ||
        Activity.PlaytimeMinutes > 0 ||
        Moderation.Banned ||
        Moderation.TotalWarnings > 0;
}

/// <summary>What a viewer is allowed to see of a profile.</summary>
/// <remarks>
/// TWO LEVELS, not one per role. The question a profile asks is only ever "may this person
/// see network intelligence", and inventing a finer scheme would be a permission model with
/// no second reader to keep it honest.
/// </remarks>
public enum ProfileVisibility
{
    /// <summary>Moderation and activity, no addresses and no network detail.</summary>
    Moderation,

    /// <summary>Everything, including addresses.</summary>
    Network,
}

/// <summary>
/// Removing what a viewer may not see, before it can reach a Discord message.
/// </summary>
/// <remarks>
/// A PURE FUNCTION OVER THE WHOLE PROFILE, and deliberately not a set of checks at each
/// display site. Redaction done at the point of rendering is redaction that gets forgotten
/// the day somebody adds a field, and the failure is silent and unrecoverable: an address in
/// a channel cannot be un-seen because the message was deleted.
///
/// So the profile is narrowed ONCE, on the way out of the service, and the Discord layer
/// never holds data it must not print. A test asserts that a redacted profile contains no
/// address anywhere in it - which is a property of this function rather than of every
/// embed built from it.
/// </remarks>
public static class ProfileRedaction
{
    /// <summary>A profile narrowed to what this viewer may see.</summary>
    public static PlayerProfile For(PlayerProfile profile, ProfileVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (visibility == ProfileVisibility.Network) return profile;

        var hadNetwork =
            profile.Network.KnownAddresses > 0 ||
            profile.Network.Asn is not null ||
            profile.Associations.Count > 0 ||
            profile.Risk.Signals.Any(s => s.Sensitive);

        return profile with
        {
            /* THE VERDICTS SURVIVE, THE ADDRESSES DO NOT. "On a VPN" is a moderation fact a
               moderator needs and reveals nothing about where somebody lives; the address
               itself is the sensitive part. Keeping the flags is what stops redaction making
               the feature useless to the people who use it most. */
            Network = ProfileNetwork.Empty with
            {
                Vpn = profile.Network.Vpn,
                Proxy = profile.Network.Proxy,
                Country = profile.Network.Country,
            },

            /* ASSOCIATIONS GO ENTIRELY. Every one of them is derived from a shared address,
               so "these two accounts are linked" leaks the address relationship even without
               printing the address. */
            Associations = [],

            Risk = profile.Risk with
            {
                Signals = [.. profile.Risk.Signals.Select(Narrow)],
            },

            Redacted = hadNetwork,
        };
    }

    /// <summary>
    /// A signal with its evidence removed, but its claim and weight intact.
    /// </summary>
    /// <remarks>
    /// The signal itself is NOT dropped. Removing it would change the visible score between
    /// viewers, so two moderators comparing notes would see different numbers for one player
    /// and neither would know why. The reason stays; only the address behind it goes.
    ///
    /// THE SUMMARY IS SCRUBBED TOO, AND FOR EVERY SIGNAL. Clearing the Evidence field alone
    /// was the first version of this and it leaked: a signal whose SUMMARY read "Shares
    /// 203.0.113.77 with banned player Bob" passed straight through, because the address was
    /// in the sentence rather than the evidence slot. Caught by the serialise-and-search test
    /// rather than by review, which is the argument for writing that test the blunt way.
    ///
    /// Scrubbing every signal rather than only the ones marked Sensitive is deliberate. The
    /// flag is set by whoever writes the signal, so relying on it makes redaction depend on
    /// an author remembering - and the cost of scrubbing a signal that has no address in it
    /// is nothing at all.
    /// </remarks>
    private static RiskSignal Narrow(RiskSignal signal) => signal with
    {
        Summary = Addresses.Scrub(signal.Summary),
        Evidence = signal.Sensitive ? "withheld - needs network access" : Addresses.Scrub(signal.Evidence),
    };

    /// <summary>What a viewer at this access level may see.</summary>
    public static ProfileVisibility VisibilityFor(bool hasNetworkAccess) =>
        hasNetworkAccess ? ProfileVisibility.Network : ProfileVisibility.Moderation;
}
