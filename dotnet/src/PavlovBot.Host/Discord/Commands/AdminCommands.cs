using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Vpn;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/setpin</c> - lock a server behind a join PIN.
/// </summary>
/// <remarks>
/// The wiki's signature is <c>SetPin [Optional PinNumber]</c>. Two details in it are
/// load-bearing and were both got wrong before reading it properly:
///
///   THE PIN IS 1 TO 4 DIGITS WITH NO LEADING ZERO. Pavlov treats it as a NUMBER, so
///   "0123" is not a valid PIN - and a generator producing six digits would have had every
///   single PIN rejected by the server.
///
///   THERE IS NO RemovePin VERB. Clearing the PIN is <c>SetPin</c> with no argument. A
///   command that sent "RemovePin" would be silently ignored and the server would stay
///   locked, which is the worst possible failure for this feature.
/// </remarks>
public sealed partial class SetPinCommand(RconRegistry rcon, Access access, ILogger<SetPinCommand> logger) : ISlashCommand
{
    /// <summary>1-4 digits, no leading zero. Straight from the wiki's signature.</summary>
    [GeneratedRegex(@"^[1-9]\d{0,3}$")]
    private static partial Regex ValidPin { get; }

    public string Name => "setpin";

    /// <summary>Ephemeral: a PIN posted in a public channel is not a lock.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server").WithDescription("Which server to lock")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        foreach (var name in rcon.Servers) server.AddChoice(name, name);
        if (rcon.Servers.Count > 1) server.AddChoice("All servers", "all");

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Lock a server behind a join PIN")
            .AddOption(server)
            .AddOption("pin", ApplicationCommandOptionType.String, "1-4 digits, no leading zero. Leave blank to generate one.", isRequired: false)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var target = command.Data.Options.First(o => o.Name == "server").Value as string ?? "";
        var supplied = (command.Data.Options.FirstOrDefault(o => o.Name == "pin")?.Value as string)?.Trim();

        string pin;
        if (supplied is { Length: > 0 })
        {
            if (!ValidPin.IsMatch(supplied))
            {
                await Reply(command, Theme.Failure("That PIN will not work",
                    "Pavlov treats the PIN as a number: **1 to 4 digits, no leading zero**. " +
                    "`0123` and `12345` are both rejected by the server.")).ConfigureAwait(false);
                return;
            }
            pin = supplied;
        }
        else
        {
            // Crypto RNG. A predictable PIN on a locked server is the same as no lock.
            // 1000-9999 keeps it four digits with no leading zero by construction.
            pin = RandomNumberGenerator.GetInt32(1000, 10000).ToString(CultureInfo.InvariantCulture);
        }

        var servers = target == "all" ? rcon.Servers.ToList() : [target];
        var applied = new List<string>();

        foreach (var server in servers)
        {
            try
            {
                await rcon.SendAsync(server, $"SetPin {pin}", ct).ConfigureAwait(false);
                applied.Add(server);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("SetPin failed on {Server}: {Message}", server, ex.Message);
            }
        }

        // The PIN itself is never logged. It is a credential, and an audit line containing
        // it defeats the lock for anyone who can read the log.
        logger.LogInformation("setpin | servers={Servers} | by={By}", string.Join(",", applied), command.User.Username);

        await Reply(command, applied.Count == 0
            ? Theme.Failure("Could not lock the server", "No server accepted the command.")
            : Theme.Success($"{Theme.Deny} Locked",
                $"**{string.Join(", ", applied)}** now requires the PIN **{pin}**.\n" +
                "Only you can see this message. Clear it with `/removepin`.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary>
/// <c>/removepin</c> - clear the PIN.
/// </summary>
/// <remarks>
/// Sends a BARE <c>SetPin</c>. There is no RemovePin verb - the wiki's signature makes the
/// argument optional, and omitting it is how the lock is cleared.
/// </remarks>
public sealed class RemovePinCommand(RconRegistry rcon, Access access, ILogger<RemovePinCommand> logger) : ISlashCommand
{
    public string Name => "removepin";

    public ApplicationCommandProperties Build()
    {
        var server = new SlashCommandOptionBuilder()
            .WithName("server").WithDescription("Which server to unlock")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);

        foreach (var name in rcon.Servers) server.AddChoice(name, name);
        if (rcon.Servers.Count > 1) server.AddChoice("All servers", "all");

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Clear a server's join PIN")
            .AddOption(server)
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Admin, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Admin, command))).ConfigureAwait(false);
            return;
        }

        var target = command.Data.Options.First().Value as string ?? "";
        var servers = target == "all" ? rcon.Servers.ToList() : [target];
        var cleared = new List<string>();

        foreach (var server in servers)
        {
            try
            {
                // Bare SetPin. NOT "SetPin 0" - that sets the PIN to zero rather than
                // clearing it, and the server stays locked.
                await rcon.SendAsync(server, "SetPin", ct).ConfigureAwait(false);
                cleared.Add(server);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("SetPin (clear) failed on {Server}: {Message}", server, ex.Message);
            }
        }

        logger.LogInformation("removepin | servers={Servers} | by={By}", string.Join(",", cleared), command.User.Username);

        await Reply(command, cleared.Count == 0
            ? Theme.Failure("Could not unlock the server", "No server accepted the command.")
            : Theme.Success($"{Theme.Up} Unlocked", $"**{string.Join(", ", cleared)}** no longer requires a PIN.")).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/iplookup</c> - location, ISP and VPN status for one address.</summary>
public sealed class IpLookupCommand(VpnScreeningService vpn, Access access, StaticMap? map = null) : ISlashCommand
{
    public string Name => "iplookup";

    /// <summary>Ephemeral: an address in a public channel is somebody's home location.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Look up an IP: location, ISP and VPN status")
            .AddOption("ip", ApplicationCommandOptionType.String, "The address to look up", isRequired: true)
            .AddOption("refresh", ApplicationCommandOptionType.Boolean, "Re-screen instead of using the cached verdict", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var ip = (command.Data.Options.First(o => o.Name == "ip").Value as string ?? "").Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            await Reply(command, Theme.Failure("Not an IP address", $"`{Sanitize.Code(ip)}` could not be parsed.")).ConfigureAwait(false);
            return;
        }

        var record = await vpn.CheckAsync(ip, ct).ConfigureAwait(false);
        if (record is null)
        {
            /* Null is NOT clean - it means nothing could be determined. Saying "clean" here
               would be asserting innocence on the strength of an outage. */
            await Reply(command, Theme.Warning("No verdict",
                "Nothing could be determined for that address - every detector failed or is unconfigured. " +
                "This is **not** the same as a clean result.")).ConfigureAwait(false);
            return;
        }

        var location = new[] { record.City, record.Region, record.Country }.Where(p => !string.IsNullOrEmpty(p));
        var embed = Theme.Notice($"IP lookup — {ip}")
            .AddField("Location", location.Any() ? string.Join(", ", location) : "unknown", true)
            .AddField("ISP", record.Isp ?? record.Organization ?? "unknown", true)
            .AddField("VPN / proxy", Verdict(record), true);

        if (record.Provider is { Length: > 0 }) embed.AddField("Provider", record.Provider, true);
        if (record.Asn is { Length: > 0 }) embed.AddField("ASN", record.Asn, true);
        if (record.ThreatScore is { } score) embed.AddField("Risk", $"{score:0}/100", true);

        embed.Brand($"Screened {record.CheckedAt:yyyy-MM-dd} by {record.Sources.Count} detector(s)");

        /* The map is strictly a bonus. Everything above is already correct, so any failure
           here - no key, no coordinates, a map service outage - just means no picture.
           `record.Local` is skipped because a private address has no place on a map. */
        var image = !record.Local && map is not null && record.Latitude is { } lat && record.Longitude is { } lon
            ? await map.RenderAsync(lat, lon, ct).ConfigureAwait(false)
            : null;

        if (image is null)
        {
            await Reply(command, embed).ConfigureAwait(false);
            return;
        }

        /* attachment:// points the embed at the file uploaded in this same message, so the
           key that rendered it never leaves the bot. See StaticMap for why that matters. */
        const string fileName = "location.png";
        embed.WithImageUrl($"attachment://{fileName}");

        using var stream = new MemoryStream(image);
        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Build();
            m.AllowedMentions = AllowedMentions.None;
            m.Attachments = new[] { new FileAttachment(stream, fileName) };
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The same verdict, worded the same way, as the connection card and the ban feed.
    /// </summary>
    /// <remarks>
    /// This used to print "VPN + datacenter (flagged (inconclusive))" - the signals first and
    /// a parenthesised verdict inside another parenthesis. The signals are what was detected;
    /// the verdict is what it MEANS, and that is the part being asked for.
    /// </remarks>
    private static string Verdict(VpnRecord record)
    {
        var summary = VpnVerdict.Of(record);

        var mark = summary.Stance switch
        {
            VpnStance.Confirmed => Theme.Bad,
            VpnStance.Disputed or VpnStance.Likely => Theme.Warn,
            VpnStance.Clean => Theme.Ok,
            _ => Theme.Dot,
        };

        var line = $"{mark} **{summary.Headline}**\n{summary.Plain}";

        return VpnVerdict.Signals(record) is { Count: > 0 } signals
            ? $"{line}\nSignals: **{string.Join(", ", signals)}**"
            : line;
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}

/// <summary><c>/vpncheck</c> - probe every detector and report which are up.</summary>
public sealed class VpnCheckCommand(VpnScreeningService vpn, Access access) : ISlashCommand
{
    public string Name => "vpncheck";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Probe every VPN detector: which are up and each one's role")
            .AddOption("ip", ApplicationCommandOptionType.String, "Address to probe with (default 8.8.8.8)", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command))).ConfigureAwait(false);
            return;
        }

        var ip = (command.Data.Options.FirstOrDefault()?.Value as string)?.Trim() is { Length: > 0 } given ? given : "8.8.8.8";

        // Spends one lookup per configured detector, which is why this is owner-gated.
        var probes = await vpn.ProbeAsync(ip, ct).ConfigureAwait(false);

        if (probes.Count == 0)
        {
            await Reply(command, Theme.Warning("No detectors configured",
                "VPN screening is off entirely. Set at least one API key.")).ConfigureAwait(false);
            return;
        }

        /* Three states, not two. A detector with no key was never asked; one that answered
           has a verdict; one that failed now says WHY - a bad key, a spent quota, a timeout
           and a blocked port are four different fixes, and this screen used to render all
           four as the same two words. */
        var lines = probes.OrderBy(p => p.Tier).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p =>
            $"{Mark(p)} **{p.Name}** — {p.Role}\n" +
            $"{Theme.Dot} {p.Outcome}" + (p.Configured ? $" ({p.DurationMs:0}ms)" : ""));

        var answered = probes.Count(p => p.Answered);
        var embed = Theme.Notice($"VPN detectors — probed with {ip}",
            $"**{answered}** of **{probes.Count(p => p.Configured)}** configured detector(s) returned a verdict.\n\n" +
            string.Join("\n", lines));

        if (vpn.ConfigurationWarning() is { } warning) embed.AddField($"{Theme.Warn} Configuration", warning);

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>Answered, failed, or never asked - three states need three marks.</summary>
    private static string Mark(PavlovBot.Host.Vpn.DetectorProbe probe) =>
        !probe.Configured ? Theme.Dot : probe.Answered ? Theme.Ok : Theme.Bad;

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
