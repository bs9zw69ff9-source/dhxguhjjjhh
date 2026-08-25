using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Host.Configuration;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/setroles</c> - map Discord roles to the bot's permission tiers.
/// </summary>
/// <remarks>
/// Roles are configured at RUNTIME rather than in the environment so a server can rearrange
/// its staff structure without a redeploy. Owners deliberately are NOT here - an
/// owner granted by a role could be granted by anyone with Manage Roles, which is a
/// privilege escalation with extra steps. See <see cref="Access"/>.
/// </remarks>
public sealed class SetRolesCommand : ISlashCommand
{
    /// <summary>Discord's cap on options for one command.</summary>
    private const int MaxOptions = 25;

    /// <summary>The three tiers that exist whatever the RP is.</summary>
    private const int FixedOptions = 3;

    private readonly SerializedStore _store;
    private readonly Access _access;
    private readonly ILogger<SetRolesCommand> _logger;

    /// <summary>Option name -> faction name, built once from the loaded set.</summary>
    private readonly IReadOnlyList<(string Option, string Faction)> _factionOptions;

    /// <summary>Factions that could not be given an option, so the reply can say which.</summary>
    private readonly IReadOnlyList<string> _unnameable;

    /// <summary>False when no enabled command checks police access, so the slot is dead.</summary>
    private readonly bool _police;

    public SetRolesCommand(SerializedStore store, Access access, FactionSet factions,
        BotOptions options, ILogger<SetRolesCommand> logger)
    {
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _access = access;
        _logger = logger;

        /* THE POLICE SLOT IS SHOWN ONLY WHEN IT DOES SOMETHING. RequiredAccess.Police is
           checked nowhere but PoliceCommands, so with those disabled the role grants nothing
           and offering it on a Fallout bot is an option that lies. */
        var disabled = options.DisabledCommands.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _police = PoliceCommandNames.All.Any(name => !disabled.Contains(name));

        var named = new List<(string, string)>();
        var unnameable = new List<string>();

        foreach (var faction in factions.Names.Order(StringComparer.OrdinalIgnoreCase))
        {
            var option = CommandOptionName.Slug(faction, "_role");

            // A collision would make the second option unreachable, and Discord rejects the
            // whole registration for a duplicate name - taking every command with it.
            if (option is null || named.Any(o => string.Equals(o.Item1, option, StringComparison.OrdinalIgnoreCase)))
            {
                unnameable.Add(faction);
                continue;
            }

            named.Add((option, faction));
        }

        /* DISCORD REJECTS THE BULK OVERWRITE WHOLE. Twenty-six options would not shorten this
           command, it would empty the picker - so the cap is enforced here, where the excess
           can be named, rather than discovered as every command vanishing after a deploy. */
        var room = MaxOptions - FixedOptions - (_police ? 1 : 0);
        if (named.Count > room)
        {
            foreach (var (_, faction) in named.Skip(room)) unnameable.Add(faction);
            named = [.. named.Take(room)];
        }

        _factionOptions = named;
        _unnameable = unnameable;

        if (_unnameable.Count > 0)
        {
            _logger.LogWarning(
                "setroles cannot offer a role option for {Count} faction(s): {Factions}. " +
                "Their rosters are manageable only by the whitelist leader role.",
                _unnameable.Count, string.Join(", ", _unnameable));
        }
    }

    public string Name => "setroles";

    public ApplicationCommandProperties Build()
    {
        var builder = new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Map Discord roles to the bot's permission tiers")
            .AddOption("mod_role", ApplicationCommandOptionType.Role, "Moderator", isRequired: false)
            .AddOption("admin_role", ApplicationCommandOptionType.Role, "Admin", isRequired: false)
            .AddOption("whitelist_leader_role", ApplicationCommandOptionType.Role, "Manages every whitelist", isRequired: false);

        if (_police)
            builder.AddOption("police_role", ApplicationCommandOptionType.Role, "Police officer", isRequired: false);

        foreach (var (option, faction) in _factionOptions)
        {
            // Description carries the REAL name, which is what the operator recognises -
            // the option name is a slug and "brotherhood_of_steel_role" is not it.
            builder.AddOption(option, ApplicationCommandOptionType.Role,
                $"Manages the {faction} whitelist", isRequired: false);
        }

        return builder.Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", _access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        ulong? Role(string name) => (command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as IRole)?.Id;

        /* Only the options actually SUPPLIED are changed. Rebuilding the whole map from
           this one invocation would silently clear every role the caller did not mention -
           so setting the mod role would unset the admin role. */
        var updated = await _store.UpdateAsync(Datasets.Roles, RoleMap.Empty, current =>
        {
            // Same rule one level down: start from what is stored, overwrite only what was
            // named. A fresh dictionary here would clear every faction not in this call.
            var factionRoles = new Dictionary<string, ulong>(current.FactionRoles, StringComparer.OrdinalIgnoreCase);
            foreach (var (option, faction) in _factionOptions)
            {
                if (Role(option) is { } id) factionRoles[faction] = id;
            }

            return current with
            {
                ModRole = Role("mod_role") ?? current.ModRole,
                AdminRole = Role("admin_role") ?? current.AdminRole,
                FactionLeaderRole = Role("whitelist_leader_role") ?? current.FactionLeaderRole,
                PoliceRole = Role("police_role") ?? current.PoliceRole,
                FactionRoles = factionRoles,
            };
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("setroles | by={By}", command.User.Username);

        var map = updated.Value;
        var tiers = new List<(string Label, ulong? Id)>
        {
            ("Moderator", map.ModRole), ("Admin", map.AdminRole),
            ("Whitelist leader", map.FactionLeaderRole),
        };
        if (_police) tiers.Add(("Police", map.PoliceRole));

        // RoleFor, not the map directly, so a deployment still on the legacy mafia/NYPD
        // slots sees the role it actually has rather than "not set".
        var factionLines = _factionOptions
            .Select(o => (Label: o.Faction, Id: map.RoleFor(o.Faction)));

        var lines = tiers.Concat(factionLines)
            .Select(r => $"**{r.Label}** — {(r.Id is { } id ? $"<@&{id}>" : "*not set*")}");

        var embed = Theme.Success("Roles updated", string.Join("\n", lines))
            .AddField("Owners", "Set through the environment, never a role - a role-based owner could be granted by anyone with Manage Roles.");

        if (_unnameable.Count > 0)
        {
            embed.AddField("No option available",
                $"{string.Join(", ", _unnameable.Select(Sanitize.Code))} — manageable only by the whitelist leader role.");
        }

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <param name="AddedBy">Who granted it, so a perk nobody remembers granting is traceable.</param>
public sealed record Donator(string Player, string AddedBy, DateTimeOffset At);

/// <summary>
/// <c>/donator</c> - the donator whitelist.
/// </summary>
/// <remarks>
/// Donators are written to a roster file the game reads, exactly like a faction - so the
/// same write guard applies, and adding one is a one-line change that cannot wipe the list.
/// </remarks>
public sealed class DonatorCommand(
    SerializedStore store, RosterService rosters, Access access, Paged paged, ILogger<DonatorCommand> logger) : ISlashCommand
{
    /// <summary>The roster file the game reads for donator perks.</summary>
    private const string RosterFile = "donators.txt";

    public string Name => "donator";

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder Player() =>
            new SlashCommandOptionBuilder()
                .WithName("playerid").WithDescription("Player ID or username")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Manage the donator list")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("add").WithDescription("Add a donator")
                .WithType(ApplicationCommandOptionType.SubCommand).AddOption(Player()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("remove").WithDescription("Remove a donator")
                .WithType(ApplicationCommandOptionType.SubCommand).AddOption(Player()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("list").WithDescription("Show the donator list")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();
    }

    private Dictionary<string, Donator> Load() =>
        store.Read(Datasets.DonatorSuspend, new Dictionary<string, Donator>(StringComparer.OrdinalIgnoreCase));

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sub = command.Data.Options.First();

        if (sub.Name == "list")
        {
            var all = Load().Values.OrderBy(d => d.Player, StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0)
            {
                await Reply(command, Theme.Notice("No donators")).ConfigureAwait(false);
                return;
            }

            // Every page, not just the first. This used to take [0] and silently drop the
            // rest, so a long donator list looked complete when it was not.
            var pages = Theme.Paginate(all.Select(d =>
                $"`{Sanitize.Code(d.Player)}` — added by {d.AddedBy}, {Theme.Relative(d.At)}"));
            await paged.SendAsync(command, $"{Theme.Money} Donators — {all.Count}", pages, ct).ConfigureAwait(false);
            return;
        }

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(sub.Options.First().Value as string ?? "");
        if (player.Length == 0)
        {
            await Reply(command, Theme.Failure("That name has nothing usable in it")).ConfigureAwait(false);
            return;
        }

        var adding = sub.Name == "add";
        var roster = rosters.Read(RosterFile);

        if (roster is null && rosters.Enabled)
        {
            /* Unreadable is NOT empty. Writing an "updated" list built from nothing would
               wipe every donator on the server. */
            await Reply(command, Theme.Failure("Could not read the donator roster",
                "The file is unreadable, so nothing was written - the list is untouched.")).ConfigureAwait(false);
            return;
        }

        if (rosters.Enabled && roster is not null)
        {
            IReadOnlyList<string> next = adding
                ? (roster.Contains(player, StringComparer.OrdinalIgnoreCase) ? roster : [.. roster, player])
                : roster.Where(p => !string.Equals(p, player, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!await rosters.WriteAsync(RosterFile, next, ct: ct).ConfigureAwait(false))
            {
                await Reply(command, Theme.Failure("Roster write refused",
                    "The write guard rejected it - the list is untouched. Check the log.")).ConfigureAwait(false);
                return;
            }
        }

        await store.UpdateAsync(Datasets.DonatorSuspend,
            new Dictionary<string, Donator>(StringComparer.OrdinalIgnoreCase),
            donators =>
            {
                if (adding) donators[player] = new Donator(player, command.User.Username, DateTimeOffset.UtcNow);
                else donators.Remove(player);
                return donators;
            }, ct).ConfigureAwait(false);

        logger.LogInformation("donator {Action} | player=\"{Player}\" | by={By}", sub.Name, player, command.User.Username);

        await Reply(command, Theme.Success(adding ? "Donator added" : "Donator removed",
            $"**{Sanitize.Code(player)}**")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <param name="RestoreTo">The rank to put them back at. Without it a suspension is a demotion.</param>
public sealed record RankSuspension(
    string Player, string Faction, string RestoreTo, string SuspendedBy, DateTimeOffset Until);

/// <summary>
/// <c>/suspendrank</c> - pull a member's rank for a set time.
/// </summary>
/// <remarks>
/// The suspension records WHERE TO PUT THEM BACK. Without that it is just a demotion with
/// extra steps, and a sweep restoring everybody to the default rank would quietly strip
/// every suspended officer of everything they had earned.
/// </remarks>
public sealed class SuspendRankCommand(
    SerializedStore store, RosterService rosters, Access access, ILogger<SuspendRankCommand> logger) : ISlashCommand
{
    public string Name => "suspendrank";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Pull a member's whitelist rank for a set time; it auto-restores")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .AddOption("time", ApplicationCommandOptionType.String, "How long, e.g. 30m, 2h, 1d", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(command.Data.Options.First(o => o.Name == "playerid").Value as string ?? "");
        var raw = command.Data.Options.First(o => o.Name == "time").Value as string ?? "";

        var duration = EasternTime.ParseDuration(raw);
        if (duration is null)
        {
            await Reply(command, Theme.Failure("That duration makes no sense",
                "Use `30m`, `2h` or `1d`. A bare number is minutes.")).ConfigureAwait(false);
            return;
        }

        var membership = await rosters.FindAsync(player, ct).ConfigureAwait(false);
        if (membership is null)
        {
            await Reply(command, Theme.Failure("Not whitelisted", $"**{Sanitize.Code(player)}** is not on any roster.")).ConfigureAwait(false);
            return;
        }

        var until = DateTimeOffset.UtcNow + duration.Value;
        var suspension = new RankSuspension(player, membership.Faction.Name, membership.Rank, command.User.Username, until);

        // Record BEFORE removing them. If the write fails, they keep their rank - the
        // opposite order strips a rank with nothing recording how to give it back.
        await store.UpdateAsync(Datasets.RankSuspensions,
            new Dictionary<string, RankSuspension>(StringComparer.OrdinalIgnoreCase),
            suspensions => { suspensions[player] = suspension; return suspensions; }, ct).ConfigureAwait(false);

        await rosters.LeaveAsync(membership.Faction, player, ct).ConfigureAwait(false);

        logger.LogInformation("suspendrank | player=\"{Player}\" | was={Rank} | until={Until} | by={By}",
            player, membership.Rank, EasternTime.Stamp(until), command.User.Username);

        await Reply(command, Theme.Warning("Rank suspended",
            $"**{Sanitize.Code(player)}** — {membership.Faction.Name} **{membership.Rank}**")
            .AddField("Restores", Theme.Relative(until), true)
            .AddField("By", command.User.Username, true)).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <param name="HighStaff">Discord role granted the high-staff menu tier.</param>
public sealed record MenuRoleMap(ulong? HighStaff = null, ulong? Staff = null)
{
    public static MenuRoleMap Empty { get; } = new();

    /// <summary>
    /// The menu tier a member qualifies for, or null for none.
    /// </summary>
    /// <remarks>
    /// HIGHEST WINS. A member holding both roles gets the high-staff menu - checking in the
    /// other order would silently downgrade every senior member who also kept the ordinary
    /// staff role, which is most of them.
    /// </remarks>
    public string? TierFor(IReadOnlyCollection<ulong> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        if (HighStaff is { } high && roleIds.Contains(high)) return "highstaff";
        if (Staff is { } staff && roleIds.Contains(staff)) return "staff";
        return null;
    }
}

/// <summary><c>/setrconroles</c> - which Discord roles map to which RCON+ menu tier.</summary>
public sealed class SetRconRolesCommand(SerializedStore store, Access access, ILogger<SetRconRolesCommand> logger) : ISlashCommand
{
    public string Name => "setrconroles";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Map Discord roles to RCON menu tiers")
            .AddOption("high_staff_role", ApplicationCommandOptionType.Role, "Gets the high-staff menu", isRequired: false)
            .AddOption("staff_role", ApplicationCommandOptionType.Role, "Gets the staff menu", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        ulong? Role(string name) => (command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as IRole)?.Id;

        var high = Role("high_staff_role");
        var staff = Role("staff_role");
        var changing = high is not null || staff is not null;

        // Only what was supplied, same rule as /setroles: naming one must not clear the other.
        var updated = await store.UpdateAsync(Datasets.MenuRoles, MenuRoleMap.Empty, current => current with
        {
            HighStaff = high ?? current.HighStaff,
            Staff = staff ?? current.Staff,
        }, ct).ConfigureAwait(false);

        if (changing) logger.LogInformation("setrconroles | by={By}", command.User.Username);

        var map = updated.Value;
        await Reply(command, Theme.Notice("RCON menu roles", changing
            ? "Updated. A member gets the menu of their **highest** role below."
            : "Current mapping. Pass a role option to change it.")
            .AddField("High staff", map.HighStaff is { } h ? $"<@&{h}>" : "*not set*", true)
            .AddField("Staff", map.Staff is { } s ? $"<@&{s}>" : "*not set*", true)
            .Brand("Priority: high staff beats staff")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
