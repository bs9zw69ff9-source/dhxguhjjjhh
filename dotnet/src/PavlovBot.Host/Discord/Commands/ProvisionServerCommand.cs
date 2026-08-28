using System.Globalization;
using System.Net;
using System.Net.Sockets;
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

    /// <summary>
    /// The password given to the <c>steam</c> OS account when this creates it.
    /// </summary>
    /// <remarks>
    /// LITERALLY "1", BY EXPLICIT OPERATOR INSTRUCTION, on a box reached only by SSH key. It is
    /// still set rather than left blank, because an account with no password at all is a different
    /// and more awkward state to be in later. Worth knowing what this does and does not cost:
    /// <c>sudo -u steam</c> and key-based SSH never consult it, so it is not what protects the
    /// account - but it IS a real credential for anything that does check one (a console login,
    /// <c>su - steam</c>), and combined with the full sudo this command grants, "1" is enough to
    /// become root for anyone who reaches such a prompt.
    /// </remarks>
    private const string SteamUserPassword = "1";

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Install a new Pavlov server, configure it and wire it into this bot (needs root)")
            .AddOption("name", ApplicationCommandOptionType.String, "Public server name", isRequired: true)
            /* The descriptions name the SEQUENCE, not one number: these are registered with
               Discord once, so they cannot say what this particular box's next server will get.
               See ServerPortDefaults for why each stride is what it is. */
            .AddOption(Port("gameport", "Game port (UDP; defaults step per server: 7777, 7778, 7779...)"))
            .AddOption(Port("queryport", "Query/beacon port (UDP; defaults to the game port + 400)"))
            .AddOption(Port("rconport", "RCON port (TCP; defaults step per server: 9100, 9200, 9300...)"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("maxplayers").WithDescription("Max players (default 24 - Shack's hard cap)")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithMinValue(1).WithMaxValue(ProvisionValidation.MaxShackPlayers).WithRequired(false))
            .AddOption("maps", ApplicationCommandOptionType.String, "Rotation as MapId:Mode,MapId:Mode (default: this network's map)", isRequired: false)
            /* NO unit OR installdir OPTION. The naming is a fixed convention on this box - server
               1 is pavlovserver, server 2 is pavlovserver1, and so on - and the unit name, the
               install directory and the slot all have to agree. Letting any of them be typed in
               is how they stop agreeing, so the slot decides all three. */
            .AddOption("copy", ApplicationCommandOptionType.Boolean,
                "Copy server 1's install instead of downloading it with SteamCMD", isRequired: false)
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

        /* THE DISK DECIDES WHICH SLOT, not the count of .env entries. A configured slot with no
           install behind it is the slot to build - otherwise asking for a first server on a box
           where RCON_HOST_1 was set but nothing was ever installed produced a SECOND one, leaving
           the first as a hole that every later run then skipped too. */
        var installed = InstalledSlots(usedIndices.Count);
        var prospectiveSlot = ServerSlotPlanner.TargetSlot(usedIndices.Count, installed);
        var refilling = prospectiveSlot <= usedIndices.Count;

        /* THE CONVENTION DECIDES BOTH, from the slot alone: server 1 is pavlovserver, server 2 is
           pavlovserver1, server 3 is pavlovserver2. The unit is named for the directory and the
           directory sits beside its siblings, so the two cannot disagree. */
        var unit = $"pavlovserver{Off(prospectiveSlot)}";
        var installDir = Path.Combine(DefaultInstallParent(), unit);

        /* When filling in a slot that is already configured, its OWN current port, unit and base
           are what is being replaced - counting them as collisions would make the command refuse
           to rebuild the very server it was asked to rebuild. The planner still gets the FULL
           lists, because it replaces one entry in place and needs to see every position. */
        var rconPortsToAvoid = Without(existingRconPorts, refilling ? prospectiveSlot : 0);
        var unitsToAvoid = Without(existingUnits, refilling ? prospectiveSlot : 0);
        var basesToAvoid = Without(existingBases, refilling ? prospectiveSlot : 0);
        var rconPassword = StringOption(command, "rconpassword") is { Length: > 0 } supplied ? supplied : GeneratePassword();
        var generated = StringOption(command, "rconpassword") is not { Length: > 0 };

        /* STEPPED PER SLOT, so server 2 is not a copy of server 1. The query port hangs off the
           game port rather than the slot, so naming a game port by hand still yields the pair
           Pavlov expects. */
        var gamePort = (int)(IntOption(command, "gameport") ?? ServerPortDefaults.GamePort(prospectiveSlot));
        var queryPort = (int)(IntOption(command, "queryport") ?? ServerPortDefaults.QueryPortFor(gamePort));
        var rconPort = (int)(IntOption(command, "rconport") ?? ServerPortDefaults.RconPort(prospectiveSlot));

        var spec = new ServerProvisionSpec
        {
            Slot = prospectiveSlot,
            UnitName = unit,
            InstallDir = installDir,
            ServerName = ProvisionText.CleanServerName(StringOption(command, "name")),
            GamePort = gamePort,
            QueryPort = queryPort,
            RconPort = rconPort,
            RconPassword = rconPassword,
            MaxPlayers = (int)(IntOption(command, "maxplayers") ?? features.DefaultServerCapacity),
            Maps = ParseMaps(StringOption(command, "maps")),
            GamePassword = StringOption(command, "gamepassword"),
        };

        // ---- plan the slot and validate everything, reporting all problems at once ----
        var (plan, planProblems) = ServerSlotPlanner.Plan(
            new ServerLayout(usedIndices, existingUnits, existingBases), prospectiveSlot, unit, installDir);
        var problems = new List<string>(planProblems);
        problems.AddRange(ProvisionValidation.Check(spec, rconPortsToAvoid, unitsToAvoid, basesToAvoid));

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

        /* Server 1's install, which is what a copy is made from. Same convention as everything
           else: slot 1 is "pavlovserver" with no suffix. */
        var copyFrom = BoolOption(command, "copy") == true
            ? Path.Combine(DefaultInstallParent(), $"pavlovserver{Off(1)}")
            : null;

        // ---- kick off, and hand the slow work to a detached task ----
        await Reply(command, StartedEmbed(spec, generated, SteamUserPassword, copyFrom, refilling)).ConfigureAwait(false);

        var request = new ProvisionRequest(
            spec, plan.FinalUnits, plan.FinalBases,
            FinalPlayerCountChannels: null,
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            SteamUserPassword,
            RconHostFor(prospectiveSlot),
            copyFrom,
            refilling);

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

    private static Embed StartedEmbed(
        ServerProvisionSpec spec, bool generatedPassword, string steamUserPassword, string? copyFrom, bool refilling)
    {
        var source = copyFrom is null
            ? "SteamCMD downloads several GB, so this"
            : $"Copying `{Sanitize.Code(copyFrom)}` rather than downloading, so this";

        var embed = Theme.Notice($"Provisioning server {spec.Slot} started",
                $"Installing **{Sanitize.Code(spec.ServerName)}** as `{spec.UnitName}`. {source} " +
                "runs in the background - watch the checklist I just posted in this channel. The bot restarts itself at the end.");

        /* Said out loud, because "server 1" when the operator expected the next number is
           surprising until you know why - and the reason is worth knowing: that slot was
           configured but had nothing installed behind it. */
        if (refilling)
            embed.AddField($"{Theme.Info} Filling in an existing slot",
                $"Server {spec.Slot} is already configured but has no install on disk, so this builds THAT " +
                "rather than adding another. Its RCON settings are replaced.", inline: false);

        embed
            .AddField("Ports", $"game `{spec.GamePort}/udp` • query `{spec.QueryPort}/udp` • rcon `{spec.RconPort}/tcp`", inline: false)
            .AddField("Install dir", $"`{Sanitize.Code(spec.InstallDir)}`", inline: false)
            .AddField($"{Theme.Warn} Port collisions",
                "Defaults step per server (game +1, RCON +100), so they do not collide on their own. " +
                "Only RCON ports can be checked against other servers though - if you named the game or " +
                "query port yourself, make sure it is unique on this box.", inline: false);

        if (generatedPassword)
            embed.AddField("RCON password (generated - save it)", $"||`{spec.RconPassword}`||", inline: false);

        /* ALWAYS shown, not conditionally: whether the "steam" OS account already exists is only
           known deep inside the background provisioner, long after this reply is the only chance
           to hand the operator a secret. Unused if the account turns out to already exist. */
        embed.AddField("steam OS account password (used only if the account needs creating)",
            $"||`{steamUserPassword}`||", inline: false);

        embed.AddField($"{Theme.Warn} steam is getting FULL sudo",
            "Every provision installs `/etc/sudoers.d/pavlov-steam-full`, giving the `steam` account " +
            "unrestricted, passwordless root. This was requested explicitly and is not this bot's usual " +
            "narrow, per-unit grant - a compromise of the game server or a bad workshop map is a root " +
            "compromise from here on. Remove that file if you did not mean for that to apply here.", inline: false);

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

    /// <summary>
    /// Which configured slots have a real install behind them.
    /// </summary>
    /// <remarks>
    /// KEYED ON <c>PavlovServer.sh</c>, not on the directory existing. That script is what the
    /// unit's ExecStart runs, so its absence means there is nothing to start no matter what else
    /// is in the tree - and an empty or half-deleted directory left over from a failed run would
    /// otherwise read as a working server and get skipped forever.
    /// </remarks>
    private IReadOnlyCollection<int> InstalledSlots(int configuredCount)
    {
        var parent = DefaultInstallParent();
        var installed = new List<int>();

        for (var slot = 1; slot <= configuredCount; slot++)
        {
            var path = Path.Combine(parent, $"pavlovserver{Off(slot)}", "PavlovServer.sh");
            if (File.Exists(path)) installed.Add(slot);
        }

        return installed;
    }

    /// <summary>
    /// The address to put in <c>RCON_HOST_{N}</c>.
    /// </summary>
    /// <remarks>
    /// THE BOX'S OWN ANSWER FIRST. This was hard-coded to <c>127.0.0.1</c>, which is right in
    /// isolation - RCON is on the same machine - but wrong for consistency: a box whose existing
    /// servers are configured by their real address gained one slot that disagreed with the rest,
    /// and the next person to read the file has to work out whether that was deliberate. So an
    /// address already in use by another server wins, because it is the one the operator has
    /// already proved works here.
    ///
    /// Failing that, the machine's own primary address, discovered without sending anything (see
    /// <see cref="LocalAddress"/>), and loopback only when even that cannot be determined.
    /// </remarks>
    private string RconHostFor(int slotBeingBuilt)
    {
        for (var i = 1; i <= ProvisionValidation.MaxServers; i++)
        {
            if (i == slotBeingBuilt) continue;   // its own stale value is not a precedent
            if (configuration[$"RCON_HOST_{i}"]?.Trim() is { Length: > 0 } host) return host;
        }

        return LocalAddress() ?? "127.0.0.1";
    }

    /// <summary>
    /// This machine's primary IPv4, or null.
    /// </summary>
    /// <remarks>
    /// A connected UDP socket to a routable address. NOTHING IS SENT - connecting a datagram
    /// socket only makes the OS pick the interface it would use, which is exactly the question
    /// being asked, and needs no DNS, no network round trip and no external service to answer it.
    /// </remarks>
    internal static string? LocalAddress()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A copy of <paramref name="values"/> without the entry at 1-based <paramref name="slot"/>.</summary>
    /// <remarks>Slot 0 means "drop nothing", which is the append case.</remarks>
    private static List<T> Without<T>(IReadOnlyList<T> values, int slot) =>
        slot >= 1 && slot <= values.Count
            ? [.. values.Where((_, i) => i != slot - 1)]
            : [.. values];

    private string DefaultInstallParent()
    {
        var first = configuration["PAVLOV_BASE_1"]?.Trim();
        var root = string.IsNullOrWhiteSpace(first) ? PavlovBot.Host.Storage.PavlovInstalls.DefaultBase : first.TrimEnd('/');
        return Path.GetDirectoryName(root) is { Length: > 0 } parent ? parent : "/home/steam";
    }

    /// <summary>The box's off-by-one unit numbering: server 1 is <c>pavlovserver</c> (no suffix).</summary>
    internal static string Off(int slot) => slot <= 1 ? "" : (slot - 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The rotation a server gets when none is named: this network's own map, from the config
    /// that is actually running, rather than a stock one nobody here plays.
    /// </summary>
    internal static readonly MapEntry DefaultMap = new("UGC5616264", "CUSTOM");

    internal static IReadOnlyList<MapEntry> ParseMaps(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [DefaultMap];

        var maps = new List<MapEntry>();
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', 2, StringSplitOptions.TrimEntries);
            var mapId = parts[0];
            var mode = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "SND";
            if (mapId.Length > 0) maps.Add(new MapEntry(mapId, mode));
        }
        return maps.Count > 0 ? maps : [DefaultMap];
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

    private static bool? BoolOption(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value as bool?;

    private static Task Reply(SocketSlashCommand command, Embed embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed;
            m.AllowedMentions = AllowedMentions.None;
        });

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) => Reply(command, embed.Build());
}
