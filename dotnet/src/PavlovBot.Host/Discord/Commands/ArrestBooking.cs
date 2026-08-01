using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Penal;
using PavlovBot.Core.Text;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// The interactive arrest booking: pick a section, pick charges, confirm.
/// </summary>
/// <remarks>
/// The Node bot does this with an in-message collector, which holds the booking in a
/// closure for three minutes. There is no collector here - components are dispatched
/// statelessly - so the booking lives in a bounded cache keyed by the message it belongs
/// to, the same shape <see cref="Paged"/> uses.
///
/// WHY NOT PUT THE CHARGES IN THE CUSTOM ID: a booking can run to a dozen codes, and
/// Discord caps a custom id at 100 characters. It would work until the arrest that needed
/// it most.
///
/// The typed-codes form of <c>/arrest</c> stays. This is the one an officer uses at speed;
/// that one is what a script or a keyboard-fluent officer uses, and neither is a
/// replacement for the other.
/// </remarks>
public sealed class ArrestBooking(ILogger<ArrestBooking> logger) : IComponentHandler
{
    public const string Id = "arr";

    /// <summary>The value of the placeholder option shown when the server is empty.</summary>
    private const string NobodyOnline = "-none-";

    /// <summary>Bookings in flight. Small - one per officer mid-arrest.</summary>
    private const int MaxOpen = 100;

    /// <param name="Player">Empty until an officer picks one. Nothing books without it.</param>
    /// <param name="Minutes">An officer's chosen time. Null means "use the charges".</param>
    private sealed record Booking(string Player, List<string> Codes, double Rate, int Cap,
        ulong OwnerId, DateTimeOffset At, int? Minutes = null);

    private readonly ConcurrentDictionary<ulong, Booking> _open = new();

    public string Prefix => Id;

    /// <summary>Start a booking. The caller has already deferred and checked access.</summary>
    /// <param name="online">Who is on the server right now, for the player picker.</param>
    public async Task BeginAsync(SocketSlashCommand command, IReadOnlyList<string> online, double rate, int capMinutes)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(online);

        var booking = new Booking("", [], rate, capMinutes, command.User.Id, DateTimeOffset.UtcNow);
        _roster[command.User.Id] = online;

        var message = await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = Render(booking);
            m.Components = Controls(booking, section: null, online);
        }).ConfigureAwait(false);

        Remember(message.Id, booking);
    }

    /// <summary>
    /// The roster each open booking was started with, so redraws keep the same list.
    /// </summary>
    /// <remarks>
    /// Snapshotted rather than re-read per redraw. A player who disconnects mid-booking
    /// would otherwise vanish from the menu between two clicks, and the officer would be
    /// unable to finish booking the person who just left - which is exactly who they are
    /// most likely to be booking.
    /// </remarks>
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<string>> _roster = new();

    private IReadOnlyList<string> RosterFor(Booking booking) =>
        _roster.TryGetValue(booking.OwnerId, out var names) ? names : [];

    private void Remember(ulong messageId, Booking booking)
    {
        _open[messageId] = booking;
        if (_open.Count <= MaxOpen) return;

        foreach (var stale in _open.OrderBy(e => e.Value.At).Take(_open.Count - MaxOpen).ToList())
            _open.TryRemove(stale.Key, out _);
    }

    // ---- rendering ----

    private static PavlovBot.Core.Penal.Booking Result(Booking booking)
    {
        var result = PenalCode.Book(booking.Codes, booking.Rate, booking.Cap);
        return booking.Minutes is { } chosen ? ArrestCommand.Override(result, chosen, booking.Cap) : result;
    }

    private static Embed Render(Booking booking)
    {
        var result = Result(booking);

        var lines = booking.Codes.Count == 0
            ? "*No charges yet.*"
            : string.Join("\n", result.Charges.Select(c =>
                $"`{c.Code}`  {c.Name} — {c.Class}" +
                (c.BailAt(booking.Rate) is { } b ? $" • ${b:N0}" : "")));

        var sentence = $"**Jail:** {result.SentenceLabel()}" +
                       (booking.Minutes is not null ? " *(set by you)*" : "") +
                       $"  •  **Bail:** {result.BailLabel()}";

        var note = result.Capped
            ? $"\n*Charges total more than the {result.CapMinutes} min cap. Bail is not capped.*"
            : "";

        var who = booking.Player.Length == 0 ? "no player chosen" : Sanitize.Code(booking.Player);

        return Theme.Warning($"Booking: {who}",
                "Pick the player, then a section, then the charge(s). Add as many as needed, then confirm.\n" +
                "**Set time** overrides the sentence; bail still stacks from the charges.\n\n" +
                $"{sentence}{note}\n{lines}")
            .Brand()
            .Build();
    }

    private static MessageComponent Controls(Booking booking, int? section, IReadOnlyList<string> online)
    {
        var builder = new ComponentBuilder();

        /* The player picker. Discord caps a select at 25 options, and a full server can
           exceed that - so the list is truncated and the "Type a name" button below is the
           way to reach anybody it left out, or anybody who has already disconnected. */
        var players = new SelectMenuBuilder()
            .WithCustomId(ComponentId.Encode(Id, "player"))
            .WithPlaceholder(booking.Player.Length == 0
                ? online.Count == 0 ? "Nobody is online - use \"Type a name\"" : "Choose the player..."
                : $"Player: {Trim(booking.Player, 80)}");

        foreach (var name in online.Take(25))
            players.AddOption(Trim(name, 100), Trim(name, 100));

        /* A select with NO options is rejected by Discord outright, taking the whole message
           with it - so an empty server gets one disabled placeholder option instead of a
           booking that silently fails to render. */
        if (players.Options.Count == 0) players.AddOption("(nobody online)", NobodyOnline).WithDisabled(true);

        builder.WithSelectMenu(players);

        var sections = new SelectMenuBuilder()
            .WithCustomId(ComponentId.Encode(Id, "section"))
            .WithPlaceholder("Choose a penal code section...");

        foreach (var (number, title, count) in PenalCode.SectionList())
        {
            sections.AddOption(Trim($"{number}. {title}", 100), number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{count} charges");
        }

        builder.WithSelectMenu(sections);

        if (section is { } chosen && PenalCode.Sections.TryGetValue(chosen, out var charges))
        {
            var picker = new SelectMenuBuilder()
                .WithCustomId(ComponentId.Encode(Id, "charge"))
                .WithPlaceholder($"Add charge(s) from section {chosen}...")
                .WithMinValues(1)
                // Discord caps a select at 25 options; every section is smaller, but the
                // Min() keeps a future section from silently breaking the menu.
                .WithMaxValues(Math.Min(charges.Count, 25));

            foreach (var charge in charges.Take(25))
            {
                picker.AddOption(Trim($"{charge.Code} {charge.Name}", 100), charge.Code,
                    Trim($"{charge.Class} • {charge.JailMinutes} min", 100));
            }

            builder.WithSelectMenu(picker);
        }

        return builder
            // Both halves required: an arrest with no charges punishes nobody for nothing,
            // and an arrest with no player has nobody to punish.
            .WithButton("Confirm arrest", ComponentId.Encode(Id, "confirm"), ButtonStyle.Danger,
                disabled: booking.Codes.Count == 0 || booking.Player.Length == 0)
            .WithButton("Type a name", ComponentId.Encode(Id, "name"), ButtonStyle.Secondary)
            .WithButton("Set time", ComponentId.Encode(Id, "time"), ButtonStyle.Primary)
            .WithButton("Cancel", ComponentId.Encode(Id, "cancel"), ButtonStyle.Secondary)
            .Build();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>A submitted modal: either a chosen sentence or a typed player name.</summary>
    private async Task OnModalAsync(SocketModal modal, ComponentId id)
    {
        var messageId = modal.Message?.Id ?? 0;
        if (messageId == 0 || !_open.TryGetValue(messageId, out var booking))
        {
            await modal.RespondAsync("That booking has expired. Run `/arrest` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        Booking updated;

        if (id.Argument(0) == "setname")
        {
            var typed = Sanitize.Id(modal.Data.Components.FirstOrDefault(c => c.CustomId == "player")?.Value ?? "");
            if (typed.Length == 0)
            {
                /* Sanitize.Id strips what a player name cannot contain, so an empty result
                   means nothing usable was typed - booking it would create an arrest record
                   against a name that matches nobody. */
                await modal.RespondAsync("That name has nothing usable in it.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            updated = booking with { Player = typed };
        }
        else
        {
            var typed = modal.Data.Components.FirstOrDefault(c => c.CustomId == "minutes")?.Value ?? "";
            if (!int.TryParse(typed.Trim(), out var minutes) || minutes < 0)
            {
                await modal.RespondAsync("Give a whole number of minutes, e.g. `10`.", ephemeral: true).ConfigureAwait(false);
                return;
            }

            updated = booking with { Minutes = minutes };
        }

        _open[messageId] = updated;

        await modal.UpdateAsync(m =>
        {
            m.Embed = Render(updated);
            m.Components = Controls(updated, section: null, RosterFor(updated));
        }).ConfigureAwait(false);
    }

    // ---- dispatch ----

    /// <summary>Raised when an officer confirms. The command owns what a booking DOES.</summary>
    public event Func<ArrestConfirmed, Task>? Confirmed;

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        /* The modal submit arrives as a SocketModal and carries the message it came from,
           so it is handled BEFORE the component-only path below - which would otherwise
           drop it silently and leave the officer's typed sentence going nowhere. */
        if (interaction is SocketModal modal)
        {
            await OnModalAsync(modal, id).ConfigureAwait(false);
            return;
        }

        if (interaction is not SocketMessageComponent component) return;

        if (!_open.TryGetValue(component.Message.Id, out var booking))
        {
            await component.RespondAsync("That booking has expired. Run `/arrest` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        /* Only the officer who opened it. An arrest is somebody's punishment, and a
           bystander adding charges to an open booking is not a theoretical problem in a
           channel staff share. */
        if (component.User.Id != booking.OwnerId)
        {
            await component.RespondAsync("That booking belongs to whoever ran the command.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        switch (id.Argument(0))
        {
            case "player":
            {
                var picked = component.Data.Values.FirstOrDefault() ?? "";
                if (picked.Length == 0 || picked == NobodyOnline)
                {
                    await component.DeferAsync().ConfigureAwait(false);
                    return;
                }

                var chosen = booking with { Player = picked };
                _open[component.Message.Id] = chosen;

                await component.UpdateAsync(m =>
                {
                    m.Embed = Render(chosen);
                    m.Components = Controls(chosen, section: null, RosterFor(chosen));
                }).ConfigureAwait(false);
                return;
            }

            case "name":
                // For anybody the picker cannot show: past the 25-option limit, or already
                // disconnected. Must not defer - a modal cannot open on an acknowledged
                // interaction.
                await component.RespondWithModalAsync(new ModalBuilder()
                    .WithTitle("Who is being booked?")
                    .WithCustomId(ComponentId.Encode(Id, "setname"))
                    .AddTextInput("Player name or ID", "player", TextInputStyle.Short,
                        placeholder: "exactly as it appears in game", required: true, maxLength: 64)
                    .Build()).ConfigureAwait(false);
                return;

            case "time":
                /* A modal, not a select: sentences are arbitrary numbers and a menu of
                   twenty-five of them is worse than a text box. Must not defer - a modal
                   cannot open on an acknowledged interaction. */
                await component.RespondWithModalAsync(new ModalBuilder()
                    .WithTitle("Set the sentence")
                    .WithCustomId(ComponentId.Encode(Id, "settime"))
                    .AddTextInput(
                        booking.Cap > 0 ? $"Minutes (max {booking.Cap})" : "Minutes",
                        "minutes", TextInputStyle.Short, placeholder: "e.g. 10",
                        required: true, maxLength: 4)
                    .Build()).ConfigureAwait(false);
                return;

            case "cancel":
                _open.TryRemove(component.Message.Id, out _);
                await component.UpdateAsync(m =>
                {
                    m.Embed = Theme.Notice("Arrest cancelled", "No booking was recorded.").Brand().Build();
                    m.Components = new ComponentBuilder().Build();
                }).ConfigureAwait(false);
                return;

            case "section":
            {
                var chosen = int.TryParse(component.Data.Values.FirstOrDefault(), out var s) ? s : (int?)null;
                await component.UpdateAsync(m =>
                {
                    m.Embed = Render(booking);
                    m.Components = Controls(booking, chosen, RosterFor(booking));
                }).ConfigureAwait(false);
                return;
            }

            case "charge":
            {
                // Deduplicated: picking the same charge twice must not jail somebody twice
                // for it, and the select does not remember what is already on the booking.
                foreach (var code in component.Data.Values)
                    if (PenalCode.Get(code) is not null && !booking.Codes.Contains(code, StringComparer.Ordinal))
                        booking.Codes.Add(code);

                await component.UpdateAsync(m =>
                {
                    m.Embed = Render(booking);
                    m.Components = Controls(booking, section: null, RosterFor(booking));
                }).ConfigureAwait(false);
                return;
            }

            case "confirm":
            {
                if (booking.Codes.Count == 0 || booking.Player.Length == 0)
                {
                    await component.DeferAsync().ConfigureAwait(false);
                    return;
                }

                // Removed BEFORE the work, so a double-click cannot book twice.
                _open.TryRemove(component.Message.Id, out _);
                await component.DeferAsync().ConfigureAwait(false);

                var result = Result(booking);
                try
                {
                    if (Confirmed is { } handler)
                    {
                        await handler(new ArrestConfirmed(booking.Player, booking.Codes, result,
                            component.User.Username, component)).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Booking {Player} failed to record", booking.Player);
                    await component.ModifyOriginalResponseAsync(m =>
                    {
                        m.Embed = Theme.Failure("Arrest not recorded", "That failed. The error has been logged.").Brand().Build();
                        m.Components = new ComponentBuilder().Build();
                    }).ConfigureAwait(false);
                }
                return;
            }

            default:
                await component.DeferAsync().ConfigureAwait(false);
                return;
        }
    }
}

/// <param name="Interaction">So the command can replace the booking message with its summary.</param>
public sealed record ArrestConfirmed(
    string Player,
    IReadOnlyList<string> Codes,
    Booking Result,
    string Officer,
    SocketMessageComponent Interaction);
