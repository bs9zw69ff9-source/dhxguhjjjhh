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

    /// <summary>Bookings in flight. Small - one per officer mid-arrest.</summary>
    private const int MaxOpen = 100;

    /// <param name="Minutes">An officer's chosen time. Null means "use the charges".</param>
    private sealed record Booking(string Player, List<string> Codes, double Rate, int Cap,
        ulong OwnerId, DateTimeOffset At, int? Minutes = null);

    private readonly ConcurrentDictionary<ulong, Booking> _open = new();

    public string Prefix => Id;

    /// <summary>Start a booking. The caller has already deferred and checked access.</summary>
    public async Task BeginAsync(SocketSlashCommand command, string player, double rate, int capMinutes)
    {
        ArgumentNullException.ThrowIfNull(command);

        var booking = new Booking(player, [], rate, capMinutes, command.User.Id, DateTimeOffset.UtcNow);

        var message = await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = Render(booking);
            m.Components = Controls(booking, section: null);
        }).ConfigureAwait(false);

        Remember(message.Id, booking);
    }

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

        return Theme.Warning($"Booking: {Sanitize.Code(booking.Player)}",
                "Pick a section, then the charge(s). Add as many as needed, then confirm.\n" +
                "**Set time** overrides the sentence; bail still stacks from the charges.\n\n" +
                $"{sentence}{note}\n{lines}")
            .Brand()
            .Build();
    }

    private static MessageComponent Controls(Booking booking, int? section)
    {
        var sections = new SelectMenuBuilder()
            .WithCustomId(ComponentId.Encode(Id, "section"))
            .WithPlaceholder("Choose a penal code section...");

        foreach (var (number, title, count) in PenalCode.SectionList())
        {
            sections.AddOption(Trim($"{number}. {title}", 100), number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{count} charges");
        }

        var builder = new ComponentBuilder().WithSelectMenu(sections);

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
            .WithButton("Confirm arrest", ComponentId.Encode(Id, "confirm"), ButtonStyle.Danger,
                disabled: booking.Codes.Count == 0)
            .WithButton("Set time", ComponentId.Encode(Id, "time"), ButtonStyle.Primary)
            .WithButton("Cancel", ComponentId.Encode(Id, "cancel"), ButtonStyle.Secondary)
            .Build();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>An officer typed a sentence. Apply it to their open booking.</summary>
    private async Task OnTimeSetAsync(SocketModal modal)
    {
        var messageId = modal.Message?.Id ?? 0;
        if (messageId == 0 || !_open.TryGetValue(messageId, out var booking))
        {
            await modal.RespondAsync("That booking has expired. Run `/arrest` again.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var typed = modal.Data.Components.FirstOrDefault(c => c.CustomId == "minutes")?.Value ?? "";
        if (!int.TryParse(typed.Trim(), out var minutes) || minutes < 0)
        {
            await modal.RespondAsync("Give a whole number of minutes, e.g. `10`.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var updated = booking with { Minutes = minutes };
        _open[messageId] = updated;

        await modal.UpdateAsync(m =>
        {
            m.Embed = Render(updated);
            m.Components = Controls(updated, section: null);
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
            await OnTimeSetAsync(modal).ConfigureAwait(false);
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
                    m.Components = Controls(booking, chosen);
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
                    m.Components = Controls(booking, section: null);
                }).ConfigureAwait(false);
                return;
            }

            case "confirm":
            {
                if (booking.Codes.Count == 0)
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
