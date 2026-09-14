using System.Text.Json;
using System.Text.Json.Serialization;
using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Security;
using PavlovBot.Host.Storage;
using PavlovBot.Core.Data;

using PavlovBot.Core.Factions;

namespace PavlovBot.Host.Discord;

/// <summary>Which Discord roles map to which powers. Set at runtime by <c>/setroles</c>.</summary>
/// <remarks>
/// READS TWO KEY SHAPES, WRITES ONE. The Node bot stored this dataset as
/// <c>{"modRoleId":"123","adminRoleId":"...","gambinoRoleId":"...", ...}</c> - suffixed
/// names, and ids as STRINGS because that is what discord.js hands you. Those files are
/// still the files this bot reads: startup imports <c>roles.json</c> verbatim into the
/// store. Against the unsuffixed names below, every one of those keys is an UNKNOWN
/// PROPERTY, so the whole mapping deserialised to null and every role-gated command refused
/// everybody - while owners, matched by user id, kept working and made it look like one
/// tier misbehaving rather than the mapping being gone. See the legacy properties.
///
/// New writes use the unsuffixed names only. The suffixed ones are read-only aliases: they
/// fill a slot the canonical key did not, and they are never written back, so the first
/// <c>/setroles</c> after an upgrade normalises the dataset for good.
/// </remarks>
public sealed record RoleMap
{
    private readonly ulong? _mod;
    private readonly ulong? _admin;
    private readonly ulong? _leader;
    private readonly ulong? _police;
    private readonly ulong? _mafia;
    private readonly ulong? _nypd;

    private readonly Dictionary<string, ulong> _factionRoles = New();

    public ulong? ModRole { get => _mod; init => _mod = value; }
    public ulong? AdminRole { get => _admin; init => _admin = value; }
    public ulong? FactionLeaderRole { get => _leader; init => _leader = value; }
    public ulong? PoliceRole { get => _police; init => _police = value; }

    /// <summary>
    /// Faction name -> the Discord role that may edit THAT faction's roster.
    /// </summary>
    /// <remarks>
    /// A MAP, BECAUSE THE FACTIONS ARE DATA NOW. This used to be two fixed slots named after
    /// the built-in set - one for the mafias and one for the police - and a bot running a
    /// configured faction set had neither. Those slots granted nothing, so its only way to
    /// delegate a roster was the leader role, which manages EVERY roster. The separation rule
    /// was failing open on exactly the deployments that most needed it.
    ///
    /// REBUILT IN THE SETTER, NOT AN INITIALIZER. A dictionary's comparer is not serialised,
    /// so the value handed back by the deserializer is ORDINAL however this was built; and
    /// <c>record with { ... }</c> bypasses initializers, so an initializer cannot guarantee
    /// non-null either - System.Text.Json ignores the nullable annotation and will happily
    /// assign null from <c>"factionRoles": null</c>.
    /// </remarks>
    public IReadOnlyDictionary<string, ulong> FactionRoles
    {
        get => _factionRoles;
        init => _factionRoles = New(value);
    }

    /// <summary>Manages BOTH mafia whitelists. Superseded by <see cref="FactionRoles"/>.</summary>
    /// <remarks>
    /// KEPT SO A ROLES DATASET WRITTEN BY AN OLDER C# BUILD STILL READS. Node never had this
    /// key - it held Gambino and Colombo separately - so a file from that era arrives through
    /// <see cref="GambinoRoleId"/> and <see cref="ColomboRoleId"/> instead, which land in
    /// <see cref="FactionRoles"/> where per-faction delegation actually lives.
    /// </remarks>
    public ulong? MafiaRole { get => _mafia; init => _mafia = value; }

    public ulong? NypdRole { get => _nypd; init => _nypd = value; }

    // ---- the Node bot's key names. Read-only aliases; see the type remarks. ----

    /// <summary>
    /// Fill a canonical slot from the Node bot's suffixed key, if nothing filled it already.
    /// </summary>
    /// <remarks>
    /// SET-ONLY, SO IT IS NEVER SERIALISED. System.Text.Json writes only properties it can
    /// read, so a getterless property is deserialise-in, never write-out - which is the whole
    /// contract these aliases need: understand the old shape, emit the new one.
    ///
    /// The guard is "only if the canonical key did not set it". In a file holding just the
    /// old keys - every Node-era install - the alias is the only source and always applies.
    /// In one holding both, the canonical name wins, which is the name this bot writes.
    /// </remarks>
    [JsonPropertyName("modRoleId")]
    public JsonElement ModRoleId { init => _mod ??= Id(value); }

    [JsonPropertyName("adminRoleId")]
    public JsonElement AdminRoleId { init => _admin ??= Id(value); }

    [JsonPropertyName("factionLeaderRoleId")]
    public JsonElement FactionLeaderRoleId { init => _leader ??= Id(value); }

    [JsonPropertyName("policeRoleId")]
    public JsonElement PoliceRoleId { init => _police ??= Id(value); }

    [JsonPropertyName("nypdRoleId")]
    public JsonElement NypdRoleId { init => _nypd ??= Id(value); }

    /// <summary>
    /// Node held the two mafia rosters as SEPARATE roles, so they land as separate entries.
    /// </summary>
    /// <remarks>
    /// Collapsing them into <see cref="MafiaRole"/> would hand each family's leader the other
    /// family's whitelist - the exact separation <see cref="FactionRoles"/> exists to keep.
    /// </remarks>
    [JsonPropertyName("gambinoRoleId")]
    public JsonElement GambinoRoleId { init => Adopt("Gambino", value); }

    [JsonPropertyName("colomboRoleId")]
    public JsonElement ColomboRoleId { init => Adopt("Colombo", value); }

    /// <summary>Take a legacy per-faction role, unless <see cref="FactionRoles"/> named it.</summary>
    private void Adopt(string faction, JsonElement value)
    {
        if (Id(value) is { } id && !_factionRoles.ContainsKey(faction)) _factionRoles[faction] = id;
    }

    /// <summary>
    /// One role id out of the old file, whatever JSON shape it was stored in.
    /// </summary>
    /// <remarks>
    /// STRINGS, BECAUSE DISCORD.JS IDS ARE STRINGS - a snowflake does not survive a double,
    /// so every JS library hands them out as text and the Node bot stored them that way.
    /// Numbers are accepted too for a file somebody has hand-edited. Anything else, and the
    /// empty string the old seed used for "unset", is no role rather than an exception: this
    /// runs while deserialising a dataset, and throwing here would turn one malformed key
    /// into every permission in the bot reading as unset.
    /// </remarks>
    private static ulong? Id(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ulong.TryParse(value.GetString(), out var id) && id != 0 ? id : null,
        JsonValueKind.Number => value.TryGetUInt64(out var id) && id != 0 ? id : null,
        _ => null,
    };

    private static Dictionary<string, ulong> New(IReadOnlyDictionary<string, ulong>? from = null) =>
        from is null
            ? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ulong>(from, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The role that manages one faction's roster, or null for none.
    /// </summary>
    /// <remarks>
    /// The map first, then the legacy slots for the three built-in names. Order matters:
    /// an admin who sets Gambino's role explicitly must not keep getting the old shared
    /// mafia role, or the new setting would appear to do nothing.
    ///
    /// THE SCAN IS NOT LAZINESS, even with the comparer now rebuilt in the setter. That
    /// rebuild covers every path through this type; the scan covers the one it cannot - a
    /// map that reached here some other way - and the failure it guards has the worst shape
    /// a permissions bug can have: case matching works in memory, then a role stops granting
    /// anything once the process cycles. The map holds one entry per faction, so this is a
    /// scan over a handful of items on a path that already reads a file.
    /// </remarks>
    public ulong? RoleFor(string? faction)
    {
        if (string.IsNullOrWhiteSpace(faction)) return null;
        if (FactionRoles.TryGetValue(faction, out var id)) return id;

        foreach (var configured in FactionRoles)
        {
            if (Same(configured.Key, faction)) return configured.Value;
        }

        return faction switch
        {
            var f when Same(f, "Gambino") || Same(f, "Colombo") => MafiaRole,
            var f when Same(f, "NYPD") => NypdRole,
            _ => null,
        };
    }

    /// <summary>
    /// One line for the startup summary: which tiers a role actually resolved for.
    /// </summary>
    /// <remarks>
    /// A METHOD, NOT A PROPERTY, so the serialiser does not write it back into the dataset.
    ///
    /// Said at startup because "the mapping is stored" and "the mapping was read" are
    /// different facts and only the second one grants anything. A roles dataset written in a
    /// shape this type does not understand reads as entirely unset, and from outside that is
    /// indistinguishable from nobody having run /setroles - which is a week of looking at
    /// the wrong thing. IDS, NOT NAMES: resolving a name needs the guild, this runs before
    /// the gateway is up, and the id is what an operator matches against the file anyway.
    /// </remarks>
    public string Describe()
    {
        var tiers = new List<string>();
        void Add(string label, ulong? id) => tiers.Add($"{label} {(id is { } value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unset")}");

        Add("mod", ModRole);
        Add("admin", AdminRole);
        Add("whitelist leader", FactionLeaderRole);
        Add("police", PoliceRole);

        var factions = _factionRoles.Count == 0
            ? "no per-faction roles"
            : $"{_factionRoles.Count} per-faction role(s): {string.Join(", ", _factionRoles.Keys.Order(StringComparer.OrdinalIgnoreCase))}";

        return $"{string.Join(", ", tiers)}; {factions}";
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static RoleMap Empty { get; } = new();
}

/// <summary>
/// Answering "may this person do this".
/// </summary>
/// <remarks>
/// Two sources of authority, and the split matters:
///
///   OWNERS COME FROM THE ENVIRONMENT, not from a role. An owner is whoever the person
///   holding the server's <c>.env</c> says it is. A role-based owner could be granted by
///   anyone with Manage Roles, which is a privilege escalation with extra steps.
///
///   EVERYTHING ELSE COMES FROM CONFIGURED ROLES, so a server can arrange its own staff
///   structure without touching the bot.
///
/// Higher tiers imply lower ones - an admin is a mod - because the alternative is every
/// owner also holding four other roles just to use the bot.
///
/// EVERY PREDICATE TAKES <see cref="IUser"/>, NOT <see cref="IGuildUser"/>, and that is a
/// fix rather than a preference. They used to take IGuildUser, so every caller wrote
/// <c>command.User as IGuildUser</c> - and a cast that fails yields NULL, which every
/// predicate reads as "no permissions". An owner would then be refused a mod command while
/// still passing the owner-tier ones, because those alone checked the id directly. The role
/// lookups still need the guild member and still degrade to false without it; ownership
/// never does, because it is a set of ids and needs nothing from the guild at all.
/// </remarks>
public sealed class Access
{
    private readonly SerializedStore _store;
    private readonly IReadOnlySet<ulong> _owners;
    private readonly IReadOnlySet<ulong> _superOwners;

    /// <summary>
    /// How many owners the DEPLOYMENT set, ignoring the compiled-in one.
    /// </summary>
    /// <remarks>
    /// Only used to decide whether "no owners are configured at all" is worth saying. The
    /// built-in super owner means <see cref="_superOwners"/> is never empty, so counting the
    /// resolved sets would silence that hint permanently - and it is the hint that tells a
    /// fresh install why nothing works.
    /// </remarks>
    private readonly int _configuredOwnerCount;

    private readonly FactionSet _factions;

    /// <summary>
    /// The staff guild's membership for a user who is not in one here, or null for none.
    /// </summary>
    /// <remarks>
    /// SETTABLE RATHER THAN INJECTED, and for the usual reason in this composition root:
    /// resolving it needs the gateway, the gateway is built from every command, and nearly
    /// every command needs this type. A constructor dependency closes that loop and the
    /// container deadlocks on it - see GatewayChannelRenamer, and the outage that taught it.
    ///
    /// Null leaves behaviour exactly as it was: roles read off the interaction and nothing
    /// else, which is correct inside a guild and was the whole problem outside one.
    /// </remarks>
    private Func<ulong, IGuildUser?>? _homeMember;

    /// <summary>Attach the home-guild lookup. See <see cref="_homeMember"/>.</summary>
    public void UseHomeGuild(Func<ulong, IGuildUser?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _homeMember = lookup;
    }

    public Access(SerializedStore store, IEnumerable<ulong> owners, IEnumerable<ulong>? superOwners = null,
        FactionSet? factions = null)
    {
        _factions = factions ?? FactionRegistry.Default;
        _store = store;
        _owners = owners.ToHashSet();

        var configuredSuper = (superOwners ?? []).ToHashSet();
        _configuredOwnerCount = _owners.Count + configuredSuper.Count;

        /* THE BUILT-IN SUPER OWNER IS ADDED HERE AND ASSERTED HERE, and the assertion is
           over the RESULT rather than the intent - so a filter added downstream that removes
           it again fails too, not just the deletion of this line. See OwnerGuard: an owner
           who can be removed from .env is an owner who can be locked out of their own bot. */
        _superOwners = OwnerGuard.WithBuiltIn(configuredSuper);
    }

    public RoleMap Roles => _store.Read(Datasets.Roles, RoleMap.Empty);

    /// <summary>
    /// The guild member to read roles from: the interaction's own, or the staff guild's.
    /// </summary>
    /// <remarks>
    /// THE INTERACTION'S MEMBER WINS when there is one, because it is the guild the person
    /// is actually standing in and it needs no lookup. The home guild answers only when
    /// there is no member at all - a DM, or a server this bot was carried into - which is
    /// exactly where every role check used to answer false.
    ///
    /// A user this resolves for in a DM gets the same access they would have had in the
    /// staff guild. That is the point, and it is also the whole of the widening: no role is
    /// granted that the guild has not granted.
    /// </remarks>
    private IGuildUser? Member(IUser? user) => user switch
    {
        null => null,
        IGuildUser member => member,
        _ => _homeMember?.Invoke(user.Id),
    };

    private bool Has(IUser? user, ulong? roleId) =>
        roleId is { } id && Member(user) is { } member && member.RoleIds.Contains(id);

    /// <summary>
    /// Whether this user holds a role, and whether their roles could be read at all.
    /// </summary>
    /// <remarks>
    /// THE TWO ANSWERS Has COLLAPSES INTO ONE. "false" covers a member the bot cannot see,
    /// a member whose roles it cannot read, and a member who simply does not have the role -
    /// three completely different fixes, and from outside the bot they are indistinguishable.
    /// A mapping that reads correctly and grants nothing is the shape this is for.
    /// </remarks>
    public RoleStanding StandingOn(IUser? user, ulong? roleId)
    {
        if (roleId is not { } id) return RoleStanding.Unset;
        if (Member(user) is not { } member) return RoleStanding.NoMember;
        return member.RoleIds.Contains(id) ? RoleStanding.Held : RoleStanding.NotHeld;
    }

    /// <summary>Every role id the bot can see on this user, or null when it cannot see them.</summary>
    public IReadOnlyCollection<ulong>? VisibleRoles(IUser? user) => Member(user)?.RoleIds;

    public bool IsSuperOwner(IUser? user)
    {
        if (user is null) return false;

        /* AT THE POINT OF USE, not only at construction. Patching the constructor is the
           obvious move; this makes the answer itself depend on the constant, so the id has
           to be right here as well. Compiled-in super owner short-circuits before the set is
           consulted at all, so emptying the set does not change the answer either. */
        if (user.Id == OwnerGuard.SuperOwnerId) return true;

        return _superOwners.Contains(user.Id);
    }

    /// <summary>
    /// A super owner IS an owner. Id-based, so it holds wherever the check is made.
    /// </summary>
    public bool IsOwner(IUser? user) =>
        // Through IsSuperOwner rather than reading the set again, so the compiled-in
        // short-circuit covers owner tier too rather than only super-owner tier.
        user is not null && (IsSuperOwner(user) || _owners.Contains(user.Id));

    public bool IsAdmin(IUser? user) =>
        IsOwner(user) || Has(user, Roles.AdminRole) ||
        // Discord's own Administrator permission counts: somebody who can delete the guild
        // is not meaningfully restricted by a bot role check. Through Member, so it holds in
        // a DM too - an administrator who loses admin by messaging the bot is the same bug.
        (Member(user)?.GuildPermissions.Administrator ?? false);

    public bool IsMod(IUser? user) => IsAdmin(user) || Has(user, Roles.ModRole);

    public bool IsFactionLeader(IUser? user) => IsAdmin(user) || Has(user, Roles.FactionLeaderRole);

    public bool IsPolice(IUser? user) => IsMod(user) || Has(user, Roles.PoliceRole);

    /// <summary>
    /// Which faction rosters this member may edit.
    /// </summary>
    /// <remarks>
    /// A faction leader manages EVERY roster; a per-faction role manages only its own, which
    /// is what stopped one faction's leader adding themselves to another's whitelist. An
    /// OWNER manages all of them, via <see cref="IsFactionLeader"/>.
    ///
    /// WALKS THE LOADED SET rather than three compiled-in names. It used to read
    /// <c>if (Has(MafiaRole)) add Gambino, Colombo</c> and the same for NYPD, which meant a
    /// bot running a configured faction set had no per-faction delegation at all: those two
    /// role slots matched none of its factions, so the leader role - which manages EVERY
    /// roster - was the only thing that worked. The separation rule failed open precisely
    /// where it was needed.
    ///
    /// One role may still manage several factions; that is now something an admin sets, by
    /// naming the same role twice, rather than something the code decides for them.
    /// </remarks>
    public IReadOnlyCollection<string> ManageableFactions(IUser? user)
    {
        if (IsFactionLeader(user)) return [.. _factions.Names];

        var roles = Roles;

        // Only factions that EXIST here. The legacy fallback answers for the three built-in
        // names, and a themed bot must not report managing a faction it has never heard of.
        return [.. _factions.Names.Where(name => Has(user, roles.RoleFor(name)))];
    }

    public bool CanManage(IUser? user, string faction) =>
        ManageableFactions(user).Contains(faction, StringComparer.OrdinalIgnoreCase);

    public StaffTier TierOf(IUser? user) =>
        StaffHierarchy.TierOf(IsSuperOwner(user), IsOwner(user), IsAdmin(user), IsMod(user));

    /// <summary>The label shown on the help menu.</summary>
    public string DescribeAccess(IUser? user) => TierOf(user) switch
    {
        StaffTier.SuperOwner => "SUPER OWNER",
        StaffTier.Owner => "OWNER",
        StaffTier.Admin => "ADMIN",
        StaffTier.Mod => "MODERATOR",
        _ => IsFactionLeader(user) ? "WHITELIST LEADER" : IsPolice(user) ? "POLICE" : "PUBLIC",
    };

    /// <summary>
    /// Why a refusal happened, in the words of what to change.
    /// </summary>
    /// <remarks>
    /// "You need Mod access" is true and useless: it does not distinguish an unconfigured
    /// MOD_ROLE from an id missing out of OWNER_IDS from a role the person simply lacks, and
    /// those are three different fixes. This is only ever shown to the person refused, and
    /// it describes THEIR OWN standing - it never lists who does hold a tier, because that
    /// turns every refusal into a map of who to social-engineer.
    /// </remarks>
    public string ExplainRefusal(IUser? user, RequiredAccess required)
    {
        var lines = new List<string> { $"You need **{required}** access to use that." };

        var held = DescribeAccess(user);
        lines.Add($"You currently hold: **{held}**.");

        if (Member(user) is null)
        {
            /* No member ANYWHERE - not in this interaction, and not in the staff guild
               either. Distinct from lacking a role, and it used to look identical. Owner
               tiers still work in this state because they are id-based, which is exactly
               the confusing part. */
            lines.Add(
                "The bot could not read your roles here, so only owner access - which goes by " +
                "user id - was checked. Outside the server your roles come from the staff guild; " +
                "if you are not in it, there is nothing to read.");
        }
        else if (required is RequiredAccess.Mod && Roles.ModRole is null)
        {
            lines.Add("No moderator role is configured. An admin can set one with `/setroles`.");
        }
        else if (required is RequiredAccess.Admin && Roles.AdminRole is null)
        {
            lines.Add("No admin role is configured. An owner can set one with `/setroles`.");
        }
        else if (VisibleRoles(user) is { Count: 0 })
        {
            /* SEEN, BUT WITH NO ROLES TO READ - a third state, distinct again from lacking
               the one role. A staff member who swears they have the role and still reads as
               PUBLIC is THIS case, not "could not read your roles" above: the bot resolved
               them to a member and that member carries nothing. AFTER the unconfigured-role
               checks on purpose - if nothing is mapped for the tier, "run /setroles" is the
               fix whatever roles the caller has, so it must not be preempted by this.

               For a USER-INSTALLED APP this is the ordinary shape, not an edge one. Run a
               command in a server the bot itself is not a member of and Discord hands it the
               caller with no roles attached - there is no guild membership for it to read
               them from - and from the refusal alone that is indistinguishable from the
               Server Members intent being off. Both point the same way: the roles that
               should grant this are somewhere the bot is not looking, and naming both is
               what turns "it says I'm public" into something the operator can act on. */
            lines.Add(
                "The bot can see you but reads no roles on you at all. If you do hold a staff " +
                "role, either the bot is not a member of THIS server - a user-installed app " +
                "cannot read roles in a server it was only carried into - or the **Server " +
                "Members** intent is off in the developer portal. When your staff roles live " +
                "in another server, set `HOME_GUILD_ID` to it so they can be read from there.");
        }

        if (required is RequiredAccess.Owner && _configuredOwnerCount == 0)
            lines.Add("No owners are configured at all - set `OWNER_IDS` in the bot's `.env`.");

        return string.Join("\n", lines);
    }
}

/// <summary>Where a user stands against one mapped role.</summary>
public enum RoleStanding
{
    /// <summary>No role is mapped to that tier, so nothing can grant it.</summary>
    Unset,

    /// <summary>The bot cannot see this user as a guild member, so it read no roles at all.</summary>
    NoMember,

    /// <summary>Seen, and does not have it.</summary>
    NotHeld,

    /// <summary>Seen, and has it.</summary>
    Held,
}

/// <summary>The permission a command requires.</summary>
public enum RequiredAccess
{
    Public,
    Police,
    FactionLeader,
    Mod,
    Admin,
    Owner,
}

public static class AccessChecks
{
    public static bool Allows(this Access access, RequiredAccess required, SocketSlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return access.Allows(required, (SocketInteraction)command);
    }

    /// <summary>
    /// The same check for a button, select menu or modal.
    /// </summary>
    /// <remarks>
    /// Components need this because a panel MESSAGE OUTLIVES the permission that opened it.
    /// Checking only when the panel is created would leave a former owner holding a working
    /// control panel in their history - which is exactly who would try it.
    /// </remarks>
    public static bool Allows(this Access access, RequiredAccess required, SocketInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(interaction);
        return access.Allows(required, interaction.User);
    }

    /// <summary>
    /// The decision itself, over a user rather than an interaction.
    /// </summary>
    /// <remarks>
    /// SEPARATE SO IT CAN BE TESTED. <c>SocketInteraction</c> belongs to Discord.Net's
    /// gateway and cannot be constructed, so while this logic lived inside the interaction
    /// overload the only way to cover it was for a test to restate the same switch - a guard
    /// that passes forever once the two copies drift, which is worse than no guard because it
    /// reads as covered. The overloads above are now the only thing not directly tested, and
    /// all they do is reach for <c>.User</c>.
    /// </remarks>
    public static bool Allows(this Access access, RequiredAccess required, IUser? user)
    {
        ArgumentNullException.ThrowIfNull(access);

        /* OWNERS AND SUPER OWNERS PASS EVERY GATE, checked FIRST and by user id.

           This is the one authority that must not depend on anything the guild can fail to
           tell us. Every other branch below needs the interaction's user to resolve to an
           IGuildUser to read roles from; when that does not happen the role checks all
           answer false, and an owner was refused a Mod command while the Owner-tier
           commands - the only ones that consulted the id - kept working. A ladder where the
           top rung fails and the rung above it works is not a ladder.

           It is also just what "owner" is meant to mean. Every tier below is a subset of
           what an owner may do, so there is no gate an owner should be stopped at. */
        if (access.IsOwner(user)) return true;

        return required switch
        {
            RequiredAccess.Public => true,
            RequiredAccess.Police => access.IsPolice(user),
            RequiredAccess.FactionLeader => access.IsFactionLeader(user) || access.ManageableFactions(user).Count > 0,
            RequiredAccess.Mod => access.IsMod(user),
            RequiredAccess.Admin => access.IsAdmin(user),
            RequiredAccess.Owner => access.IsOwner(user),   // false here, and kept for the reader
            _ => false,
        };
    }

    /// <summary>
    /// The refusal message, when the person refused is not to hand.
    /// </summary>
    /// <remarks>
    /// Says WHAT is needed, not who has it. Listing the holders turns every refusal into a
    /// map of who to social-engineer. Prefer <see cref="Access.ExplainRefusal"/>, which adds
    /// what the person actually holds and why - a bare "you need Mod" cannot tell an
    /// unconfigured role apart from a missing one.
    /// </remarks>
    public static string Refusal(RequiredAccess required) =>
        $"You need **{required}** access to use that.";

    /// <summary>The refusal for an interaction, explaining the refused person's own standing.</summary>
    public static string Refusal(this Access access, RequiredAccess required, SocketInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(interaction);
        return access.ExplainRefusal(interaction.User, required);
    }
}
