using System.Globalization;
using System.Security.Cryptography;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Provisioning;
using PavlovBot.Core.Text;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/provisionserver</c> - install a new Pavlov dedicated server and wire it into this bot.
/// </summary>
/// <remarks>
/// The heaviest command in the bot. It runs SteamCMD, writes the server's config, installs and
/// enables a systemd unit, opens the firewall, appends the server to the bot's own <c>.env</c>
/// and then restarts the bot to pick it up. Owner-only, and every value that reaches a
/// privileged command line is either an integer choice bounded by Discord or validated against
/// an allow-list before anything runs (ports, the unit name, the RCON password charset).
///
/// SteamCMD outlives the 5-minute command budget and the 15-minute interaction token, so the
/// work runs as a detached task and its progress is posted as a live checklist to the channel
/// the command was run in - the ephemeral reply only kicks it off (and is the one place the
/// generated RCON password is shown).
/// </remarks>
public sealed class ProvisionServerCommand(
    IServerProvisioner provisioner,
    Access access,
    AuditLog audit,
    BotOptions options,
    FeatureOptions features,
    IConfiguration configuration,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<ProvisionServerCommand> logger) : ISlashCommand
{
    public string Name => "provisionserver";

    /// <summary>
    /// The auto-post target, resolved on first use rather than injected.
    /// </summary>
    /// <remarks>
    /// THE GATEWAY IS RESOLVED ON USE, NOT INJECTED, for the exact reason
    /// <see cref="GatewayChannelRenamer"/> documents: <see cref="DiscordGateway"/> takes every
    /// <see cref="ISlashCommand"/> in its constructor, and <see cref="IAutoPostTarget"/>'s own
    /// registration resolves <see cref="DiscordGateway"/> - so a command that took it as a
    /// constructor parameter would close the cycle DiscordGateway -&gt; every command -&gt; this
    /// command -&gt; IAutoPostTarget -&gt; DiscordGateway. Because IAutoPostTarget is registered
    /// behind a factory delegate, the container cannot see that cycle statically the way it can
    /// for a plain interface mapping, so instead of failing fast with "a circular dependency was
    /// detected" it deadlocks silently the first time <c>--selftest</c> builds the whole graph -
    /// which is exactly what happened before this was changed to resolve lazily here. Nothing
    /// needs the gateway until a command is actually run, long after startup, so resolving it
    /// then breaks the cycle without pretending the dependency is not real.
    /// </remarks>
    private IAutoPostTarget? _post;
    private IAutoPostTarget Post => _post ??= services.GetRequiredService<IAutoPostTarget>();

    /// <summary>Ephemeral: operator business, and it prints the generated RCON password.</summary>
    public bool Ephemeral => true;

    private const int DefaultGamePort = 7777;
    private const int DefaultQueryPort = 8177;
    private const int DefaultRconPort = 9100;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Install a new Pavlov server, configure it and wire it into this bot (needs root)")
            .AddOption("name", ApplicationCommandOptionType.String, "Public server name", isRequired: true)
            .AddOption(Port("gameport", $"Game port (UDP, default {DefaultGamePort})"))
            .AddOption(Port("queryport", $"Query/beacon port (UDP, default {DefaultQueryPort})"))
            .AddOption(Port("rconport", $"RCON port (TCP, default {DefaultRconPort})"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("maxplayers").WithDescription("Max players (default 24)")
                .WithType(ApplicationCommandOptionType.Integer).WithMinValue(1).WithMaxValue(100).WithRequired(false))
            .AddOption("maps", ApplicationCommandOptionType.String, "Rotation as MapId:Mode,MapId:Mode (default datacenter:SND)", isRequired: false)
            .AddOption("unit", ApplicationCommandOptionType.String, "systemd unit name (default pavlovserverN)", isRequired: false)
            .AddOption("installdir", ApplicationCommandOptionType.String, "Install directory (default beside the others)", isRequired: false)
            .AddOption("rconpassword", ApplicationCommandOptionType.String, "RCON password (default: a strong one is generated)", isRequired: false)
            .AddOption("gamepassword", ApplicationCommandOptionType.String, "Optional join password (default: open)", isRequired: false)
            .Build();

    private static SlashCommandOptionBuilder Port(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name).WithDescription(description)
            .WithType(ApplicationCommandOptionType.Integer)
            .WithMinValue(1).WithMaxValue(65535).WithRequired(false);

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        // OWNER. This reaches root-level OS operations, the same tier as /manual and /firewall.
        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        // ---- read the existing layout straight from configuration (not the defaulted views) ----
        var usedIndices = UsedRconIndices();
        var existingRconPorts = options.Servers.Select(s => s.Port).ToList();
        var existingUnits = SplitList(configuration["PAVLOV_UNITS"]);
        var existingBases = SplitList(configuration["PAVLOV_BASES"]);

        // ---- assemble the request from options + sensible, slot-aware defaults ----
        var prospectiveSlot = usedIndices.Count + 1;

        var unit = (StringOption(command, "unit") ?? $"pavlovserver{Off(prospectiveSlot)}").Trim();
        if (!ServiceControl.IsPlausibleUnitName(unit))
        {
            await Reply(command, Theme.Failure("Unusable unit name",
                $"`{Sanitize.Code(unit)}` is not a valid systemd unit name.")).ConfigureAwait(false);
            return;
        }

        var installDir = (StringOption(command, "installdir") ?? Path.Combine(DefaultInstallParent(), unit)).TrimEnd('/');
        var rconPassword = StringOption(command, "rconpassword") is { Length: > 0 } supplied ? supplied : GeneratePassword();
        var generated = StringOption(command, "rconpassword") is not { Length: > 0 };

        var spec = new ServerProvisionSpec
        {
            Slot = prospectiveSlot,
            UnitName = unit,
            InstallDir = installDir,
            ServerName = ProvisionText.CleanServerName(StringOption(command, "name")),
            GamePort = (int)(IntOption(command, "gameport") ?? DefaultGamePort),
            QueryPort = (int)(IntOption(command, "queryport") ?? DefaultQueryPort),
            RconPort = (int)(IntOption(command, "rconport") ?? DefaultRconPort),
            RconPassword = rconPassword,
            MaxPlayers = (int)(IntOption(command, "maxplayers") ?? features.DefaultServerCapacity),
            Maps = ParseMaps(StringOption(command, "maps")),
            GamePassword = StringOption(command, "gamepassword"),
        };

        // ---- plan the slot and validate everything, reporting all problems at once ----
        var (plan, planProblems) = ServerSlotPlanner.Plan(
            new ServerLayout(usedIndices, existingUnits, existingBases), unit, installDir);
        var problems = new List<string>(planProblems);
        problems.AddRange(ProvisionValidation.Check(spec, existingRconPorts, existingUnits, existingBases));

        if (problems.Count > 0 || plan is null)
        {
            await Reply(command, Theme.Failure($"Cannot provision server {prospectiveSlot}",
                string.Join("\n", problems.Select(p => $"{Theme.Dot} {p}")))).ConfigureAwait(false);
            return;
        }

        // The authoritative slot from the planner (equals prospectiveSlot when the layout is sound).
        spec = spec with { Slot = plan.Slot };

        var who = command.User.Username;
        await audit.RecordAsync("provisionserver", who, spec.UnitName,
            $"slot {spec.Slot}, ports {spec.GamePort}/{spec.QueryPort}/{spec.RconPort}, dir {spec.InstallDir}", ct)
            .ConfigureAwait(false);
        logger.LogWarning("PROVISIONSERVER by {User} | slot {Slot} | unit {Unit} | dir {Dir}",
            who, spec.Slot, spec.UnitName, spec.InstallDir);

        // ---- kick off, and hand the slow work to a detached task ----
        // Generated up front, always - not conditionally, and not inside the provisioner - because
        // whether the steam account needs creating is only known deep in a background task with no
        // interaction left to reply on. REQUIRED: an account this creates never goes unpassworded.
        var steamUserPassword = GeneratePassword();
        await Reply(command, StartedEmbed(spec, generated, steamUserPassword)).ConfigureAwait(false);

        var request = new ProvisionRequest(
            spec, plan.FinalUnits, plan.FinalBases,
            FinalPlayerCountChannels: null,
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            steamUserPassword);

        var channelId = command.Channel.Id;
        var messageId = await Post.SendAsync(channelId, Checklist(spec, InitialSteps()), null, ct).ConfigureAwait(false);

        // Detached: SteamCMD outlives both the command budget and the interaction token, so this
        // runs under the HOST lifetime, not the command's ct, and reports to the channel.
        _ = Task.Run(() => RunAsync(request, spec, channelId, messageId), CancellationToken.None);
    }

    /// <summary>Drive the provision to completion, editing the channel checklist as it goes.</summary>
    private async Task RunAsync(ProvisionRequest request, ServerProvisionSpec spec, ulong channelId, ulong? messageId)
    {
        var stopping = lifetime.ApplicationStopping;
        try
        {
            await provisioner.ProvisionAsync(request, async steps =>
            {
                var embed = Checklist(spec, steps);
                if (messageId is { } id) await Post.EditAsync(channelId, id, embed, null, stopping).ConfigureAwait(false);
                else await Post.SendAsync(channelId, embed, null, stopping).ConfigureAwait(false);
            }, stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Provisioning server {Slot} threw", spec.Slot);
            try
            {
                await Post.SendAsync(channelId,
                    Theme.Failure($"Provisioning server {spec.Slot} failed", Sanitize.Code(ex.Message)).Build(),
                    null, stopping).ConfigureAwait(false);
            }
            catch (Exception postEx) when (postEx is not OperationCanceledException)
            {
                logger.LogWarning(postEx, "Could not post the provisioning failure for server {Slot}", spec.Slot);
            }
        }
    }

    // ---- rendering ----

    private static Embed StartedEmbed(ServerProvisionSpec spec, bool generatedPassword, string steamUserPassword)
    {
        var embed = Theme.Notice($"Provisioning server {spec.Slot} started",
                $"Installing **{Sanitize.Code(spec.ServerName)}** as `{spec.UnitName}`. SteamCMD downloads several GB, so this " +
                "runs in the background - watch the checklist I just posted in this channel. The bot restarts itself at the end.")
            .AddField("Ports", $"game `{spec.GamePort}/udp` • query `{spec.QueryPort}/udp` • rcon `{spec.RconPort}/tcp`", inline: false)
            .AddField("Install dir", $"`{Sanitize.Code(spec.InstallDir)}`", inline: false)
            .AddField($"{Theme.Warn} Port collisions",
                "Only RCON ports are checked against other servers. Make sure the game and query ports are unique on this box.", inline: false);

        if (generatedPassword)
            embed.AddField("RCON password (generated - save it)", $"||`{spec.RconPassword}`||", inline: false);

        /* ALWAYS shown, not conditionally: whether the "steam" OS account already exists is only
           known deep inside the background provisioner, long after this reply is the only chance
           to hand the operator a secret. Unused if the account turns out to already exist. */
        embed.AddField("steam OS account password (used only if the account needs creating)",
            $"||`{steamUserPassword}`||", inline: false);

        return embed.Build();
    }

    private static IReadOnlyList<ProvisionStep> InitialSteps() =>
        [new ProvisionStep("Starting…", ProvisionStatus.Running, "")];

    private static Embed Checklist(ServerProvisionSpec spec, IReadOnlyList<ProvisionStep> steps)
    {
        var body = string.Join("\n", steps.Select(s =>
        {
            var line = $"{Glyph(s.Status)} {s.Name}";
            return s.Detail.Length > 0 ? $"{line} — {Sanitize.Code(s.Detail)}" : line;
        }));

        var title = $"Provisioning server {spec.Slot} ({spec.UnitName})";
        var anyFailed = steps.Any(s => s.Status == ProvisionStatus.Failed);
        var running = steps.Any(s => s.Status is ProvisionStatus.Pending or ProvisionStatus.Running);

        var embed = anyFailed && !running ? Theme.Failure(title, body)
            : running ? Theme.Notice(title, body)
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

    // ---- option and layout helpers ----

    /// <summary>The RCON slots configured right now, read raw so gaps and defaults are visible.</summary>
    private IReadOnlyList<int> UsedRconIndices()
    {
        var indices = new List<int>();
        for (var i = 1; i <= ProvisionValidation.MaxServers; i++)
            if (!string.IsNullOrWhiteSpace(configuration[$"RCON_HOST_{i}"])) indices.Add(i);
        return indices;
    }

    private string DefaultInstallParent()
    {
        var first = configuration["PAVLOV_BASE_1"]?.Trim();
        var root = string.IsNullOrWhiteSpace(first) ? PavlovBot.Host.Storage.PavlovInstalls.DefaultBase : first.TrimEnd('/');
        return Path.GetDirectoryName(root) is { Length: > 0 } parent ? parent : "/home/steam";
    }

    /// <summary>The box's off-by-one unit numbering: server 1 is <c>pavlovserver</c> (no suffix).</summary>
    internal static string Off(int slot) => slot <= 1 ? "" : (slot - 1).ToString(CultureInfo.InvariantCulture);

    internal static IReadOnlyList<MapEntry> ParseMaps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [new MapEntry("datacenter", "SND")];

        var maps = new List<MapEntry>();
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', 2, StringSplitOptions.TrimEntries);
            var mapId = parts[0];
            var mode = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "SND";
            if (mapId.Length > 0) maps.Add(new MapEntry(mapId, mode));
        }
        return maps.Count > 0 ? maps : [new MapEntry("datacenter", "SND")];
    }

    private static string GeneratePassword()
    {
        // Alphanumeric only: trivially inside ProvisionValidation's allowed charset and safe
        // unquoted in every file it lands in. 20 characters of it is ample entropy.
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(alphabet, 20);
    }

    private static List<string> SplitList(string? raw) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? StringOption(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as string;

    private static long? IntOption(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as long?;

    private static Task Reply(SocketSlashCommand command, Embed embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.AllowedMentions = AllowedMentions.None;
        });

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) => Reply(command, embed.Build());
}
