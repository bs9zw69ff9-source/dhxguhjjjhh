using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/packettools</c> - install and check tcpdump and tshark, the tools the DDoS monitor uses.
/// </summary>
/// <remarks>
/// TSHARK IS WIRESHARK FOR A SERVER: the same dissectors and the same capture files, without
/// the window a headless box has no screen for. The Wireshark window goes on somebody's own PC,
/// where it opens the pcaps the monitor saves.
///
/// OWNER ONLY, AND NOTHING FROM DISCORD REACHES THE COMMAND LINE. The package names are
/// constants and the only choice is which of two fixed actions to run, so this is "run this one
/// apt command", not "run a command". It still installs software as root, which is why it is the
/// owner's and why it is audited.
///
/// DETACHED, because <c>apt-get update</c> and a tshark install together can outlast the
/// five-minute command budget. The work runs under the host's lifetime and edits the command's
/// own reply, which Discord keeps editable for fifteen minutes - longer than both steps' caps
/// put together.
/// </remarks>
public sealed class PacketToolsCommand(
    Access access, AuditLog audit, IHostApplicationLifetime lifetime, ILogger<PacketToolsCommand> logger) : ISlashCommand
{
    /// <summary>What gets installed. tcpdump does the live capture; tshark reads the pcaps.</summary>
    internal static readonly string[] Packages = ["tcpdump", "tshark"];

    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(8);

    /// <summary>One install at a time: apt holds a lock, and a second run would only fail on it.</summary>
    private static readonly SemaphoreSlim Installing = new(1, 1);

    public string Name => "packettools";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - tcpdump and tshark (Wireshark) for the DDoS monitor")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status").WithDescription("Owner - Which packet tools are installed")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("install").WithDescription("Owner - Install tcpdump and tshark with apt (needs root)")
                .WithType(ApplicationCommandOptionType.SubCommand))
            .Build();

    /// <summary><c>apt-get update</c>, non-interactive so no prompt can stall it.</summary>
    internal static IReadOnlyList<string> UpdateArgv() => ["DEBIAN_FRONTEND=noninteractive", "apt-get", "update"];

    /// <summary>
    /// <c>apt-get install -y</c> of exactly <see cref="Packages"/>.
    /// </summary>
    /// <remarks>
    /// NON-INTERACTIVE, because tshark's package asks whether ordinary users may capture. With no
    /// terminal the question takes its default, which is no: only root captures, and the bot is
    /// root. <c>--no-install-recommends</c> keeps the desktop Wireshark and its dependencies off
    /// a server that has no use for them.
    /// </remarks>
    internal static IReadOnlyList<string> InstallArgv() =>
        ["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "--no-install-recommends", .. Packages];

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault(o => o.Type == ApplicationCommandOptionType.SubCommand)?.Name;
        if (sub == "status")
        {
            await Reply(command, await StatusAsync("Packet tools", ct).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        if (sub != "install")
        {
            await Reply(command, Theme.Failure("Unknown subcommand")).ConfigureAwait(false);
            return;
        }

        if (!Environment.IsPrivilegedProcess)
        {
            await Reply(command, Theme.Failure("Cannot install",
                "The bot is not running as root, so it cannot install packages. Run this on the host instead:\n" +
                $"```\napt-get install -y --no-install-recommends {string.Join(' ', Packages)}\n```")).ConfigureAwait(false);
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

        await Reply(command, Theme.Notice("Installing packet tools",
            $"Running `apt-get update`, then installing {string.Join(" and ", Packages.Select(p => $"`{p}`"))}. " +
            "This can take a few minutes; this message updates when it is done.")).ConfigureAwait(false);

        // Detached, under the host's lifetime: see the remarks. The semaphore is released there.
        _ = Task.Run(() => InstallAsync(command), CancellationToken.None);
    }

    private async Task InstallAsync(SocketSlashCommand command)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            var update = await ProcessRunner.RunAsync("env", UpdateArgv(), UpdateTimeout, logger, stopping).ConfigureAwait(false);
            if (Problem("apt-get update", update, UpdateTimeout) is { } updateProblem)
            {
                await Reply(command, Theme.Failure("Install failed", updateProblem)).ConfigureAwait(false);
                return;
            }

            var install = await ProcessRunner.RunAsync("env", InstallArgv(), InstallTimeout, logger, stopping).ConfigureAwait(false);
            if (Problem("apt-get install", install, InstallTimeout) is { } installProblem)
            {
                await Reply(command, Theme.Failure("Install failed", installProblem)).ConfigureAwait(false);
                return;
            }

            logger.LogWarning("PACKETTOOLS installed {Packages}", string.Join(", ", Packages));
            await Reply(command, await StatusAsync("Packet tools installed", stopping).ConfigureAwait(false)).ConfigureAwait(false);
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
        if (!outcome.Started) return $"`{step}` could not start - this box has no apt. Install tcpdump and tshark with its own package manager.";
        if (outcome.TimedOut) return $"`{step}` did not finish within {timeout.TotalMinutes:0} minutes.";
        if (outcome.ExitCode == 0) return null;

        var tail = outcome.Combined.Length > 900 ? "…" + outcome.Combined[^900..] : outcome.Combined;
        return $"`{step}` exited {outcome.ExitCode}:\n```\n{Sanitize.Code(tail)}\n```";
    }

    /// <summary>Each tool's version, or that it is missing, plus whether the bot can capture at all.</summary>
    private async Task<EmbedBuilder> StatusAsync(string title, CancellationToken ct)
    {
        var tcpdump = await VersionAsync("tcpdump", ct).ConfigureAwait(false);
        var tshark = await VersionAsync("tshark", ct).ConfigureAwait(false);

        var embed = (tcpdump is not null && tshark is not null ? Theme.Success(title) : Theme.Warning(title))
            .AddField("tcpdump", tcpdump is null ? "not installed" : $"`{Sanitize.Code(tcpdump)}`", true)
            .AddField("tshark (Wireshark)", tshark is null ? "not installed" : $"`{Sanitize.Code(tshark)}`", true)
            .AddField("Capture", Environment.IsPrivilegedProcess
                ? "the bot is root, so the DDoS monitor can capture"
                : "the bot is NOT root, so the DDoS monitor reports totals only", false);

        if (tcpdump is null || tshark is null)
            embed.AddField("To install", "`/packettools install`", false);

        embed.AddField("Opening a pcap",
            "The monitor saves attack pcaps in the bot's `data/attacks`. Copy one to your PC and open it in Wireshark, " +
            "or read it on the server: `tshark -r <file> -q -z io,phs`", false);
        return embed;
    }

    /// <summary>The first line of <c>&lt;tool&gt; --version</c>, or null when it is not installed.</summary>
    private async Task<string?> VersionAsync(string tool, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync(tool, ["--version"], TimeSpan.FromSeconds(10), logger, ct).ConfigureAwait(false);
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
