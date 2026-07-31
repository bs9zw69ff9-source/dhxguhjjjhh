using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// A multi-page embed with working page buttons.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS: <see cref="Theme.Paginate"/> already split long output into pages
/// correctly, but several commands then took <c>[0]</c> and threw the rest away. A
/// moderator reading a 300-name donator list saw the first 40 and nothing telling them
/// there were more - not a missing feature so much as output that quietly lied about being
/// complete.
///
/// PAGES ARE HELD IN MEMORY, keyed by the message they belong to, because a custom id
/// cannot carry 300 names and re-running the query on every click would show a DIFFERENT
/// list as players join and leave - page 2 of a list that no longer exists.
///
/// That memory is bounded and evicted oldest-first. An unbounded cache keyed by message id
/// is a slow leak in a bot that is expected to run for months, and the failure mode of
/// eviction is mild: the buttons say the list expired and ask for the command again.
/// </remarks>
public sealed class Paged(ILogger<Paged> logger) : IComponentHandler
{
    public const string Id = "page";

    /// <summary>Roughly a day of heavy use, and small - each entry is a list of strings.</summary>
    private const int MaxCached = 200;

    private sealed record Session(string Title, IReadOnlyList<string> Pages, ulong OwnerId, DateTimeOffset At);

    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();

    public string Prefix => Id;

    /// <summary>
    /// Send a paged reply. With one page it is an ordinary embed and no buttons at all.
    /// </summary>
    public async Task SendAsync(SocketSlashCommand command, string title, IReadOnlyList<string> pages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Count <= 1)
        {
            await command.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = Theme.Notice(title, pages.Count == 1 ? pages[0] : "*Nothing to show.*").Brand().Build();
                m.AllowedMentions = AllowedMentions.None;
            }).ConfigureAwait(false);
            return;
        }

        var message = await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = Render(title, pages, 0);
            m.Components = Buttons(0, pages.Count);
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);

        Remember(message.Id, new Session(title, pages, command.User.Id, DateTimeOffset.UtcNow));
        _ = ct;
    }

    private void Remember(ulong messageId, Session session)
    {
        _sessions[messageId] = session;
        if (_sessions.Count <= MaxCached) return;

        // Oldest first. Cheap at this size, and only ever runs when over the ceiling.
        foreach (var stale in _sessions.OrderBy(e => e.Value.At).Take(_sessions.Count - MaxCached).ToList())
            _sessions.TryRemove(stale.Key, out _);
    }

    private static Embed Render(string title, IReadOnlyList<string> pages, int index) =>
        Theme.Notice($"{title} — page {index + 1}/{pages.Count}", pages[index]).Brand().Build();

    private static MessageComponent Buttons(int index, int count) =>
        new ComponentBuilder()
            .WithButton("First", ComponentId.Encode(Id, "first"), ButtonStyle.Secondary, disabled: index == 0)
            .WithButton("Prev", ComponentId.Encode(Id, "prev"), ButtonStyle.Primary, disabled: index == 0)
            .WithButton("Next", ComponentId.Encode(Id, "next"), ButtonStyle.Primary, disabled: index >= count - 1)
            .WithButton("Last", ComponentId.Encode(Id, "last"), ButtonStyle.Secondary, disabled: index >= count - 1)
            .Build();

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);
        _ = ct;

        if (interaction is not SocketMessageComponent component) return;

        if (!_sessions.TryGetValue(component.Message.Id, out var session))
        {
            /* Evicted, or posted by a previous run of the bot. Say so rather than failing
               silently - a button that does nothing reads as the bot being broken. */
            await component.RespondAsync("That list has expired. Run the command again.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        /* Anyone can SEE an ephemeral reply's buttons only if it is theirs, but a paged
           reply in a channel is visible to everyone - and letting a bystander turn the
           pages of somebody else's list is a small but real nuisance. */
        if (component.User.Id != session.OwnerId)
        {
            await component.RespondAsync("That list belongs to whoever ran the command.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var current = CurrentPage(component, session.Pages.Count);
        var target = id.Argument(0) switch
        {
            "first" => 0,
            "prev" => current - 1,
            "next" => current + 1,
            "last" => session.Pages.Count - 1,
            _ => current,
        };

        target = Math.Clamp(target, 0, session.Pages.Count - 1);

        try
        {
            await component.UpdateAsync(m =>
            {
                m.Embed = Render(session.Title, session.Pages, target);
                m.Components = Buttons(target, session.Pages.Count);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not turn to page {Page}", target);
        }
    }

    /// <summary>
    /// Which page is on screen, read back from the embed title.
    /// </summary>
    /// <remarks>
    /// The title is the only per-message state that survives a restart, and it is already
    /// there for the reader. Tracking the index in the session instead would desynchronise
    /// the moment two people clicked at once.
    /// </remarks>
    private static int CurrentPage(SocketMessageComponent component, int count)
    {
        var title = component.Message.Embeds.FirstOrDefault()?.Title ?? "";
        var marker = title.LastIndexOf("page ", StringComparison.Ordinal);
        if (marker < 0) return 0;

        var text = title[(marker + 5)..].Split('/')[0];
        return int.TryParse(text, out var page) ? Math.Clamp(page - 1, 0, count - 1) : 0;
    }
}
