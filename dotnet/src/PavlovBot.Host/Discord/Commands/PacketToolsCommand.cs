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
/// <c>/packettools</c> - install Wireshark and tshark, check them, read an attack's pcap, and look
/// an address up in the always-on source log.
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

    /// <summary>The most days <c>sources</c> searches back, matching the default <c>PACKET_LOG_DAYS</c>.</summary>
    internal const int MaxSearchDays = 30;
    private const int DefaultSearchDays = 7;

    /// <summary>The most matching lines attached: about a week of an address present every minute.</summary>
    internal const int MaxAttachedLines = 10_000;

    /// <summary>One install at a time: apt holds a lock, and a second run would only fail on it.</summary>
    private static readonly SemaphoreSlim Installing = new(1, 1);

    public string Name => "packettools";

    public bool Ephemeral => true;

    /// <summary>Where the DDoS monitor keeps its pcaps.</summary>
    private string AttackDirectory => Path.Combine(options.DataDirectory, "attacks");

    /// <summary>Where the packet log keeps its ring and its daily source logs.</summary>
    private string PacketDirectory => packetLog?.Directory ?? Path.Combine(options.DataDirectory, "packets");

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
                .WithName("sources").WithDescription("Owner - Every minute an IPv4 address sent traffic, from the packet log")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("ip", ApplicationCommandOptionType.String, "The IPv4 address to look up", isRequired: true)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("days").WithDescription($"How many days back to search (default {DefaultSearchDays})")
                    .WithType(ApplicationCommandOptionType.Integer).WithMinValue(1).WithMaxValue(MaxSearchDays)))
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
                await InstallAsync(command, ct).ConfigureAwait(false);
                return;
            case "analyze":
                await AnalyzeAsync(command, ct).ConfigureAwait(false);
                return;
            case "unblock":
                await UnblockAsync(command, ct).ConfigureAwait(false);
                return;
            case "sources":
                await SourcesAsync(command, ct).ConfigureAwait(false);
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

    private async Task AnalyzeAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (LatestPcap(AttackDirectory) is not { } pcap)
        {
            await Reply(command, Theme.Notice("No captures yet",
                $"The DDoS monitor has not saved an attack pcap in `{Sanitize.Code(AttackDirectory)}`. One is written for " +
                "every attack while `ATTACK_CHANNEL` is set.")).ConfigureAwait(false);
            return;
        }

        if (await TsharkCapture.StatisticsAsync(pcap, logger, ct).ConfigureAwait(false) is not { } statistics)
        {
            await Reply(command, Theme.Failure("Could not read the capture",
                $"tshark could not analyse `{Sanitize.Code(Path.GetFileName(pcap))}`. Check `/packettools status`.")).ConfigureAwait(false);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(pcap) + "-statistics.txt";
        var embed = Theme.Notice($"Wireshark statistics · {Path.GetFileName(pcap)}",
            "Protocol hierarchy, top IPv4 endpoints and top conversations, from tshark. The busiest rows of each table " +
            $"are kept ({TsharkCapture.StatisticsRows} per table).");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(statistics));
        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Build();
            m.Attachments = new[] { new FileAttachment(stream, name) };
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);
    }

    // ---- sources ----

    /// <summary>
    /// The address in its one canonical form (<c>203.0.113.5</c>), or null when it is not an IPv4
    /// address. The log is written that way, so <c>203.000.113.005</c> or an IPv6 address would
    /// only ever match nothing.
    /// </summary>
    internal static string? CanonicalIpv4(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return System.Net.IPAddress.TryParse(trimmed, out var address) &&
               address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               address.ToString() == trimmed
            ? trimmed
            : null;
    }

    /// <summary>The daily source logs covering the last <paramref name="days"/> days, oldest first.</summary>
    internal static IReadOnlyList<string> SourceLogs(string directory, DateTimeOffset now, int days)
    {
        var logs = new List<string>(days);
        for (var day = days - 1; day >= 0; day--)
        {
            var path = Path.Combine(directory, PavlovBot.Core.Network.SourceLog.FileName(now.AddDays(-day)));
            if (File.Exists(path)) logs.Add(path);
        }
        return logs;
    }

    private async Task SourcesAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var args = command.Data.Options.First().Options;
        if (CanonicalIpv4(args.FirstOrDefault(o => o.Name == "ip")?.Value as string) is not { } address)
        {
            await Reply(command, Theme.Failure("Not an IPv4 address",
                "Give it as four numbers, like `203.0.113.5`. The packet log records IPv4 only.")).ConfigureAwait(false);
            return;
        }

        var days = args.FirstOrDefault(o => o.Name == "days")?.Value is long d ? (int)Math.Clamp(d, 1, MaxSearchDays) : DefaultSearchDays;
        var logs = SourceLogs(PacketDirectory, DateTimeOffset.UtcNow, days);
        if (logs.Count == 0)
        {
            await Reply(command, Theme.Notice("No source logs yet",
                $"Nothing in `{Sanitize.Code(PacketDirectory)}` for the last {days} day(s). The packet log writes one file a day " +
                "while `ATTACK_CHANNEL` is set (or `PACKET_LOG=on`), the bot is root, and tshark is installed. " +
                "`/packettools status` says which is missing.")).ConfigureAwait(false);
            return;
        }

        var matches = new List<string>();
        var truncated = false;
        try
        {
            foreach (var log in logs)
            {
                await foreach (var line in File.ReadLinesAsync(log, ct).ConfigureAwait(false))
                {
                    if (!PavlovBot.Core.Network.SourceLog.IsFor(line, address)) continue;
                    if (matches.Count >= MaxAttachedLines) { truncated = true; break; }
                    matches.Add(line);
                }
                if (truncated) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the source logs in {Directory}", PacketDirectory);
            await Reply(command, Theme.Failure("Could not read the source logs", Sanitize.Code(ex.Message))).ConfigureAwait(false);
            return;
        }

        await audit.RecordAsync("packettools-sources", command.User.Username, address, $"{days} day(s)", ct).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            await Reply(command, Theme.Notice($"Not seen · {address}",
                $"`{address}` sent nothing in the last {days} day(s), or never ranked in a minute's top " +
                $"{PavlovBot.Core.Network.SourceLog.TopPerMinute} senders. Searched {logs.Count} daily log(s).")).ConfigureAwait(false);
            return;
        }

        var totals = PavlovBot.Core.Network.SourceLog.Summarise(matches);
        var embed = Theme.Notice($"Source log · {address}",
                $"Seen in {PavlovBot.Core.Network.AttackReport.Count(totals.Minutes)} minute(s) over the last {days} day(s)" +
                (truncated ? $" - the first {PavlovBot.Core.Network.AttackReport.Count(MaxAttachedLines)} are attached" : "") + ".")
            .AddField("Packets", PavlovBot.Core.Network.AttackReport.Count(totals.Packets), true)
            .AddField("Payload", PavlovBot.Core.Network.AttackReport.Bytes(totals.Bytes), true)
            .AddField("Player", totals.Player ? "yes - a connected player's address" : "no", true)
            .AddField("First seen (UTC)", $"`{totals.First}`", true)
            .AddField("Last seen (UTC)", $"`{totals.Last}`", true);

        var csv = PavlovBot.Core.Network.SourceLog.Header + "\n" + PavlovBot.Core.Network.SourceLog.Join(matches);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Build();
            m.Attachments = new[] { new FileAttachment(stream, $"sources-{address}.csv") };
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);
    }

    // ---- unblock ----

    private async Task UnblockAsync(SocketSlashCommand command, CancellationToken ct)
    {
        var before = await shield.StatsAsync(ct).ConfigureAwait(false);
        if (await shield.UnblockAllAsync(ct).ConfigureAwait(false) is { } problem)
        {
            await Reply(command, Theme.Failure("Could not unblock", problem)).ConfigureAwait(false);
            return;
        }

        await audit.RecordAsync("ddos-unblock", command.User.Username, "all", $"{before?.BlockedSources ?? 0} address(es)", ct)
            .ConfigureAwait(false);
        logger.LogWarning("DDoS filter block list emptied by {User} ({Count} addresses)", command.User.Username, before?.BlockedSources ?? 0);
        await Reply(command, Theme.Success("Unblocked",
            $"{before?.BlockedSources ?? 0} address(es) the DDoS filter had blocked can connect again.")).ConfigureAwait(false);
    }

    // ---- install ----

    private async Task InstallAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            await Reply(command, Theme.Failure("Cannot install",
                "The bot is not running as root, so it cannot install packages. Run this on the host instead:\n" +
                $"```\nsudo apt update\nsudo apt install {string.Join(' ', Packages)} -y\n```")).ConfigureAwait(false);
            return;
        }

        if (!await Installing.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await Reply(command, Theme.Warning("Already installing", "Another `/packettools install` is still running.")).ConfigureAwait(false);
            return;
        }

        await audit.RecordAsync("packettools-install", command.User.Username, string.Join(", ", Packages), "apt-get", ct)
            .ConfigureAwait(false);
        logger.LogWarning("PACKETTOOLS install by {User}: {Packages}", command.User.Username, string.Join(", ", Packages));

        await Reply(command, Theme.Notice("Installing Wireshark and tshark",
            $"Running `apt update`, then `apt install {string.Join(' ', Packages)} -y`. Wireshark brings a few hundred MB of " +
            "libraries, so this can take several minutes; this message updates when it is done.")).ConfigureAwait(false);

        // Detached, under the host's lifetime: see the remarks. The semaphore is released there.
        _ = Task.Run(() => RunInstallAsync(command), CancellationToken.None);
    }

    private async Task RunInstallAsync(SocketSlashCommand command)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            var update = await ProcessRunner.RunAsync("env", UpdateArgv(), UpdateTimeout, logger, stopping).ConfigureAwait(false);
            if (Problem("apt update", update, UpdateTimeout) is { } updateProblem)
            {
                await Reply(command, Theme.Failure("Install failed", updateProblem)).ConfigureAwait(false);
                return;
            }

            var install = await ProcessRunner.RunAsync("env", InstallArgv(), InstallTimeout, logger, stopping).ConfigureAwait(false);
            if (Problem("apt install", install, InstallTimeout) is { } installProblem)
            {
                await Reply(command, Theme.Failure("Install failed", installProblem)).ConfigureAwait(false);
                return;
            }

            logger.LogWarning("PACKETTOOLS installed {Packages}", string.Join(", ", Packages));
            await Reply(command, await StatusAsync("Wireshark and tshark installed", stopping).ConfigureAwait(false)).ConfigureAwait(false);
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
                await Reply(command, Theme.Failure("Install failed", Sanitize.Code(ex.Message))).ConfigureAwait(false);
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

    private async Task<EmbedBuilder> StatusAsync(string title, CancellationToken ct)
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
        { Running: true } => $"recording on `{Sanitize.Code(packetLog.Interface)}` into `{Sanitize.Code(packetLog.Directory)}` - " +
                             "`/packettools sources` looks an address up",
        _ => $"NOT recording: {packetLog.Problem ?? "starting"}",
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

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
