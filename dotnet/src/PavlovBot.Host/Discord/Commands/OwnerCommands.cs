using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Plugins;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary><c>/stats</c> - what the process is actually costing.</summary>
public sealed class StatsCommand(
    MetricsRegistry metrics, PluginHost plugins, ServiceControl services, Access access) : ISlashCommand
{
    public string Name => "stats";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder().WithName(Name).WithDescription("Owner - CPU and memory for the bot and each game server").Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        using var process = Process.GetCurrentProcess();
        var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();

        var embed = Theme.Notice("Process stats")
            .AddField("Resident", $"{process.WorkingSet64 / 1048576.0:0.0} MB", true)
            .AddField("Managed heap", $"{GC.GetTotalMemory(false) / 1048576.0:0.0} MB", true)
            .AddField("Threads", process.Threads.Count.ToString(CultureInfo.InvariantCulture), true)
            .AddField("Uptime", uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays}d {uptime.Hours}h" : $"{(int)uptime.TotalHours}h {uptime.Minutes}m", true)
            .AddField("CPU time", $"{process.TotalProcessorTime.TotalMinutes:0.0} min", true)
            .AddField("GC (g0/g1/g2)", $"{GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}", true);

        var stats = metrics.Stats();
        embed.AddField("Metrics", $"{stats.Metrics} metric(s), {stats.Series} series" +
            // A non-zero drop count is a label explosion, which is a memory leak with extra
            // steps - worth surfacing rather than leaving in a counter nobody reads.
            (stats.Dropped > 0 ? $", **{stats.Dropped} dropped** (label explosion)" : ""), true);

        var loaded = plugins.Status();
        if (loaded.Count > 0)
            embed.AddField("Plugins", string.Join("\n", loaded.Select(p => $"`{p.Name}` v{p.Version} — {p.State}")));

        /* THE GAME SERVERS, which are what the box is actually spending itself on. The bot's
           own footprint is a rounding error beside three Pavlov processes, and "the server is
           laggy" is a question about them, not about this process. */
        foreach (var line in await ServerLines(services, ct).ConfigureAwait(false))
            embed.AddField(line.Title, line.Body, inline: true);

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>One field per game server: state, CPU, memory.</summary>
    private static async Task<IReadOnlyList<(string Title, string Body)>> ServerLines(
        ServiceControl services, CancellationToken ct)
    {
        var stats = await services.StatsAsync(ct).ConfigureAwait(false);
        var lines = new List<(string, string)>();

        for (var i = 0; i < stats.Count; i++)
        {
            var s = stats[i];

            /* A DOWN SERVER SAYS SO INSTEAD OF SHOWING ZEROES. "CPU 0%, 0 MB" is what a
               stopped unit reports and it reads as a healthy idle server. */
            if (s.State is not (UnitState.Active or UnitState.Starting))
            {
                lines.Add(($"Server {i + 1}", $"`{s.Unit}`\n{Theme.Bad} {Describe(s.State)}"));
                continue;
            }

            var body = new List<string> { $"`{s.Unit}`" };

            body.Add(s.CpuPercent is { } cpu
                ? $"CPU **{cpu:0.#}%**"
                : s.CpuTotal is { } total ? $"CPU {total.TotalMinutes:0.#} min total" : "CPU unknown");

            if (s.MemoryBytes is { } memory)
            {
                var used = Bytes(memory);
                body.Add(s.MemoryLimitBytes is { } limit
                    ? $"RAM **{used}** / {Bytes(limit)}"
                    : $"RAM **{used}**");
            }

            if (s.MemoryPeakBytes is { } peak) body.Add($"peak {Bytes(peak)}");
            if (s.Tasks is { } tasks) body.Add($"{tasks} tasks");
            if (s.Uptime is { } up) body.Add($"up {Duration(up)}");

            lines.Add(($"Server {i + 1}", string.Join("\n", body)));
        }

        /* Said once, at the end, rather than beside every server. Without it "180%" reads as
           an impossible number instead of "most of two cores". */
        if (lines.Count > 0 && stats.Any(s => s.CpuPercent is not null))
        {
            lines.Add(("CPU scale",
                $"100% = one core\n{Environment.ProcessorCount} core(s) on this box"));
        }

        return lines;
    }

    private static string Describe(UnitState state) => state switch
    {
        UnitState.Inactive => "stopped",
        UnitState.Failed => "failed",
        _ => "systemd could not be asked",
    };

    /// <summary>Bytes as the unit a person would use for that magnitude.</summary>
    private static string Bytes(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824.0:0.0} GB",
        >= 1048576 => $"{bytes / 1048576.0:0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        _ => $"{(int)span.TotalMinutes}m",
    };

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/manual</c> - send a raw RCON command.
/// </summary>
/// <remarks>
/// An escape hatch, and it is worth being honest about what it is: every other command in
/// this bot validates its arguments, and this one lets an owner send the unvalidated
/// version. It exists because Pavlov gains commands faster than this bot does, and an
/// owner locked out of a new one has no recourse.
///
/// So it is constrained rather than open:
///
///   OWNER ONLY, from the environment - not a role somebody can be granted.
///   NEWLINES ARE STRIPPED, because the protocol is line-oriented and one would smuggle a
///   second command past the audit line that records the first.
///   EVERY INVOCATION IS AUDITED with the full text, before it is sent.
///   THE REPLY IS SHOWN VERBATIM, so a mistake is visible rather than interpreted.
/// </remarks>
public sealed class ManualCommand(
    RconRegistry rcon, AuditLog audit, Access access, ILogger<ManualCommand> logger) : ISlashCommand
{
    public string Name => "manual";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server").WithDescription("Which server")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        foreach (var name in rcon.Servers) server.AddChoice(name, name);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Send a raw RCON command")
            .AddOption(server)
            .AddOption("command", ApplicationCommandOptionType.String, "The exact command to send", isRequired: true)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var server = command.Data.Options.First(o => o.Name == "server").Value as string ?? "";

        /* Flattened, not rejected. A newline here would split one command into two, and the
           second would not appear in the audit line recording the first - so the record
           would be a lie about what was sent. */
        var raw = Sanitize.Message(command.Data.Options.First(o => o.Name == "command").Value as string ?? "");

        if (raw.Length == 0)
        {
            await Reply(command, Theme.Failure("Nothing to send")).ConfigureAwait(false);
            return;
        }

        // Audited BEFORE it is sent. A command that crashes the server must still be in the
        // record afterwards.
        await audit.RecordAsync("manual", command.User.Username, server, raw, ct).ConfigureAwait(false);
        logger.LogWarning("MANUAL RCON | server={Server} | by={By} | {Command}", server, command.User.Username, raw);

        string reply;
        try
        {
            reply = await rcon.SendAsync(server, raw, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Reply(command, Theme.Failure("The server refused it", $"```\n{Truncate(ex.Message)}\n```")).ConfigureAwait(false);
            return;
        }

        // Verbatim, in a code block. Interpreting it would hide exactly the surprise this
        // command exists to surface.
        await Reply(command, Theme.Notice($"Sent to {server}",
            $"```\n{Truncate(raw)}\n```\n**Reply**\n```\n{Truncate(reply.Length > 0 ? reply : "(no response)")}\n```")).ConfigureAwait(false);
    }

    private static string Truncate(string text) => text.Length > 1500 ? text[..1500] + "\n…" : text;

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/firewall</c> - manual OS-level blocks through ufw.
/// </summary>
/// <remarks>
/// THIS IS NEVER AUTOMATED, and that is the whole design. A ufw rule blocks an ADDRESS, not
/// an account: on residential CGNAT or a shared household one, blocking an evader can cut
/// off people who have done nothing, and nothing in the bot would know to undo it. So the
/// auto-ban path deliberately never touches it, and this command exists for an owner who
/// has decided to accept that cost themselves.
///
/// The implementation is the careful part:
///
///   THE ADDRESS IS PARSED, not pattern-matched. Only something <see cref="IPAddress"/>
///   accepts is ever passed on.
///   ARGUMENTS GO AS AN ARGV ARRAY, never a shell string - so there is no shell to inject
///   into even if the validation were wrong.
///   ONLY THREE VERBS are possible, and they are compiled in rather than composed.
/// </remarks>
public sealed partial class FirewallCommand(AuditLog audit, Access access, ILogger<FirewallCommand> logger) : ISlashCommand
{
    public string Name => "firewall";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Manual OS firewall (ufw) control. Never applied automatically.")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action").WithDescription("What to do")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("block", "block").AddChoice("unblock", "unblock").AddChoice("status", "status"))
            .AddOption("ip", ApplicationCommandOptionType.String, "The address to block or unblock", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var action = command.Data.Options.First(o => o.Name == "action").Value as string ?? "status";
        var rawIp = (command.Data.Options.FirstOrDefault(o => o.Name == "ip")?.Value as string ?? "").Trim();

        string[] argv;
        if (action == "status")
        {
            argv = ["status", "numbered"];
        }
        else
        {
            // PARSED, not pattern-matched. Only something the framework accepts as an
            // address is ever passed on.
            if (!IPAddress.TryParse(rawIp, out var address))
            {
                await Reply(command, Theme.Failure("Not an IP address",
                    $"`{Sanitize.Code(rawIp)}` could not be parsed. Nothing was run.")).ConfigureAwait(false);
                return;
            }

            var canonical = address.ToString();
            argv = action == "block"
                ? ["deny", "from", canonical]
                : ["delete", "deny", "from", canonical];

            await audit.RecordAsync($"firewall-{action}", command.User.Username, canonical, ct: ct).ConfigureAwait(false);
            logger.LogWarning("FIREWALL {Action} | ip={Ip} | by={By}", action.ToUpperInvariant(), canonical, command.User.Username);
        }

        var (ok, output) = await RunUfwAsync(argv, ct).ConfigureAwait(false);

        if (!ok)
        {
            await Reply(command, Theme.Failure("ufw did not run",
                $"```\n{Truncate(output)}\n```\nThis usually means ufw is not installed, or the bot is not running as root.")).ConfigureAwait(false);
            return;
        }

        var embed = action switch
        {
            "block" => Theme.Warning($"{Theme.Deny} Blocked at the firewall", $"`{Sanitize.Code(rawIp)}` is now denied.")
                .AddField($"{Theme.Warn} Remember", "This blocks an ADDRESS, not an account. On a shared or CGNAT connection it affects everyone behind it, and only `/firewall unblock` undoes it."),
            "unblock" => Theme.Success("Firewall block removed", $"`{Sanitize.Code(rawIp)}` is no longer denied."),
            _ => Theme.Notice("Firewall status", $"```\n{Truncate(output)}\n```"),
        };

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>
    /// Run ufw with an ARGV ARRAY. There is no shell here, so there is nothing to inject
    /// into even if the address validation above were wrong.
    /// </summary>
    private async Task<(bool Ok, string Output)> RunUfwAsync(string[] argv, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo("ufw")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,   // no shell, no interpolation
            };
            foreach (var argument in argv) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return (false, "could not start ufw");

            // Bounded: a ufw that hangs must not hold the interaction open past its token.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var output = (stdout + stderr).Trim();
            return (process.ExitCode == 0, output.Length > 0 ? output : "(no output)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ufw invocation failed");
            return (false, ex.Message);
        }
    }

    private static string Truncate(string text) => text.Length > 1500 ? text[..1500] + "\n…" : text;

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/inspect</c> - everything the bot knows about one player.</summary>
public sealed class InspectCommand(
    IpTrackingService tracking, AuditLog audit, BanService bans, Access access) : ISlashCommand
{
    public string Name => "inspect";

    /// <summary>Ephemeral: this prints addresses and account ids.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Everything the bot knows about a player")
            .AddOption("playerid", ApplicationCommandOptionType.String, "Player ID or username", isRequired: true, isAutocomplete: true)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var player = Sanitize.Id(command.Data.Options.First().Value as string ?? "");
        var account = tracking.AccountByName(player);

        var embed = Theme.Notice($"Inspect — {Sanitize.Code(player)}");

        if (account is null)
        {
            embed.WithDescription("The bot has never recorded this account. They have not connected while it was running.");
        }
        else
        {
            embed.AddField("Account id", $"`{account.Id}`", true)
                 .AddField("First seen", account.FirstSeen is { } f ? Theme.Relative(f) : "unknown", true)
                 .AddField("Last seen", account.LastSeen is { } l ? Theme.Relative(l) : "unknown", true);

            if (account.Names.Count > 1)
                embed.AddField("Also known as", string.Join(", ", account.Names.Skip(1).Select(n => $"`{Sanitize.Code(n)}`")));

            /* Confirmed and guessed addresses are shown SEPARATELY and labelled. A guess
               presented next to a certainty invites somebody to act on it, and acting on a
               join-time guess bans whoever else was connecting at that moment. */
            if (account.ConfirmedIps.Count > 0)
                embed.AddField($"Confirmed addresses ({account.ConfirmedIps.Count})",
                    string.Join("\n", account.ConfirmedIps.Select(ip => $"`{ip}`")));

            if (account.GuessedIps.Count > 0)
                embed.AddField($"{Theme.Warn} Guessed addresses ({account.GuessedIps.Count})",
                    string.Join("\n", account.GuessedIps.Select(ip => $"`{ip}`")) +
                    "\nCorrelated from a join, **not certain** - never act on these alone.");
        }

        var ban = bans.ActiveBans().FirstOrDefault(b => BanRules.SamePlayer(b.PlayerId, player));
        embed.AddField("Ban", ban is null
            ? "Not banned"
            : $"{Sanitize.Code(ban.Reason ?? "no reason")} — {(ban.Permanent ? "permanent" : $"expires {Theme.Relative(ban.Expires!.Value)}")}");

        var history = audit.Against(player).Take(5).ToList();
        if (history.Count > 0)
            embed.AddField("Recent actions", string.Join("\n", history.Select(a =>
                $"{Theme.Relative(a.At)} — `{a.Action}` by {a.Moderator}")));

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
