using Discord;
using Discord.WebSocket;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon.Protocol;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/serverinfo</c> - live map, mode and roster for every configured server.
/// </summary>
/// <remarks>
/// The first command ported, chosen because it exercises the entire stack end to end:
/// configuration, the persistent RCON connection, read coalescing, reply parsing, and the
/// Discord response path. If this works, the plumbing works.
///
/// Two behaviours carried over deliberately:
///
///   THE COUNT AND THE NAMES COME FROM THE SAME REPLY. The Node bot counted this fetch but
///   drew names from a separately-polled cache, so a player who joined between the two
///   showed in one and not the other - a bug report that took far longer to understand than
///   to fix.
///
///   AN UNREACHABLE SERVER IS REPORTED, NOT OMITTED. Dropping it from the output makes a
///   down server look like a server that does not exist.
/// </remarks>
public sealed class ServerInfoCommand : ISlashCommand
{
    private readonly RconRegistry _rcon;

    public ServerInfoCommand(RconRegistry rcon) => _rcon = rcon;

    public string Name => "serverinfo";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Live server info - map, mode and who is online.")
            .Build();

    /// <summary>What one server reported, or that it could not be reached.</summary>
    private sealed record ServerView(
        string Server,
        bool Online,
        string DisplayName,
        string Map,
        string Mode,
        int PlayerCount,
        int? MaxPlayers,
        IReadOnlyList<string> Roster);

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Fan out. Servers are independent, so one slow host delays the reply by its own
        // latency rather than by the sum of everyone's.
        var views = await Task.WhenAll(_rcon.Servers.Select(s => FetchAsync(s, ct))).ConfigureAwait(false);

        var embeds = views.Select(BuildEmbed).ToList();

        // The network total across every server, as one figure rather than per-server -
        // it is the number anyone actually asks for.
        var online = views.Where(v => v.Online).Sum(v => v.PlayerCount);
        var capacity = views.Sum(v => v.MaxPlayers ?? 0);
        var totalLine = capacity > 0
            ? $"**{online}/{capacity}** online across {views.Length} server(s)."
            : $"**{online}** online across {views.Length} server(s).";

        embeds[0] = embeds[0].ToEmbedBuilder()
            .AddField("All servers", totalLine)
            .Build();

        await command.ModifyOriginalResponseAsync(m => m.Embeds = embeds.ToArray()).ConfigureAwait(false);
    }

    private async Task<ServerView> FetchAsync(string server, CancellationToken ct)
    {
        try
        {
            // Both reads at once: they are independent, and coalescing one layer down means
            // a concurrent sweep asking for the same RefreshList shares this round trip.
            var listTask = _rcon.SendAsync(server, "RefreshList", ct);
            var infoTask = _rcon.SendAsync(server, "ServerInfo", ct);
            await Task.WhenAll(listTask, infoTask).ConfigureAwait(false);

            RconReply.TryParse(await listTask.ConfigureAwait(false), out var listDocument);
            RconReply.TryParse(await infoTask.ConfigureAwait(false), out var infoDocument);

            using (listDocument)
            using (infoDocument)
            {
                var info = infoDocument is null
                    ? new PavlovServerInfo(null, null, null, null)
                    : ServerInfoReply.Read(infoDocument.RootElement);

                var roster = listDocument is null ? [] : RefreshList.Names(listDocument.RootElement);

                /* Successful() is tri-state and stays that way: a server that did not say
                   is treated as up, because the roster it just returned is evidence enough.
                   Only an explicit false counts as down. */
                var online = listDocument is not null &&
                             RconReply.Successful(listDocument.RootElement) != false;

                return new ServerView(
                    server, online,
                    info.ServerName ?? server,
                    info.MapLabel ?? "*Unknown*",
                    info.GameMode ?? "*Unknown*",
                    roster.Count, info.MaxPlayers,
                    roster.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ServerView(server, false, server, "*Unreachable*", "*Unreachable*", 0, null, []);
        }
    }

    private const int MaxNamesShown = 15;

    private static Embed BuildEmbed(ServerView view)
    {
        var builder = new EmbedBuilder()
            .WithTitle(view.DisplayName)
            .WithColor(view.Online ? new Color(0x3F, 0xA2, 0x5F) : new Color(0x9E, 0x3B, 0x2F));

        if (!view.Online)
            return builder.WithDescription($"**{view.DisplayName}** looks offline right now.").Build();

        var capacity = view.MaxPlayers is { } max ? $"{view.PlayerCount}/{max}" : view.PlayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            $"**{view.DisplayName}** is online with **{capacity}** players, playing **{view.Mode}** on **{view.Map}**.",
        };

        if (view.Roster.Count == 0)
        {
            lines.Add("Nobody is on right now.");
        }
        else
        {
            // Truncated with a count rather than a scrollable wall - an embed has a hard
            // 4096-character description limit and a 60-player roster would exceed it.
            var shown = string.Join(" ", view.Roster.Take(MaxNamesShown).Select(n => $"`{Escape(n)}`"));
            var extra = view.Roster.Count > MaxNamesShown ? $" and {view.Roster.Count - MaxNamesShown} more" : "";
            lines.Add($"Online: {shown}{extra}");
        }

        return builder.WithDescription(string.Join("\n", lines)).Build();
    }

    /// <summary>
    /// Neutralise backticks in a player name. Names are attacker-controlled, and one
    /// containing a backtick would otherwise break out of the code span and let the player
    /// inject formatting into a staff-facing message.
    /// </summary>
    private static string Escape(string name) => name.Replace("`", "'", StringComparison.Ordinal);
}
