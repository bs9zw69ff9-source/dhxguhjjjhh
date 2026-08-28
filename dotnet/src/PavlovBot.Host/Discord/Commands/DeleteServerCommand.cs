using System.Globalization;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Provisioning;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/deleteserver</c> - take a server back off the box, install and all.
/// </summary>
/// <remarks>
/// The exact inverse of <see cref="ProvisionServerCommand"/>, and far more dangerous: it stops
/// and disables the service, removes its unit, DELETES ITS INSTALL DIRECTORY, and clears it out
/// of the bot's <c>.env</c>. The install goes with everything in it - that server's own
/// whitelist, bans and logs - and none of it comes back.
///
/// TWO RULES KEEP IT FROM WRECKING THE LAYOUT, and both are refusals rather than warnings:
///
///   ONLY THE HIGHEST-NUMBERED SERVER. Servers are positional as well as indexed - the unit for
///   server N is the Nth entry of PAVLOV_UNITS - so removing one from the middle leaves a gap
///   that every later provision would refuse to extend, and would silently re-map which unit
///   belongs to which server in the meantime.
///
///   NEVER THE LAST ONE. The bot requires RCON_HOST_1 to start at all, so deleting the only
///   server would leave a process that cannot come back up - and this command restarts the bot
///   as its final act, so it would be the thing that took it down.
/// </remarks>
public sealed class DeleteServerCommand(
    IServerProvisioner provisioner,
    Access access,
    AuditLog audit,
    IConfiguration configuration,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<DeleteServerCommand> logger) : ISlashCommand
{
    public string Name => "deleteserver";

    /// <summary>Ephemeral: operator business.</summary>
    public bool Ephemeral => true;

    /// <summary>Resolved on use, not injected - see <see cref="ProvisionServerCommand.Post"/>.</summary>
    private IAutoPostTarget? _post;
    private IAutoPostTarget Post => _post ??= services.GetRequiredService<IAutoPostTarget>();

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Delete a server: its service, its install directory and its slot (needs root)")
            /* THE RANGE STARTS AT 1 EVEN THOUGH SERVER 1 IS NEVER DELETABLE. It was 2, which is
               the same rule enforced a layer too early: Discord rejected the value itself with
               its own generic "Enter a number between 2 and 9", so the reason - that server 1 is
               either not the highest or is the last one standing - never reached the operator.
               Bounds belong here only to keep nonsense out; the explaining is Problem()'s job. */
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("server").WithDescription("Which server number to delete (the highest one only)")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithMinValue(1).WithMaxValue(ProvisionValidation.MaxServers).WithRequired(true))
            .AddOption("confirm", ApplicationCommandOptionType.Boolean,
                "Yes, delete it - the install directory is erased and does not come back", isRequired: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var slot = (int)(command.Data.Options.FirstOrDefault(o => o.Name == "server")?.Value as long? ?? 0);
        var confirmed = command.Data.Options.FirstOrDefault(o => o.Name == "confirm")?.Value as bool? ?? false;

        if (!confirmed)
        {
            await Reply(command, Theme.Notice("Nothing deleted",
                "Set `confirm` to True if you really mean it - this erases the install directory.")).ConfigureAwait(false);
            return;
        }

        var used = UsedRconIndices();
        var units = SplitList(configuration["PAVLOV_UNITS"]);
        var bases = SplitList(configuration["PAVLOV_BASES"]);

        if (Problem(slot, used, units, bases) is { } problem)
        {
            await Reply(command, Theme.Failure($"Cannot delete server {slot}", problem)).ConfigureAwait(false);
            return;
        }

        var unit = units[slot - 1];
        var installDir = bases[slot - 1];

        var who = command.User.Username;
        await audit.RecordAsync("deleteserver", who, unit, $"slot {slot}, dir {installDir}", ct).ConfigureAwait(false);
        logger.LogWarning("DELETESERVER by {User} | slot {Slot} | unit {Unit} | dir {Dir}", who, slot, unit, installDir);

        await Reply(command, Theme.Warning($"Deleting server {slot}",
                $"Removing `{Sanitize.Code(unit)}` and erasing `{Sanitize.Code(installDir)}`. " +
                "Watch the checklist in this channel; the bot restarts itself at the end.")).ConfigureAwait(false);

        var request = new DeleteRequest(
            slot, unit, installDir,
            units.Where((_, i) => i != slot - 1).ToList(),
            bases.Where((_, i) => i != slot - 1).ToList(),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"));

        var channelId = command.Channel.Id;
        var messageId = await Post.SendAsync(channelId, Checklist(slot, unit, []), null, ct).ConfigureAwait(false);

        _ = Task.Run(() => RunAsync(request, unit, channelId, messageId), CancellationToken.None);
    }

    /// <summary>Why this slot may not be deleted, or null when it may.</summary>
    internal static string? Problem(
        int slot, IReadOnlyList<int> usedRconIndices, IReadOnlyList<string> units, IReadOnlyList<string> bases)
    {
        ArgumentNullException.ThrowIfNull(usedRconIndices);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(bases);

        var count = usedRconIndices.Count;

        if (slot > count)
            return $"there is no server {slot} - this box has {count} configured. Nothing was deleted.";

        /* DELETING THE LAST ONE IS ALLOWED, and used to be refused. The bot exits 78 with no RCON
           server configured, so the refusal was protecting against a crash-loop on the restart
           this normally ends with - but that is a reason to SKIP the restart, not a reason to
           trap the operator with a server they cannot remove. The run leaves the process up on
           its existing configuration and says what to do next. */

        if (slot != count)
        {
            return $"only the highest-numbered server can be deleted, which is server {count}. Servers are positional " +
                   $"as well as indexed - the unit for server N is the Nth entry of PAVLOV_UNITS - so removing server " +
                   $"{slot} would leave a gap that silently re-points the remaining units at the wrong servers.\n" +
                   $"Delete from the top down if you want it gone: server {count} first.";
        }

        if (units.Count != count || bases.Count != count)
        {
            return $"PAVLOV_UNITS and PAVLOV_BASES must each list exactly {count} entries before a server can be removed " +
                   $"(they list {units.Count} and {bases.Count}). Set them explicitly first.";
        }

        return null;
    }

    private async Task RunAsync(DeleteRequest request, string unit, ulong channelId, ulong? messageId)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            await provisioner.DeleteAsync(request, async steps =>
            {
                var embed = Checklist(request.Slot, unit, steps);
                if (messageId is { } id) await Post.EditAsync(channelId, id, embed, null, stopping).ConfigureAwait(false);
                else await Post.SendAsync(channelId, embed, null, stopping).ConfigureAwait(false);
            }, stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Deleting server {Slot} threw", request.Slot);
            try
            {
                await Post.SendAsync(channelId,
                    Theme.Failure($"Deleting server {request.Slot} failed", Sanitize.Code(ex.Message)).Build(),
                    null, stopping).ConfigureAwait(false);
            }
            catch (Exception postEx) when (postEx is not OperationCanceledException)
            {
                logger.LogWarning(postEx, "Could not post the deletion failure for server {Slot}", request.Slot);
            }
        }
    }

    private static Embed Checklist(int slot, string unit, IReadOnlyList<ProvisionStep> steps)
    {
        var body = steps.Count == 0
            ? "Starting…"
            : string.Join("\n", steps.Select(s =>
            {
                var line = $"{Glyph(s.Status)} {s.Name}";
                return s.Detail.Length > 0 ? $"{line} — {Sanitize.Code(s.Detail)}" : line;
            }));

        var title = $"Deleting server {slot} ({unit})";
        var failed = steps.Any(s => s.Status == ProvisionStatus.Failed);
        var running = steps.Count == 0 || steps.Any(s => s.Status is ProvisionStatus.Pending or ProvisionStatus.Running);

        var embed = failed && !running ? Theme.Failure(title, body)
            : running ? Theme.Warning(title, body)
            : Theme.Success(title, body);
        return embed.Build();
    }

    private static string Glyph(ProvisionStatus status) => status switch
    {
        ProvisionStatus.Ok => Theme.Ok,
        ProvisionStatus.Failed => Theme.Bad,
        ProvisionStatus.Running => "⏳",
        ProvisionStatus.Skipped => "➖",
        _ => "⬜",
    };

    private IReadOnlyList<int> UsedRconIndices()
    {
        var indices = new List<int>();
        for (var i = 1; i <= ProvisionValidation.MaxServers; i++)
            if (!string.IsNullOrWhiteSpace(configuration[$"RCON_HOST_{i}"])) indices.Add(i);
        return indices;
    }

    private static List<string> SplitList(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
