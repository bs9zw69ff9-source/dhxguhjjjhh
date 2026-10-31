using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Network;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/packettools</c> - install Wireshark and tshark, check them, and read an attack's pcap.
/// </summary>
/// <remarks>
/// TSHARK IS WIRESHARK ON A SERVER: the same dissectors and statistics without the window, so
/// <c>analyze</c> posts what Wireshark's Statistics menu would show - protocol hierarchy, top
/// endpoints, top conversations - straight into Discord. Nobody needs a computer to look at an
/// attack.
///
/// OWNER ONLY, AND NOTHING FROM DISCORD REACHES A COMMAND LINE. The packages are constants, the
/// pcap is always the newest file the monitor itself wrote, and each action is a fixed argv. It
/// still installs software as root, which is why it is the owner's and why it is audited.
///
/// THE INSTALL IS DETACHED, because <c>apt-get update</c> and a Wireshark install together can
/// outlast the five-minute command budget. It runs under the host's lifetime and edits the
/// command's own reply, which Discord keeps editable for fifteen minutes - longer than both
/// steps' caps put together.
/// </remarks>
public sealed class PacketToolsCommand(
    Access access, AuditLog audit, BotOptions options, IFloodShield shield, IHostApplicationLifetime lifetime,
    ILogger<PacketToolsCommand> logger, PacketLog? packetLog = null)
    : ISlashCommand
{
    /// <summary>
    /// What gets installed: <c>apt install wireshark tshark iftop -y</c>, plus nftables, which the
    /// flood filter is written in (Ubuntu usually has it already).
    /// </summary>
    internal static readonly string[] Packages = ["wireshark", "tshark", "iftop", "nftables"];

    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(9);
    /// <summary>One install at a time: apt holds a lock, and a second run would only fail on it.</summary>
    private static readonly SemaphoreSlim Installing = new(1, 1);

    public string Name => "packettools";

    public bool Ephemeral => true;

    /// <summary>Where the DDoS monitor keeps its pcaps.</summary>
    private string AttackDirectory => Path.Combine(options.DataDirectory, "attacks");


    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Wireshark/tshark for the DDoS monitor")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status").WithDescription("Owner - Whether Wireshark and tshark are installed")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("install").WithDescription("Owner - apt install wireshark tshark (needs root)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("analyze").WithDescription("Owner - Wireshark statistics for the latest attack capture")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("unblock").WithDescription("Owner - Let every address the DDoS filter blocked back in now")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

    /// <summary><c>apt-get update</c>, non-interactive so no prompt can stall it.</summary>
    internal static IReadOnlyList<string> UpdateArgv() => ["DEBIAN_FRONTEND=noninteractive", "apt-get", "update"];

    /// <summary>
    /// <c>apt-get install -y wireshark tshark</c>.
    /// </summary>
    /// <remarks>
    /// NON-INTERACTIVE, because Wireshark's package asks whether ordinary users may capture. With
    /// no terminal the question takes its default, which is no: only root captures, and the bot
    /// is root.
    /// </remarks>
    internal static IReadOnlyList<string> InstallArgv() =>
        ["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", .. Packages];

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        switch (command.Data.Options.FirstOrDefault(o => o.Type == ApplicationCommandOptionType.SubCommand)?.Name)
        {
            case "status":
                await Reply(command, await StatusAsync("Packet tools", ct).ConfigureAwait(false)).ConfigureAwait(false);
                return;
            case "install":
                await InstallAsync(r => Reply(command, r), command.User.Username, ct).ConfigureAwait(false);
                return;
            case "analyze":
                await AnalyzeAsync(r => Reply(command, r), ct).ConfigureAwait(false);
                return;
            case "unblock":
                await UnblockAsync(r => Reply(command, r), command.User.Username, ct).ConfigureAwait(false);
                return;

            default:
                await Reply(command, Theme.Failure("Unknown subcommand")).ConfigureAwait(false);
                return;
        }
    }

    // ---- analyze ----

    /// <summary>The newest pcap the monitor wrote, or null when there is none.</summary>
    internal static string? LatestPcap(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "attack-*.pcap").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal async Task AnalyzeAsync(Func<AntiDdosReply, Task> reply, CancellationToken ct)
    {
        if (LatestPcap(AttackDirectory) is not { } pcap)
        {
            await reply(Theme.Notice("No captures yet",
                $"The DDoS monitor has not saved an attack pcap in `{Sanitize.Code(AttackDirectory)}`. One is written for " +
                "every attack while `ATTACK_CHANNEL` is set.")).ConfigureAwait(false);
            return;
        }

        if (await TsharkCapture.StatisticsAsync(pcap, logger, ct).ConfigureAwait(false) is not { } statistics)
        {
            await reply(Theme.Failure("Could not read the capture",
                $"tshark could not analyse `{Sanitize.Code(Path.GetFileName(pcap))}`. Check `/packettools status`.")).ConfigureAwait(false);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(pcap) + "-statistics.txt";
        var embed = Theme.Notice($"Wireshark statistics · {Path.GetFileName(pcap)}",
            "Protocol hierarchy, top IPv4 endpoints and top conversations, from tshark. The busiest rows of each table " +
            $"are kept ({TsharkCapture.StatisticsRows} per table).");

        await reply(new AntiDdosReply(embed, name, System.Text.Encoding.UTF8.GetBytes(statistics))).ConfigureAwait(false);
    }

    // ---- unblock ----

    internal async Task UnblockAsync(Func<AntiDdosReply, Task> reply, string actor, CancellationToken ct)
    {
        var before = await shield.StatsAsync(ct).ConfigureAwait(false);
        if (await shield.UnblockAllAsync(ct).ConfigureAwait(false) is { } problem)
        {
            await reply(Theme.Failure("Could not unblock", problem)).ConfigureAwait(false);
            return;
        }

        await audit.RecordAsync("ddos-unblock", actor, "all", $"{before?.BlockedSources ?? 0} address(es)", ct)
            .ConfigureAwait(false);
        logger.LogWarning("DDoS filter block list emptied by {User} ({Count} addresses)", actor, before?.BlockedSources ?? 0);
        await reply(Theme.Success("Unblocked",
            $"{before?.BlockedSources ?? 0} address(es) the DDoS filter had blocked can connect again.")).ConfigureAwait(false);
    }

    // ---- install ----

    internal async Task InstallAsync(Func<AntiDdosReply, Task> reply, string actor, CancellationToken ct)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            await reply(Theme.Failure("Cannot install",
                "The bot is not running as root, so it cannot install packages. Run this on the host instead:\n" +
                $"```\nsudo apt update\nsudo apt install {string.Join(' ', Packages)} -y\n```")).ConfigureAwait(false);
            return;
        }

        if (!await Installing.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await reply(Theme.Warning("Already installing", "Another `/packettools install` is still running.")).ConfigureAwait(false);
            return;
        }

        await audit.RecordAsync("packettools-install", actor, string.Join(", ", Packages), "apt-get", ct)
            .ConfigureAwait(false);
        logger.LogWarning("PACKETTOOLS install by {User}: {Packages}", actor, string.Join(", ", Packages));

        await reply(Theme.Notice("Installing Wireshark and tshark",
            $"Running `apt update`, then `apt install {string.Join(' ', Packages)} -y`. Wireshark brings a few hundred MB of " +
            "libraries, so this can take several minutes; this message updates when it is done.")).ConfigureAwait(false);

        // Detached, under the host's lifetime: see the remarks. The semaphore is released there.
        _ = Task.Run(() => RunInstallAsync(reply), CancellationToken.None);
    }

    private async Task RunInstallAsync(Func<AntiDdosReply, Task> reply)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            var update = await ProcessRunner.RunAsync("env", UpdateArgv(), UpdateTimeout, logger, stopping).ConfigureAwait(false);
            if (Problem("apt update", update, UpdateTimeout) is { } updateProblem)
            {
                await reply(Theme.Failure("Install failed", updateProblem)).ConfigureAwait(false);
                return;
            }

            var install = await ProcessRunner.RunAsync("env", InstallArgv(), InstallTimeout, logger, stopping).ConfigureAwait(false);
            if (Problem("apt install", install, InstallTimeout) is { } installProblem)
            {
                await reply(Theme.Failure("Install failed", installProblem)).ConfigureAwait(false);
                return;
            }

            logger.LogWarning("PACKETTOOLS installed {Packages}", string.Join(", ", Packages));
            await reply(await StatusAsync("Wireshark and tshark installed", stopping).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down mid-install; apt finishes or rolls back on its own.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "/packettools install failed");
            try
            {
                await reply(Theme.Failure("Install failed", Sanitize.Code(ex.Message))).ConfigureAwait(false);
            }
            catch (Exception replyEx) when (replyEx is not OperationCanceledException)
            {
                logger.LogWarning(replyEx, "Could not report the /packettools failure");
            }
        }
        finally
        {
            Installing.Release();
        }
    }

    /// <summary>Why a step failed, in words, or null when it worked.</summary>
    internal static string? Problem(string step, ProcessOutcome outcome, TimeSpan timeout)
    {
        if (!outcome.Started) return $"`{step}` could not start - this box has no apt. Install wireshark and tshark with its own package manager.";
        if (outcome.TimedOut) return $"`{step}` did not finish within {timeout.TotalMinutes:0} minutes.";
        if (outcome.ExitCode == 0) return null;

        var tail = outcome.Combined.Length > 900 ? "…" + outcome.Combined[^900..] : outcome.Combined;
        return $"`{step}` exited {outcome.ExitCode}:\n```\n{Sanitize.Code(tail)}\n```";
    }

    // ---- status ----

    internal async Task<EmbedBuilder> StatusAsync(string title, CancellationToken ct)
    {
        var tshark = await FirstLineOfAsync("tshark", ["--version"], ct).ConfigureAwait(false);
        // Wireshark is a desktop program; asking it for --version needs no screen, but the package is the surer answer.
        var wireshark = await FirstLineOfAsync("dpkg-query", ["-W", "-f", "${Version}", "wireshark"], ct).ConfigureAwait(false);
        var iftop = await FirstLineOfAsync("dpkg-query", ["-W", "-f", "${Version}", "iftop"], ct).ConfigureAwait(false);
        var nft = await FirstLineOfAsync("nft", ["--version"], ct).ConfigureAwait(false);
        var blocked = await shield.StatsAsync(ct).ConfigureAwait(false);
        var latest = LatestPcap(AttackDirectory);

        var embed = (tshark is not null ? Theme.Success(title) : Theme.Warning(title))
            .AddField("tshark", tshark is null ? "not installed" : $"`{Sanitize.Code(tshark)}`", true)
            .AddField("Wireshark", wireshark is null ? "not installed" : $"`{Sanitize.Code(wireshark)}`", true)
            .AddField("iftop", iftop is null ? "not installed" : $"`{Sanitize.Code(iftop)}`", true)
            .AddField("DDoS filter (nftables)",
                !shield.Enabled ? "off - set `ATTACK_MITIGATION=on` on ONE bot to block and rate-limit floods"
                : nft is null ? "on, but `nft` is not installed - `/packettools install`"
                : $"on · {Sanitize.Code(nft)} · {blocked?.BlockedSources ?? 0} address(es) blocked now (`/packettools unblock` lets them back)", false)
            .AddField("Capture", Environment.IsPrivilegedProcess
                ? "the bot is root, so the DDoS monitor captures with tshark"
                : "the bot is NOT root, so the DDoS monitor reports totals only", false)
            .AddField("Packet log", PacketLogStatus(), false)
            .AddField("Latest attack capture", latest is null
                ? "none yet"
                : $"`{Sanitize.Code(Path.GetFileName(latest))}` - `/packettools analyze` reads it", false);

        if (tshark is null) embed.AddField("To install", "`/packettools install`", false);
        return embed;
    }

    private string PacketLogStatus() => packetLog switch
    {
        null => "off - on with `ATTACK_CHANNEL`, or set `PACKET_LOG=on`",
        { Running: true } => $"capturing headers on `{Sanitize.Code(packetLog.Interface)}` into `{Sanitize.Code(packetLog.Directory)}` - " +
                             "`/antiddos history` searches the packet and source logs",
        _ => $"NOT capturing: {packetLog.Problem ?? "starting"}",
    };

    /// <summary>The first line a tool prints, or null when it is not installed or fails.</summary>
    private async Task<string?> FirstLineOfAsync(string tool, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync(tool, argv, TimeSpan.FromSeconds(10), logger, ct).ConfigureAwait(false);
        // Stdout first: tshark warns about running as root on stderr, and that is not its version.
        return run.Started && run.ExitCode == 0 ? FirstLine(run.Stdout) ?? FirstLine(run.Stderr) : null;
    }

    internal static string? FirstLine(string? output) =>
        output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) => Reply(command, new AntiDdosReply(embed));

    /// <summary>Show a reply on the command's own message; called again to update it (the install does).</summary>
    private static async Task Reply(SocketSlashCommand command, AntiDdosReply reply)
    {
        using var stream = reply.File is { } bytes ? new MemoryStream(bytes) : null;
        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = reply.Embed.Build();
            if (stream is not null) m.Attachments = new[] { new FileAttachment(stream, reply.FileName) };
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);
    }
}
