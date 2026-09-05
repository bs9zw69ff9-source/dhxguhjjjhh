using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The staff guild's membership, so a person's roles follow them out of it.
/// </summary>
/// <remarks>
/// THE PROBLEM THIS SOLVES. Access reads roles off the interaction's user, which is only an
/// <see cref="IGuildUser"/> when the interaction happened inside a guild the bot serves. In a
/// DM - and in any server the bot was carried into as a user-installed app - it is a plain
/// user with no roles at all, so every check below owner answered false. A moderator
/// messaging the bot privately was, to the bot, a member of the public. Owners kept working
/// throughout because they are matched by user id, which made the whole thing look like a
/// quirk of one tier rather than the permission model being absent.
///
/// ONE GUILD, NAMED OR UNAMBIGUOUS. Roles are read from the guild this bot's staff live in,
/// and nowhere else. Reading them from "whichever server we happen to share" would mean a
/// role granted in an unrelated guild carrying moderator powers here, which is a privilege
/// escalation with a friendly face. So: HOME_GUILD_ID, else GUILD_ID, else the single guild
/// the bot is in - and with several guilds and nothing named, it refuses and says so rather
/// than picking.
///
/// FETCHED, NOT CACHED BY THE GATEWAY. Members are only in the socket cache under the
/// privileged GuildMembers intent, which this bot does not ask for, so the lookup is a REST
/// call. It needs no privileged intent and is one request per person per window.
///
/// THE WINDOW IS SHORT ON PURPOSE. This is a permission answer, and a cached one is a role
/// that keeps working after it was taken away. A minute bounds that to something a human
/// would describe as immediate while still collapsing the burst of somebody running four
/// commands in a row. MISSES ARE CACHED TOO: without that, a stranger messaging the bot
/// costs a REST call per command they send.
/// </remarks>
public sealed class HomeGuildMembers(
    DiscordSocketClient client,
    ulong? configuredHome,
    ulong? commandGuild,
    ILogger<HomeGuildMembers> logger)
{
    /// <summary>How long a resolved membership is reused. See the class remarks.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Bounded, so a busy bot cannot accumulate an entry per person seen.</summary>
    private const int MaxEntries = 500;

    private readonly record struct Entry(IGuildUser? Member, DateTimeOffset At);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, Entry> _members = new();

    private bool _warnedAmbiguous;

    /// <summary>
    /// The guild whose roles count, or null when there is no unambiguous answer.
    /// </summary>
    /// <remarks>
    /// Resolved per call rather than at construction because the single-guild fallback is
    /// only knowable once the gateway has connected, and this object is built before that.
    /// </remarks>
    public SocketGuild? Home()
    {
        if (configuredHome is { } named)
        {
            var guild = client.GetGuild(named);
            if (guild is null && !_warnedAmbiguous)
            {
                _warnedAmbiguous = true;
                logger.LogWarning(
                    "HOME_GUILD_ID {Guild} is not a guild this bot is in, so roles cannot be read " +
                    "outside a server. Set it to the guild your staff roles live in", named);
            }
            return guild;
        }

        if (commandGuild is { } fallback && client.GetGuild(fallback) is { } fromCommands) return fromCommands;

        var guilds = client.Guilds;
        if (guilds.Count == 1) return guilds.First();

        if (guilds.Count > 1 && !_warnedAmbiguous)
        {
            _warnedAmbiguous = true;
            logger.LogWarning(
                "This bot is in {Count} guilds and neither HOME_GUILD_ID nor GUILD_ID is set, so it " +
                "cannot tell which server's roles should count in a DM. Staff access outside a server " +
                "is OFF until one is named - owners still work, because they are matched by user id",
                guilds.Count);
        }

        return null;
    }

    /// <summary>The cached membership for this user, without fetching. Null means unknown.</summary>
    /// <remarks>
    /// Deliberately sync and deliberately cache-only: <see cref="Access"/>'s predicates are
    /// synchronous and called from every command, and turning them async to serve one
    /// interaction shape would be a change to every call site in the bot. The fetch happens
    /// once per interaction, before dispatch, in <see cref="PrimeAsync"/>.
    /// </remarks>
    public IGuildUser? Member(ulong userId) =>
        _members.TryGetValue(userId, out var entry) && Fresh(entry) ? entry.Member : null;

    /// <summary>
    /// Resolve this user's home-guild membership, so the synchronous checks can see it.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS. A permission lookup that fails is a person with no roles, which is the
    /// same answer as being unable to reach the guild - and taking the interaction down over
    /// it would turn a degraded check into a broken command.
    /// </remarks>
    public async Task PrimeAsync(IUser? user, CancellationToken ct = default)
    {
        if (user is null || user.IsBot) return;
        if (_members.TryGetValue(user.Id, out var cached) && Fresh(cached)) return;
        if (Home() is not { } guild) return;

        IGuildUser? member = null;
        try
        {
            // AllowDownload: the socket cache holds members only under the privileged
            // GuildMembers intent, so without this the answer is null for nearly everybody.
            // Through IGuild: SocketGuild implements this explicitly, and the socket-typed
            // GetUser is cache-only - which is null for nearly everybody without the intent.
            member = await ((IGuild)guild).GetUserAsync(user.Id, CacheMode.AllowDownload,
                new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;   // The interaction is going away; do not poison the cache with a miss.
        }
        catch (Exception ex)
        {
            /* A MISS IS CACHED, an ERROR IS NOT. Somebody who is genuinely not in the guild
               should cost one lookup a minute; a guild that is briefly unreachable should be
               retried rather than remembered as "not a member" for the window. */
            logger.LogDebug(ex, "Could not read {User}'s membership of {Guild}", user.Id, guild.Id);
            return;
        }

        Remember(user.Id, member);
    }

    private static bool Fresh(Entry entry) => DateTimeOffset.UtcNow - entry.At < Window;

    private void Remember(ulong userId, IGuildUser? member)
    {
        _members[userId] = new Entry(member, DateTimeOffset.UtcNow);
        if (_members.Count <= MaxEntries) return;

        foreach (var stale in _members.Where(e => !Fresh(e.Value)).Select(e => e.Key).ToList())
            _members.TryRemove(stale, out _);
    }

    /// <summary>One line for the startup summary.</summary>
    public string Describe() => configuredHome is { } named
        ? $"HOME_GUILD_ID {named}"
        : commandGuild is { } fallback
            ? $"GUILD_ID {fallback} (HOME_GUILD_ID not set)"
            : "the only guild this bot is in, if there is exactly one (neither HOME_GUILD_ID nor GUILD_ID set)";
}
