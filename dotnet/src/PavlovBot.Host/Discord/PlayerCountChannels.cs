using Microsoft.Extensions.Logging;
using PavlovBot.Core.Servers;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;
using PavlovBot.Rcon.Protocol;

namespace PavlovBot.Host.Discord;

/// <summary>Renames a voice channel. An interface so the naming logic tests without a gateway.</summary>
public interface IChannelRenamer
{
    /// <summary>The channel's current name, or null when it is not visible to the bot.</summary>
    Task<string?> NameOfAsync(ulong channelId, CancellationToken ct);

    Task<bool> RenameAsync(ulong channelId, string name, CancellationToken ct);
}

/// <summary>
/// Live player counts as voice channel names.
/// </summary>
/// <remarks>
/// Replaces the player-list board, which posted an embed naming everybody online. A voice
/// channel sits in the sidebar permanently and is readable without opening anything, and it
/// carries no player names - so it is also the version that does not publish who is on the
/// server to anybody who can see the channel list.
///
/// DISCORD RATE-LIMITS CHANNEL RENAMES HARD: two per ten minutes, per channel, and the
/// limit is not shared with anything else. Two consequences shape this class:
///
///   THE TICK IS SLOW ON PURPOSE. Six minutes, which is under the limit with room for a
///   retry. A one-minute tick would spend the budget in two minutes and then silently stop
///   updating, which looks exactly like the feature being broken.
///
///   AN UNCHANGED NAME IS NOT WRITTEN. This is what makes the budget last: an idle server
///   spends nothing, so the whole allowance is available for the minutes when the count is
///   actually moving.
///
/// A STALE ROSTER IS NOT PUBLISHED. An unreachable server and an empty one are
/// indistinguishable from a roster, so a blip would otherwise rename the channel to "0/24"
/// and then back - burning both renames in the window on a number that was never true.
/// </remarks>
public sealed class PlayerCountChannels(
    IChannelRenamer channels,
    RconRegistry rcon,
    MasterServerList master,
    ILogger<PlayerCountChannels> logger)
{
    /// <summary>Older than this and the roster is not evidence of anything.</summary>
    private static readonly TimeSpan RosterFreshness = TimeSpan.FromMinutes(10);

    /// <param name="ServerChannels">One channel id per configured server, in order.</param>
    /// <param name="TotalChannel">The platform-wide total, from the Pavlov master server.</param>
    public sealed record Targets(IReadOnlyList<ulong> ServerChannels, ulong? TotalChannel);

    // ---- the names ----

    /// <summary>"Server 1 3/24", or "Server 1 3" when the capacity is not known.</summary>
    /// <remarks>
    /// PURE. The format is the one that was asked for, to the character, and a channel name
    /// is the kind of thing that gets quietly reformatted by a later edit - so it is pinned
    /// by a test rather than by a comment.
    /// </remarks>
    internal static string ServerName(int number, int players, int? maxPlayers) =>
        maxPlayers is { } max && max > 0
            ? $"Server {number} {players}/{max}"
            : $"Server {number} {players}";

    /// <summary>"Shack total 287".</summary>
    /// <remarks>
    /// NO DENOMINATOR, unlike the per-server names. The only capacity figure available here
    /// is the sum of every listed server's max slots, which moves as servers come and go and
    /// which nobody is trying to fill - "287/7536" reads as a meaningful fraction and is not
    /// one. The count alone is the number people want.
    /// </remarks>
    internal static string TotalName(int players) => $"Shack total {players}";

    /// <summary>Total players across every server the master list knows about.</summary>
    internal static int TotalPlayers(IEnumerable<ServerListing> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return servers.Sum(s => s.Players);
    }

    // ---- the tick ----

    public async Task TickAsync(Targets targets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var servers = rcon.Servers.ToList();
        var report = new List<string>();

        for (var index = 0; index < targets.ServerChannels.Count; index++)
        {
            var channelId = targets.ServerChannels[index];

            if (index >= servers.Count)
            {
                /* More channels configured than servers. Reported rather than ignored: the
                   channel would otherwise sit with a stale count forever and look like the
                   feature had stopped working for that one server. */
                report.Add($"#{index + 1} no such server (only {servers.Count} configured)");
                continue;
            }

            report.Add(await UpdateServerAsync(channelId, servers[index], index + 1, ct).ConfigureAwait(false));
        }

        if (targets.TotalChannel is { } total)
            report.Add(await UpdateTotalAsync(total, ct).ConfigureAwait(false));

        /* ONE LINE PER TICK, at INFORMATION. Every part of this used to be silent unless it
           actually renamed something, so "the channels are not updating" had no answer short
           of reading the code - and the causes (no permission, wrong id, a stale roster, or
           simply nothing to change) are four different fixes. */
        if (report.Count > 0)
            logger.LogInformation("Player-count channels: {Report}", string.Join("  |  ", report));
    }

    private async Task<string> UpdateServerAsync(ulong channelId, string server, int number, CancellationToken ct)
    {
        var roster = rcon.Roster(server);

        if (roster.TakenAt == DateTimeOffset.MinValue ||
            DateTimeOffset.UtcNow - roster.TakenAt > RosterFreshness)
        {
            // Leave whatever is there. A wrong number is worse than a slightly old one, and
            // writing one would spend a rename the real count needs when RCON comes back.
            return $"{server} skipped (no fresh roster)";
        }

        var name = ServerName(number, roster.Players.Count, await MaxPlayersAsync(server, ct).ConfigureAwait(false));
        return $"\"{name}\" {await ApplyAsync(channelId, name, ct).ConfigureAwait(false)}";
    }

    private async Task<string> UpdateTotalAsync(ulong channelId, CancellationToken ct)
    {
        var snapshot = await master.GetAsync(ct: ct).ConfigureAwait(false);

        // Same rule as a stale roster: "0" would be a number that was never true.
        if (snapshot.Servers.Count == 0) return "Shack total skipped (master list empty)";

        var name = TotalName(TotalPlayers(snapshot.Servers));
        return $"\"{name}\" {await ApplyAsync(channelId, name, ct).ConfigureAwait(false)}";
    }

    /// <summary>Rename, but only when the name would actually change. Returns what happened.</summary>
    private async Task<string> ApplyAsync(ulong channelId, string name, CancellationToken ct)
    {
        var current = await channels.NameOfAsync(channelId, ct).ConfigureAwait(false);

        if (current is null)
        {
            logger.LogWarning(
                "Channel {Channel} is not visible to the bot. Either the id is wrong, the bot is not in that " +
                "server, or it cannot see the channel", channelId);
            return $"FAILED - channel {channelId} not visible";
        }

        /* THE RATE LIMIT IS THE REASON THIS CHECK EXISTS. Two renames per ten minutes per
           channel; writing an identical name would spend one for nothing and leave the next
           real change waiting. */
        if (string.Equals(current, name, StringComparison.Ordinal)) return "unchanged";

        return await channels.RenameAsync(channelId, name, ct).ConfigureAwait(false)
            ? "renamed"
            : "FAILED - rename rejected (needs Manage Channels)";
    }

    /// <summary>
    /// A server's capacity, asked over RCON.
    /// </summary>
    /// <remarks>
    /// Not cached: this runs once every six minutes per server, and a stale capacity after
    /// somebody changes MaxPlayers would be wrong for as long as the process lived.
    /// </remarks>
    private async Task<int?> MaxPlayersAsync(string server, CancellationToken ct)
    {
        try
        {
            var reply = await rcon.SendAsync(server, "ServerInfo", ct).ConfigureAwait(false);
            if (!RconReply.TryParse(reply, out var document) || document is null) return null;

            using (document) return ServerInfoReply.Read(document.RootElement).MaxPlayers;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The count is the point; the capacity is decoration. A name without it is
            // still correct, so this must not stop the rename.
            logger.LogDebug(ex, "Could not read MaxPlayers for {Server}", server);
            return null;
        }
    }
}
