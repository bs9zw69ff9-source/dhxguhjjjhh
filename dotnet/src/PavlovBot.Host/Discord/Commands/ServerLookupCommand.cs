using System.Collections.Concurrent;
using System.Globalization;
using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Servers;
using PavlovBot.Core.Text;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/server lookup</c> - browse every live Pavlov server and read one in full.
/// </summary>
/// <remarks>
/// Distinct from <c>/serverinfo</c>, which asks THIS installation's own servers over RCON.
/// This one asks Vankrupt's master server about the whole platform, so it answers questions
/// RCON cannot: who else is running your map, what a player means by "the busy TTT server",
/// and whether your own server is actually visible in the public browser - which is the
/// failure nobody notices, because from inside the server everything looks fine.
/// </remarks>
public sealed class ServerLookupCommand(ServerBrowser browser) : ISlashCommand
{
    public string Name => "server";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Browse the public Pavlov server list")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("lookup").WithDescription("Pick a live server and see everything about it")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("search", ApplicationCommandOptionType.String,
                    "Filter by name, map, mode or address", isRequired: false))
            .Build();

    public Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var search = command.Data.Options.FirstOrDefault()?.Options
            .FirstOrDefault(o => o.Name == "search")?.Value as string;

        return browser.BeginAsync(command, search, ct);
    }
}

/// <summary>
/// The dropdown behind <c>/server lookup</c>, and the card it opens.
/// </summary>
/// <remarks>
/// There are three hundred-odd Pavlov servers and Discord caps a select at 25 options, so
/// the list is PAGED rather than truncated. A browser that silently showed the first 25 of
/// 314 would be worse than none: it would answer "is my server listed?" with a confident no.
///
/// The list is SNAPSHOTTED per session for the same reason <see cref="Paged"/> holds its
/// pages - the underlying list reorders itself as players join and leave, and paging
/// through a list that reorders underneath you shows some servers twice and others never.
/// Refresh takes a new snapshot deliberately.
/// </remarks>
public sealed class ServerBrowser(MasterServerList master, RconRegistry rcon, BotOptions options) : IComponentHandler
{
    public const string Id = "srv";

    /// <summary>Discord's hard cap on select options.</summary>
    private const int PerPage = 25;

    /// <summary>Small - one per person browsing.</summary>
    private const int MaxOpen = 50;

    private sealed record Session(
        IReadOnlyList<ServerListing> All,
        string? Query,
        int Page,
        string? Selected,
        TimeSpan Age,
        bool Stale,
        ulong OwnerId,
        DateTimeOffset At);

    private readonly ConcurrentDictionary<ulong, Session> _open = new();

    public string Prefix => Id;

    // ---- opening ----

    public async Task BeginAsync(SocketSlashCommand command, string? search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var snapshot = await master.GetAsync(ct: ct).ConfigureAwait(false);
        var session = NewSession(snapshot, search, command.User.Id);

        if (session.All.Count == 0)
        {
            await command.ModifyOriginalResponseAsync(m => m.Embed = Empty(snapshot)).ConfigureAwait(false);
            return;
        }

        var message = await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = Render(session);
            m.Components = Controls(session);
        }).ConfigureAwait(false);

        Remember(message.Id, session);
    }

    private Session NewSession(MasterServerList.Snapshot snapshot, string? query, ulong owner) =>
        new(ServerList.Ordered(ServerList.Matching(snapshot.Servers, query)),
            query, Page: 0, Selected: null, snapshot.Age, snapshot.Failed, owner, DateTimeOffset.UtcNow);

    private void Remember(ulong messageId, Session session)
    {
        _open[messageId] = session;
        if (_open.Count <= MaxOpen) return;

        foreach (var stale in _open.OrderBy(e => e.Value.At).Take(_open.Count - MaxOpen).ToList())
            _open.TryRemove(stale.Key, out _);
    }

    private static Embed Empty(MasterServerList.Snapshot snapshot) =>
        Theme.Failure("No servers matched",
                snapshot.Failed
                    ? "The Pavlov master server could not be reached, and nothing was cached yet. Try again shortly."
                    : "Nothing in the public list matched that. Try a shorter search, or drop it entirely.")
            .Brand()
            .Build();

    // ---- rendering ----

    private int PageCount(Session session) =>
        Math.Max(1, (session.All.Count + PerPage - 1) / PerPage);

    private IReadOnlyList<ServerListing> PageOf(Session session) =>
        [.. session.All.Skip(session.Page * PerPage).Take(PerPage)];

    private Embed Render(Session session)
    {
        // A picked server replaces the summary entirely: it is what the person came for.
        if (session.Selected is { } key &&
            session.All.FirstOrDefault(s => s.Key == key) is { } chosen)
        {
            return Card(chosen, session);
        }

        var players = session.All.Sum(s => s.Players);
        var populated = session.All.Count(s => s.Players > 0);

        var lines = new List<string>
        {
            $"**{session.All.Count}** server(s)" +
            (session.Query is { Length: > 0 } q ? $" matching `{Sanitize.Code(q)}`" : "") +
            $", **{populated}** with players on, **{players}** playing in total.",
            "",
            "Pick one from the menu for the full details.",
        };

        var body = string.Join("\n", lines) + "\n\n" + string.Join("\n", PageOf(session).Select(Summary));

        return Theme.Notice($"Pavlov servers — page {session.Page + 1}/{PageCount(session)}", body)
            .Brand(Freshness(session))
            .Build();
    }

    private static string Summary(ServerListing server)
    {
        var count = $"{server.Players}/{server.MaxPlayers}";
        var marks = (server.PasswordProtected ? " 🔒" : "") + (server.Full ? " • FULL" : "");
        return $"`{count,-6}` **{Sanitize.Message(Trim(server.Name, 44))}** — {Sanitize.Message(server.GameModeLabel)}{marks}";
    }

    /// <summary>How old this snapshot is, said plainly rather than implied.</summary>
    private static string Freshness(Session session)
    {
        var age = session.Age.TotalSeconds < 5
            ? "just now"
            : $"{(int)session.Age.TotalSeconds}s ago";

        return session.Stale
            ? $"Master server unreachable — showing the last list, fetched {age}"
            : $"Fetched {age}";
    }

    private Embed Card(ServerListing server, Session session)
    {
        var mine = IsOurs(server);

        var builder = Theme.Notice(Trim(server.Name, 240),
                $"**{server.Players}/{server.MaxPlayers}** playing **{Sanitize.Message(server.GameModeLabel)}** " +
                $"on **{Sanitize.Message(server.MapLabel)}**." +
                (mine ? "\n\n*This is one of your configured servers.*" : ""))
            .AddField("Connect", $"`{server.Ip}:{server.Port}`", inline: true)
            .AddField("Players", server.Full ? $"{server.Players}/{server.MaxPlayers} (full)" : $"{server.Players}/{server.MaxPlayers}", inline: true)
            .AddField("Game mode", Describe(server.GameModeLabel, server.GameMode), inline: true)
            .AddField("Map", Sanitize.Message(server.MapLabel), inline: true)
            .AddField("Map ID", server.MapId.Length > 0 ? $"`{Sanitize.Code(server.MapId)}`" : "*none*", inline: true)
            .AddField("Workshop", server.IsWorkshopMap ? "Custom map" : "Stock map", inline: true)
            .AddField("Version", server.Version.Length > 0 ? server.Version : "*unknown*", inline: true)
            .AddField("Password", server.PasswordProtected ? "🔒 Required" : "Open", inline: true)
            .AddField("Anti-cheat", server.Secured ? "Enforced" : "⚠️ Not enforced", inline: true);

        /* An absolute stamp AND a relative one. "5 minutes ago" is what tells you the
           server has stopped checking in, and the exact time is what you quote to somebody
           else. A listing that has gone quiet is the whole reason to look at this field. */
        builder.AddField("Last seen",
            server.Updated is { } at
                ? $"<t:{at.ToUnixTimeSeconds()}:R> ({PavlovBot.Core.Time.EasternTime.Stamp(at)})"
                : "*unknown*");

        return builder.Brand(Freshness(session)).Build();
    }

    /// <summary>The label, plus the raw mode when it says something different.</summary>
    private static string Describe(string label, string raw) =>
        raw.Length > 0 && !string.Equals(label, raw, StringComparison.OrdinalIgnoreCase)
            ? $"{Sanitize.Message(label)} (`{Sanitize.Code(raw)}`)"
            : Sanitize.Message(label.Length > 0 ? label : "*unknown*");

    /// <summary>
    /// Whether this listing is one of the bot's own servers.
    /// </summary>
    /// <remarks>
    /// Matched on ADDRESS ONLY, never on port: the RCON port and the game port are different
    /// numbers on the same host, so comparing ports would report every one of your own
    /// servers as somebody else's.
    /// </remarks>
    private bool IsOurs(ServerListing server) =>
        options.Servers.Any(s => string.Equals(s.Host, server.Ip, StringComparison.OrdinalIgnoreCase)) ||
        rcon.Servers.Any(name => string.Equals(name, server.Name, StringComparison.OrdinalIgnoreCase));

    private MessageComponent Controls(Session session)
    {
        var page = PageOf(session);

        var picker = new SelectMenuBuilder()
            .WithCustomId(ComponentId.Encode(Id, "pick"))
            .WithPlaceholder(session.Selected is null
                ? $"Choose a server ({session.All.Count} listed)..."
                : Trim($"Showing: {session.Selected}", 150));

        foreach (var server in page)
        {
            /* The VALUE is ip:port, never the name. Names are server-operator controlled,
               can exceed Discord's 100-character option value limit, and are not unique -
               two servers called "Pavlov Server" would collapse into one option. */
            picker.AddOption(
                Trim($"{server.Players}/{server.MaxPlayers} {server.Name}", 100),
                server.Key,
                Trim($"{server.GameModeLabel} • {server.MapLabel}", 100));
        }

        if (picker.Options.Count == 0)
            picker.AddOption("(no servers on this page)", "-none-").WithDisabled(true);

        var last = PageCount(session) - 1;

        return new ComponentBuilder()
            .WithSelectMenu(picker)
            .WithButton("◀", ComponentId.Encode(Id, "prev"), ButtonStyle.Secondary, disabled: session.Page <= 0)
            .WithButton($"{session.Page + 1}/{last + 1}", ComponentId.Encode(Id, "noop"), ButtonStyle.Secondary, disabled: true)
            .WithButton("▶", ComponentId.Encode(Id, "next"), ButtonStyle.Secondary, disabled: session.Page >= last)
            .WithButton("Search", ComponentId.Encode(Id, "find"), ButtonStyle.Primary)
            .WithButton("Refresh", ComponentId.Encode(Id, "reload"), ButtonStyle.Secondary)
            .Build();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    // ---- dispatch ----

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        if (interaction is SocketModal modal)
        {
            await OnSearchAsync(modal, ct).ConfigureAwait(false);
            return;
        }

        if (interaction is not SocketMessageComponent component) return;

        if (!_open.TryGetValue(component.Message.Id, out var session))
        {
            await component.RespondAsync("That server list has expired. Run `/server lookup` again.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        /* Only whoever opened it. Two people paging the same message fight over it, and
           the one who did not click watches the list jump under them. */
        if (component.User.Id != session.OwnerId)
        {
            await component.RespondAsync("That list belongs to whoever ran the command. Run `/server lookup` for your own.",
                ephemeral: true).ConfigureAwait(false);
            return;
        }

        switch (id.Argument(0))
        {
            case "find":
                // Must not defer - a modal cannot open on an acknowledged interaction.
                await component.RespondWithModalAsync(new ModalBuilder()
                    .WithTitle("Search the server list")
                    .WithCustomId(ComponentId.Encode(Id, "search"))
                    .AddTextInput("Name, map, mode or address", "query", TextInputStyle.Short,
                        placeholder: "leave empty to clear the search", required: false, maxLength: 60)
                    .Build()).ConfigureAwait(false);
                return;

            case "reload":
            {
                await component.DeferAsync().ConfigureAwait(false);

                var snapshot = await master.GetAsync(force: true, ct).ConfigureAwait(false);
                var refreshed = NewSession(snapshot, session.Query, session.OwnerId) with { Selected = session.Selected };

                await ApplyAsync(component, refreshed, ct).ConfigureAwait(false);
                return;
            }

            case "prev":
            case "next":
            {
                var delta = id.Argument(0) == "next" ? 1 : -1;
                var page = Math.Clamp(session.Page + delta, 0, PageCount(session) - 1);

                // Turning the page clears the selection: the card belongs to a server that
                // is no longer on screen, and leaving it up hides the list being paged.
                await Update(component, session with { Page = page, Selected = null }).ConfigureAwait(false);
                return;
            }

            case "pick":
            {
                var key = component.Data.Values.FirstOrDefault();
                if (key is null or "-none-")
                {
                    await component.DeferAsync().ConfigureAwait(false);
                    return;
                }

                await Update(component, session with { Selected = key }).ConfigureAwait(false);
                return;
            }

            default:
                await component.DeferAsync().ConfigureAwait(false);
                return;
        }
    }

    private async Task OnSearchAsync(SocketModal modal, CancellationToken ct)
    {
        var messageId = modal.Message?.Id ?? 0;
        if (messageId == 0 || !_open.TryGetValue(messageId, out var session))
        {
            await modal.RespondAsync("That server list has expired. Run `/server lookup` again.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        var typed = modal.Data.Components.FirstOrDefault(c => c.CustomId == "query")?.Value ?? "";

        // Re-filtered from a fresh snapshot rather than from the session's already-filtered
        // list, so a second search WIDENS as well as narrows.
        var snapshot = await master.GetAsync(ct: ct).ConfigureAwait(false);
        var filtered = NewSession(snapshot, string.IsNullOrWhiteSpace(typed) ? null : typed.Trim(), session.OwnerId);

        if (filtered.All.Count == 0)
        {
            await modal.RespondAsync($"Nothing matched `{Sanitize.Code(typed)}`. The list still shows what it did.",
                ephemeral: true).ConfigureAwait(false);
            return;
        }

        _open[messageId] = filtered;

        await modal.UpdateAsync(m =>
        {
            m.Embed = Render(filtered);
            m.Components = Controls(filtered);
        }).ConfigureAwait(false);
    }

    private async Task Update(SocketMessageComponent component, Session session)
    {
        _open[component.Message.Id] = session;

        await component.UpdateAsync(m =>
        {
            m.Embed = Render(session);
            m.Components = Controls(session);
        }).ConfigureAwait(false);
    }

    /// <summary>Redraw after a deferred action, where UpdateAsync is no longer available.</summary>
    private async Task ApplyAsync(SocketMessageComponent component, Session session, CancellationToken ct)
    {
        _open[component.Message.Id] = session;

        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = Render(session);
            m.Components = Controls(session);
        }).ConfigureAwait(false);

        _ = ct;
    }
}
