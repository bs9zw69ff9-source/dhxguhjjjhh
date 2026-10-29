using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Network;

/// <summary>Blocking and rate-limiting a flood while it lasts.</summary>
public interface IFloodShield
{
    /// <summary>Whether this bot is allowed to touch the firewall at all (<c>ATTACK_MITIGATION=on</c>).</summary>
    bool Enabled { get; }

    /// <summary>The limits it applies while an attack is on.</summary>
    ShieldLimits Limits { get; }

    /// <summary>Load the table with the attack-time limits on or off. Null on success, else why not.</summary>
    Task<string?> ApplyAsync(bool engaged, CancellationToken ct);

    /// <summary>Let these addresses through whatever the limits say.</summary>
    Task AllowAsync(IReadOnlySet<uint> addresses, CancellationToken ct);

    /// <summary>What the rules have dropped, or null when they cannot be read.</summary>
    Task<ShieldStats?> StatsAsync(CancellationToken ct);

    /// <summary>Let every blocked address back in. Null on success, else why not.</summary>
    Task<string?> UnblockAllAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="IFloodShield"/> over nftables, with the rules from <see cref="FloodShieldRules"/>.
/// </summary>
/// <remarks>
/// OFF UNLESS ASKED FOR. Two bots share this box, and the table is the host's, not the bot's:
/// both managing it would have each one's "limits off" undo the other's "limits on". So exactly
/// one bot gets <c>ATTACK_MITIGATION=on</c>.
///
/// SCRIPTS GO IN ON STDIN (<c>nft -f -</c>), so a whole change is one atomic transaction: nft
/// applies all of it or none, and a half-loaded rule set cannot happen.
/// </remarks>
public sealed class FloodShield(bool enabled, ShieldLimits limits, ILogger<FloodShield> logger) : IFloodShield
{
    private static readonly TimeSpan NftTimeout = TimeSpan.FromSeconds(15);

    public bool Enabled { get; } = enabled;

    public ShieldLimits Limits { get; } = limits;

    public async Task<string?> ApplyAsync(bool engaged, CancellationToken ct)
    {
        if (Refusal() is { } refused) return refused;
        if (FloodShieldRules.Ports(Limits.GamePorts) is null) return $"ATTACK_GAME_PORTS \"{Limits.GamePorts}\" is not a list of ports";

        var problem = await NftAsync(FloodShieldRules.Script(Limits, engaged), ct).ConfigureAwait(false);
        if (problem is null)
        {
            logger.LogWarning(engaged
                ? "DDoS shield ENGAGED: over {PerSource} pkt/s from one address is blocked for {Minutes} min, unknown traffic to {Ports} capped at {Unknown} pkt/s"
                : "DDoS shield standing by: blocked addresses stay blocked, the attack-time limits are off ({PerSource}/{Minutes}/{Ports}/{Unknown})",
                Limits.PerSourcePps, Limits.BlockMinutes, Limits.GamePorts, Limits.UnknownPps);
        }
        return problem;
    }

    public async Task AllowAsync(IReadOnlySet<uint> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (Refusal() is not null || addresses.Count == 0) return;

        foreach (var script in FloodShieldRules.AllowScripts(addresses))
        {
            if (await NftAsync(script, ct).ConfigureAwait(false) is { } problem)
            {
                logger.LogWarning("Could not add players to the DDoS allow list: {Problem}", problem);
                return;
            }
        }
    }

    public async Task<ShieldStats?> StatsAsync(CancellationToken ct)
    {
        if (Refusal() is not null) return null;

        var run = await ProcessRunner.RunAsync("nft", ["-j", "list", "table", "inet", FloodShieldRules.Table], NftTimeout, logger, ct)
            .ConfigureAwait(false);
        return run.Ok ? FloodShieldRules.ParseStats(run.Stdout) : null;
    }

    public async Task<string?> UnblockAllAsync(CancellationToken ct)
    {
        if (Refusal() is { } refused) return refused;
        return await NftAsync(FloodShieldRules.UnblockScript(), ct).ConfigureAwait(false);
    }

    private string? Refusal() =>
        !Enabled ? "ATTACK_MITIGATION is off on this bot"
        : !Environment.IsPrivilegedProcess ? "the bot is not running as root, so it cannot change the firewall"
        : null;

    private async Task<string?> NftAsync(string script, CancellationToken ct)
    {
        var run = await ProcessRunner.RunAsync("nft", ["-f", "-"], NftTimeout, logger, ct, stdin: script).ConfigureAwait(false);
        if (!run.Started) return "nft is not installed - run `/packettools install`";
        if (run.TimedOut) return "nft did not answer within 15 seconds";
        return run.ExitCode == 0 ? null : $"nft refused the rules: {run.Combined}";
    }
}

/// <summary>
/// <c>iftop -nNP</c> in text mode for a few seconds: who is using the bandwidth, per connection.
/// </summary>
/// <remarks>
/// A SNAPSHOT, NOT A FILTER. iftop only shows traffic - it cannot block or limit anything; that
/// is <see cref="FloodShield"/>'s job. Taken a few seconds into an attack and put in the report:
/// <c>-n</c> and <c>-N</c> keep addresses and ports as numbers (no lookups, which a flood would
/// turn into thousands of DNS queries), <c>-P</c> shows ports, <c>-t</c> prints text and exits.
/// </remarks>
public static class IftopSnapshot
{
    internal static IReadOnlyList<string> Argv(string iface, int seconds) =>
        ["-i", iface, "-nNP", "-t", "-s", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-L", "20"];

    /// <summary>iftop's text report, or null when it could not run.</summary>
    public static async Task<string?> TakeAsync(string iface, ILogger logger, CancellationToken ct, int seconds = 10)
    {
        if (!LinkCounters.IsPlausibleInterface(iface) || !Environment.IsPrivilegedProcess) return null;

        var run = await ProcessRunner.RunAsync("iftop", Argv(iface, seconds), TimeSpan.FromSeconds(seconds + 15), logger, ct)
            .ConfigureAwait(false);
        if (!run.Started) return null;

        // iftop prints its report on stdout and chatter ("interface: eth0") on stderr.
        return string.IsNullOrWhiteSpace(run.Stdout) ? null : run.Stdout.TrimEnd();
    }
}
