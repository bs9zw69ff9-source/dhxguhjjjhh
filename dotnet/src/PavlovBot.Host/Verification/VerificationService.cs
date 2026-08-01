using System.Security.Cryptography;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Text;
using PavlovBot.Core.Verification;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Verification;

/// <param name="Name">The Pavlov name claimed.</param>
/// <param name="Ips">Confirmed addresses at the moment the request was made.</param>
public sealed record PendingVerification(string DiscordId, string Name, IReadOnlyList<string> Ips, DateTimeOffset At);

/// <summary>
/// Member verification: a button, a name, and a staff decision.
/// </summary>
/// <remarks>
/// The flow, and why each step is where it is:
///
///   A BUTTON IN A PUBLIC CHANNEL opens a modal. A slash command would work, but the point
///   of this channel is that it is the ONLY thing an unverified member can see, so the
///   action has to be visible without knowing a command exists.
///
///   THE MODAL ASKS FOR THE EXACT PAVLOV NAME. That name is matched against the address
///   registry the log tailer builds, which is what ties a Discord account to a person
///   rather than to a claim.
///
///   STAFF DECIDE. The bot never auto-approves. The alt check has false positives -
///   housemates and shared NAT look identical to an alt - so the machine's job is to
///   surface the evidence and a human's is to judge it.
///
/// PENDING REQUESTS ARE KEYED BY A SHORT TOKEN rather than carrying the name in the button.
/// Discord caps a custom id at 100 characters and a long Pavlov name would silently blow
/// that budget, which routes the click nowhere. The token is stored, the name is not.
/// </remarks>
public sealed class VerificationService(
    SerializedStore store,
    IpTrackingService tracking,
    FeatureOptions features,
    FeedWebhooks feeds,
    AuditLog audit,
    ILogger<VerificationService> logger) : IComponentHandler
{
    public const string Id = "verify";

    /// <summary>Requests older than this are swept. A decision nobody made in a week is not coming.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(7);

    public string Prefix => Id;

    public bool Enabled => features.VerifyChannel is not null && features.VerifyStaffChannel is not null;

    // ---- storage ----

    private Dictionary<string, VerificationRecord> Verified() =>
        store.Read(Datasets.Verifications, new Dictionary<string, VerificationRecord>(StringComparer.Ordinal));

    private Dictionary<string, PendingVerification> Pending() =>
        store.Read(Datasets.VerifyState, new Dictionary<string, PendingVerification>(StringComparer.Ordinal));

    /// <summary>The panel embed. Posted and kept current by <see cref="AutoPost"/>.</summary>
    public Embed BuildPanel() =>
        Theme.Success("Verify to unlock the server",
                "Press **Verify** and enter your **exact** Pavlov in-game name.\n\n" +
                "A staff member reviews each request. Once approved you get access to the rest of the server.\n\n" +
                "One account per person — alts cannot be verified.")
            .Brand()
            .Build();

    public static MessageComponent PanelButton() =>
        new ComponentBuilder()
            .WithButton("Verify", ComponentId.Encode(Id, "start"), ButtonStyle.Success)
            .Build();

    // ---- dispatch ----

    public async Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(id);

        switch (id.Argument(0))
        {
            case "start" when interaction is SocketMessageComponent:
                await OpenModalAsync(interaction).ConfigureAwait(false);
                return;

            case "submit" when interaction is SocketModal modal:
                await OnSubmittedAsync(modal, ct).ConfigureAwait(false);
                return;

            case "ok" or "no" when interaction is SocketMessageComponent decision:
                await OnDecidedAsync(decision, id.Argument(0) == "ok", id.Argument(1), ct).ConfigureAwait(false);
                return;

            default:
                await interaction.RespondAsync("That control is no longer supported.", ephemeral: true).ConfigureAwait(false);
                return;
        }
    }

    // A modal cannot be shown on an already-acknowledged interaction, so this must not defer.
    private static Task OpenModalAsync(SocketInteraction interaction) =>
        interaction.RespondWithModalAsync(new ModalBuilder()
            .WithTitle("Verify")
            .WithCustomId(ComponentId.Encode(Id, "submit"))
            .AddTextInput("Your exact Pavlov in-game name", "name", TextInputStyle.Short,
                required: true, maxLength: 64)
            .Build());

    private async Task OnSubmittedAsync(SocketModal modal, CancellationToken ct)
    {
        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);

        var name = Sanitize.Message(NameField(modal)).Trim();
        if (name.Length == 0)
        {
            await Followup(modal, Theme.Failure("No name given", "Enter your exact in-game name.")).ConfigureAwait(false);
            return;
        }

        var discordId = modal.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /* Confirmed addresses only. A guessed one is a correlation with a nearby log line,
           and verifying somebody as the owner of an address on that basis is how one person
           gets locked out because another connected a second earlier. */
        var account = tracking.AccountByName(name);
        var ips = account?.ConfirmedIps ?? [];

        var verdict = VerificationRules.Check(Verified(), discordId, name, ips);
        if (!verdict.IsAllowed)
        {
            /* Refused to the MEMBER, not silently dropped, and staff are not asked to
               adjudicate it. The alt rule has false positives by design - the resolution is
               a staff member verifying them by hand. */
            await Followup(modal, Theme.Failure("Cannot verify", verdict.Reason ?? "That request conflicts with an existing verification.")
                .AddField("What now", "Ask a staff member to verify you manually.")).ConfigureAwait(false);
            return;
        }

        if (ips.Count == 0)
        {
            /* Not a refusal: a player who has never been seen on the server has no confirmed
               address yet, and that is the normal case for somebody joining Discord first.
               Staff see "no address on record" and decide. */
            logger.LogInformation("Verification request from {User} for {Name} has no confirmed address yet",
                modal.User.Username, name);
        }

        var token = Token();
        await store.UpdateAsync(Datasets.VerifyState,
            new Dictionary<string, PendingVerification>(StringComparer.Ordinal),
            pending =>
            {
                // Sweep on write rather than on a timer: this is the only thing that grows.
                var cutoff = DateTimeOffset.UtcNow - PendingLifetime;
                foreach (var stale in pending.Where(p => p.Value.At < cutoff).Select(p => p.Key).ToList())
                    pending.Remove(stale);

                pending[token] = new PendingVerification(discordId, name, ips.ToList(), DateTimeOffset.UtcNow);
                return pending;
            }, ct).ConfigureAwait(false);

        if (!await PostRequestAsync(modal, token, name, ips, ct).ConfigureAwait(false))
        {
            await Followup(modal, Theme.Failure("Could not send your request",
                "The staff channel is unreachable. Tell a staff member directly.")).ConfigureAwait(false);
            return;
        }

        await Followup(modal, Theme.Success("Request sent",
            $"Staff will review your request to verify as `{Sanitize.Code(name)}`. You will get the role once it is approved."))
            .ConfigureAwait(false);
    }

    private async Task<bool> PostRequestAsync(
        SocketModal modal, string token, string name, IReadOnlyList<string> ips, CancellationToken ct)
    {
        if (features.VerifyStaffChannel is not { } staffChannelId) return false;

        /* Reached through the GUILD the interaction came from rather than a gateway
           reference. A component handler cannot inject DiscordGateway - the gateway resolves
           the handlers, so that dependency is a cycle the container refuses to build. */
        if (Guild(modal) is not { } guild)
        {
            logger.LogWarning("Verification request did not arrive from a guild channel");
            return false;
        }

        if (guild.GetTextChannel(staffChannelId) is not { } channel)
        {
            logger.LogWarning("VERIFY_STAFF_CHANNEL {Channel} is not a channel this bot can see in {Guild}",
                staffChannelId, guild.Name);
            return false;
        }

        var embed = Theme.Notice("Verification request")
            .AddField("Discord", $"{modal.User.Mention} (`{modal.User.Id}`)", true)
            .AddField("Claims to be", $"`{Sanitize.Code(name)}`", true)
            .AddField("Confirmed addresses", ips.Count == 0
                // Said plainly, because it is the single most useful thing on this card:
                // approving it links a Discord account to a name on nothing but their word.
                ? "**none on record** — this name has never been seen on the server"
                : $"{ips.Count} on record", true)
            .Brand($"Requested {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC")
            .Build();

        var buttons = new ComponentBuilder()
            .WithButton("Accept", ComponentId.Encode(Id, "ok", token), ButtonStyle.Success)
            .WithButton("Deny", ComponentId.Encode(Id, "no", token), ButtonStyle.Danger)
            .Build();

        try
        {
            await channel.SendMessageAsync(embed: embed, components: buttons,
                allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not post a verification request to {Channel}", staffChannelId);
            return false;
        }
        finally { _ = ct; }
    }

    private async Task OnDecidedAsync(SocketMessageComponent component, bool approved, string? token, CancellationToken ct)
    {
        await component.DeferAsync().ConfigureAwait(false);

        if (token is null || !Pending().TryGetValue(token, out var request))
        {
            /* Already decided, or swept. Saying so beats a dead button - two staff clicking
               at once is the normal way this happens. */
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = Theme.Warning("Already handled", "This request has already been decided or has expired.").Brand().Build();
                m.Components = new ComponentBuilder().Build();
            }).ConfigureAwait(false);
            return;
        }

        // Removed FIRST. Two staff clicking at once would otherwise both grant the role and
        // write two audit entries for one request.
        await store.UpdateAsync(Datasets.VerifyState,
            new Dictionary<string, PendingVerification>(StringComparer.Ordinal),
            pending => { pending.Remove(token); return pending; }, ct).ConfigureAwait(false);

        var decidedBy = component.User.Username;

        if (!approved)
        {
            await audit.RecordAsync("verify-deny", decidedBy, request.Name, "denied", ct).ConfigureAwait(false);
            await Close(component, Theme.Failure("Denied", $"`{Sanitize.Code(request.Name)}` was not verified.")
                .AddField("By", decidedBy, true)).ConfigureAwait(false);
            return;
        }

        /* Re-checked at DECISION time, not just at request time. Somebody else may have
           verified that name in between, and the request card carries no lock. */
        var verdict = VerificationRules.Check(Verified(), request.DiscordId, request.Name, request.Ips);
        if (!verdict.IsAllowed)
        {
            await Close(component, Theme.Failure("No longer possible",
                verdict.Reason ?? "That name or address is now verified to somebody else.")).ConfigureAwait(false);
            return;
        }

        await store.UpdateAsync(Datasets.Verifications,
            new Dictionary<string, VerificationRecord>(StringComparer.Ordinal),
            verified =>
            {
                verified[request.DiscordId] = new VerificationRecord(request.Name, request.Ips);
                return verified;
            }, ct).ConfigureAwait(false);

        var roleNote = await GrantRoleAsync(component, request.DiscordId).ConfigureAwait(false);

        await audit.RecordAsync("verify-accept", decidedBy, request.Name, "approved", ct).ConfigureAwait(false);

        /* The Discord-to-address link goes to the CONNECT feed, which is the private
           address-bearing one. It must never reach the public join log. */
        if (request.Ips.Count > 0)
        {
            await feeds.PostAsync(FeedWebhooks.Connect,
                $"[VERIFIED] {Sanitize.Message(request.Name)}  |  discord={request.DiscordId}  |  " +
                $"ips={string.Join(",", request.Ips)}  |  by {Sanitize.Message(decidedBy)}", ct).ConfigureAwait(false);
        }

        await Close(component, Theme.Success("Verified", $"`{Sanitize.Code(request.Name)}` is now verified.")
            .AddField("By", decidedBy, true)
            .AddField("Role", roleNote, true)).ConfigureAwait(false);
    }

    private async Task<string> GrantRoleAsync(SocketMessageComponent component, string discordId)
    {
        if (features.VerifiedRole is not { } roleId) return "none configured";
        if (Guild(component) is not { } guild) return "not in a guild";

        if (!ulong.TryParse(discordId, out var userId)) return "bad user id";

        try
        {
            var member = guild.GetUser(userId);
            if (member is null) return "member has left";

            var role = guild.GetRole(roleId);
            if (role is null) return $"role {roleId} not found";

            await member.AddRoleAsync(role).ConfigureAwait(false);
            return role.Name;
        }
        catch (Exception ex)
        {
            /* The verification is RECORDED either way. A missing Manage Roles permission
               must not lose the decision a human just made - staff can add the role by hand
               and the link is already stored. */
            logger.LogWarning(ex, "Verified {User} but could not grant the role", discordId);
            return "FAILED — grant it manually";
        }
    }

    private static Task Close(SocketMessageComponent component, EmbedBuilder embed) =>
        component.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.Components = new ComponentBuilder().Build();   // spent buttons must not be clickable
        });

    private static Task Followup(SocketInteraction interaction, EmbedBuilder embed) =>
        interaction.FollowupAsync(embed: embed.Brand().Build(), ephemeral: true);

    /// <summary>The guild an interaction arrived from, via its channel.</summary>
    private static SocketGuild? Guild(SocketInteraction interaction) =>
        (interaction.Channel as SocketGuildChannel)?.Guild;

    /// <summary>Short, random, and not derived from anything - it only has to be unguessable.</summary>
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>
    /// The typed name, accepting the Node bot's field id as well as ours.
    /// </summary>
    /// <remarks>
    /// A modal opened by the Node bot's panel just before the cutover submits with
    /// <c>verify_name</c>. Reading only our own id would return empty and answer "no name
    /// given" to somebody who typed one.
    /// </remarks>
    private static string NameField(SocketModal modal) =>
        modal.Data.Components.FirstOrDefault(c => c.CustomId is "name" or "verify_name")?.Value ?? "";

}
