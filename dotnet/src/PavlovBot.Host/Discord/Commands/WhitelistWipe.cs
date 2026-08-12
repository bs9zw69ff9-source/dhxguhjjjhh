using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Text;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// The confirm step behind <c>/whitelist wipe</c>.
/// </summary>
/// <remarks>
/// STATELESS, unlike <see cref="Paged"/> and <see cref="ArrestBooking"/>, which hold their
/// session in a bounded cache keyed by message. There is nothing here worth remembering: the
/// faction and the owner fit in the custom id, and everything else is re-read at the moment
/// the button is pressed.
///
/// That is not just simplicity. A cached wipe would survive in memory while the roster
/// changed underneath it, so the count shown at the prompt and the file written on the press
/// could describe different rosters. Re-reading means the write acts on what is there now.
///
/// It also means a wipe offered before a restart still works afterwards, which a cache would
/// not - and "that prompt expired, run it again" on a destructive action trains people to
/// press the dangerous button twice.
/// </remarks>
public sealed class WhitelistWipe(
    RosterService rosters,
    FactionMembers members,
    Access access,
    AuditLog audit,
    ILogger<WhitelistWipe> logger) : IComponentHandler
{
    public const string Id = "wlwipe";

    private const string Confirm = "go";
    private const string Cancel = "no";

    public string Prefix => Id;

    /// <summary>The confirm and cancel buttons for one faction, pressable by one person.</summary>
    /// <remarks>
    /// The owner's id travels in the custom id rather than being looked up from the message,
    /// because the bot is the message's author - there is nothing on it that says who ran the
    /// command.
    /// </remarks>
    public static MessageComponent Controls(string faction, ulong owner)
    {
        var who = owner.ToString(CultureInfo.InvariantCulture);

        return new ComponentBuilder()
            .WithButton("Wipe the roster", ComponentId.Encode(Id, Confirm, faction, who), ButtonStyle.Danger)
            .WithButton("Cancel", ComponentId.Encode(Id, Cancel, faction, who), ButtonStyle.Secondary)
            .Build();
    }

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        if (interaction is not SocketMessageComponent component) return;

        var action = id.Argument(0);
        var faction = FactionRegistry.Get(id.Argument(1));
        var owner = ulong.TryParse(id.Argument(2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        /* THE PROMPT IS VISIBLE TO THE CHANNEL, so the button is too. Restricting it to the
           person who ran the command is what stops a bystander finishing somebody else's
           half-considered wipe. */
        if (component.User.Id != owner)
        {
            await component.RespondAsync("That confirmation belongs to whoever ran the command.", ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        /* RE-CHECKED ON THE PRESS. The check at the prompt proves who could ASK; this proves
           who may ACT, and between the two an owner can have been demoted. It is also the
           check that holds if a button from an older message is pressed days later. */
        if (!access.Allows(RequiredAccess.Owner, interaction))
        {
            await component.RespondAsync(access.Refusal(RequiredAccess.Owner, interaction), ephemeral: true)
                .ConfigureAwait(false);
            return;
        }

        if (faction is null)
        {
            await Close(component, Theme.Failure("That faction is gone",
                "It was removed from the bot between the prompt and the press. Nothing was changed."))
                .ConfigureAwait(false);
            return;
        }

        if (action != Confirm)
        {
            await Close(component, Theme.Notice("Cancelled",
                $"The **{faction.Name}** whitelist was not touched.")).ConfigureAwait(false);
            return;
        }

        var result = await rosters.WipeAsync(faction, ct).ConfigureAwait(false);

        /* THE INDEX FOLLOWS THE FILES. Cleared even on a partial wipe: the entries name people
           whose rosters have been emptied, and leaving them would make /promotion and
           /whitelist remove act on memberships the files no longer describe. Anybody still
           listed in a file that resisted is re-linked by re-adding them, which is the same
           repair a hand-edited roster needs. */
        var forgotten = await members.ForgetFactionAsync(faction.Name, ct).ConfigureAwait(false);

        await audit.RecordAsync("whitelist wipe", component.User.Username, faction.Name,
            $"{result.Removed} member(s) cleared", ct).ConfigureAwait(false);

        logger.LogWarning("whitelist wipe | faction={Faction} | by={By} | removed={Removed} | records={Records} | {Outcome}",
            faction.Name, component.User.Username, result.Removed, forgotten, result.Outcome);

        await Close(component, Describe(result, faction)).ConfigureAwait(false);
    }

    private static EmbedBuilder Describe(RosterWipe result, FactionDefinition faction) => result.Outcome switch
    {
        MembershipOutcome.Allowed => Theme.Success($"{faction.Name} whitelist wiped",
            $"**{result.Removed}** member(s) cleared from every **{faction.Name}** roster file. " +
            "A pre-write copy of each file is in the backup directory."),

        MembershipOutcome.RosterUnavailable => MembershipReply.Fallback(
            new MembershipDecision(MembershipOutcome.RosterUnavailable)),

        /* NAMED, not counted. "Some files failed" sends somebody to check all of them; the
           list says which rosters still hold members and therefore who can still play. */
        _ => Theme.Warning("Partly wiped",
            $"**{result.Removed}** member(s) were cleared, but these files could not be written and " +
            $"still hold members: {string.Join(", ", result.Failed.Select(f => $"`{Sanitize.Code(f)}`"))}.\n\n" +
            "Check the bot's log for the reason, then run the wipe again."),
    };

    /// <summary>Replace the prompt with its outcome and take the buttons away.</summary>
    /// <remarks>
    /// Removing the components matters more than the text: a spent confirm button that stays
    /// pressable is an invitation to wipe a roster somebody has just rebuilt.
    /// </remarks>
    private static Task Close(SocketMessageComponent component, EmbedBuilder embed) =>
        component.UpdateAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.Components = new ComponentBuilder().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
