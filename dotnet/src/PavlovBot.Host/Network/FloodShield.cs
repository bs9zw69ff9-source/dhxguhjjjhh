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
/// The whole anti-DDoS table, beyond what the attack monitor needs: logging mode, the trusted
/// list, single-address blocks and the kernel's counters.
/// </summary>
public interface IAntiDdosFirewall : IFloodShield
{
    /// <summary>Whether this bot owns the table at all: it watches the interface and runs as root.</summary>
    bool Managed { get; }

    FirewallSettings Settings { get; }

    /// <summary>The logging mode the table is at now.</summary>
    LogMode Mode { get; }

    /// <summary>Whether the attack-time limits are on.</summary>
    bool Engaged { get; }

    /// <summary>Why the last change failed, or null when it applied.</summary>
    string? LastProblem { get; }

    /// <summary>New thresholds: validated, checked by nft, applied whole, or refused with the old rules kept.</summary>
    Task<string?> ConfigureAsync(FirewallSettings settings, CancellationToken ct);

    Task<string?> SetModeAsync(LogMode mode, CancellationToken ct);

    /// <summary>Make the trusted sets exactly these networks.</summary>
    Task<string?> SetTrustedAsync(IReadOnlyCollection<string> networks, CancellationToken ct);

    /// <summary>Block one address in the kernel for a while: the fallback when CrowdSec is not installed.</summary>
    Task<string?> BlockAsync(string address, int minutes, CancellationToken ct);

    Task<string?> UnblockAsync(string address, CancellationToken ct);

    Task<FirewallSnapshot?> SnapshotAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="IAntiDdosFirewall"/> over nftables, with the rules from <see cref="FloodShieldRules"/>.
/// </summary>
/// <remarks>
/// OWNED BY ONE BOT. The table is the host's, not the bot's; two bots managing it would undo each
/// other's changes. The bot with the DDoS monitor on (<c>ATTACK_CHANNEL</c>) owns it; only with
/// <c>ATTACK_MITIGATION=on</c> does it hold any drop rule.
///
/// CHECKED, THEN APPLIED, ATOMICALLY. Every change is first run through <c>nft -c</c>, which parses
/// and validates it against the kernel without changing anything, then loaded with <c>nft -f -</c>
/// as one transaction. A refused change leaves the rules that were already in force exactly as
/// they were - a failed update can never leave the box with half a firewall.
///
/// ONE CHANGE AT A TIME. The attack monitor engaging, an owner changing a threshold and the
/// storage guard lowering the log mode can all happen in the same second; they queue.
///
/// THE LAST GOOD POLICY IS SAVED to <see cref="BootScript"/> (limits off, logging no higher than
/// normal) so the boot unit can load it before the bot is up. Without that unit the table simply
/// comes back when the bot starts.
/// </remarks>
public sealed class FloodShield : IAntiDdosFirewall
{
    private static readonly TimeSpan NftTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Where the last good policy is kept for the boot unit (<c>anti-ddos-firewall.service</c>).</summary>
    public const string BootScript = "/var/lib/anti-ddos/firewall.nft";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<FloodShield> _logger;
    private readonly string? _bootScript;
    private IReadOnlyCollection<string> _trusted = [];

    public FloodShield(FirewallSettings settings, bool managed, ILogger<FloodShield> logger, string? bootScript = BootScript)
    {
        Settings = settings;
        Managed = managed;
        _logger = logger;
        _bootScript = bootScript;
    }

    public bool Managed { get; }

    public FirewallSettings Settings { get; private set; }

    public LogMode Mode { get; private set; } = LogMode.Normal;

    public bool Engaged { get; private set; }

    public string? LastProblem { get; private set; }

    /// <summary>Whether the attack-time limits may be engaged: the table is ours and mitigation is on.</summary>
    public bool Enabled => Managed && Settings.Enforce;

    public ShieldLimits Limits => Settings.Limits;

    public async Task<string?> ApplyAsync(bool engaged, CancellationToken ct)
    {
        if (ManagedRefusal() is { } refused) return refused;
        if (engaged && !Settings.Enforce) return "ATTACK_MITIGATION is off on this bot";

        var problem = await ApplyPolicyAsync(Settings, engaged, Mode, ct).ConfigureAwait(false);
        if (problem is null)
        {
            _logger.LogWarning(engaged
                ? "DDoS shield ENGAGED: over {PerSource} pkt/s from one address is blocked for {Minutes} min, unknown traffic to {Ports} capped at {Unknown} pkt/s"
                : "DDoS shield standing by: blocked addresses stay blocked, the attack-time limits are off ({PerSource}/{Minutes}/{Ports}/{Unknown})",
                Limits.PerSourcePps, Limits.BlockMinutes, Limits.GamePorts, Limits.UnknownPps);
        }
        return problem;
    }

    public Task<string?> ConfigureAsync(FirewallSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (ManagedRefusal() is { } refused) return Task.FromResult<string?>(refused);
        return ApplyPolicyAsync(settings, Engaged, Mode, ct);
    }

    public Task<string?> SetModeAsync(LogMode mode, CancellationToken ct)
    {
        if (ManagedRefusal() is { } refused) return Task.FromResult<string?>(refused);
        return ApplyPolicyAsync(Settings, Engaged, mode, ct);
    }

    public async Task<string?> SetTrustedAsync(IReadOnlyCollection<string> networks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(networks);
        if (ManagedRefusal() is { } refused) return refused;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var problem = await NftAsync(FloodShieldRules.TrustedScript(networks), ct).ConfigureAwait(false);
            if (problem is null)
            {
                _trusted = [.. networks];
                SaveBootScript();
            }
            return problem;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AllowAsync(IReadOnlySet<uint> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (ManagedRefusal() is not null || addresses.Count == 0) return;

        foreach (var script in FloodShieldRules.AllowScripts(addresses))
        {
            if (await NftAsync(script, ct).ConfigureAwait(false) is { } problem)
            {
                _logger.LogWarning("Could not add players to the DDoS allow list: {Problem}", problem);
                return;
            }
        }
    }

    public async Task<string?> BlockAsync(string address, int minutes, CancellationToken ct)
    {
        if (EnforceRefusal() is { } refused) return refused;
        if (FloodShieldRules.BlockScript(address, minutes) is not { } script) return $"\"{address}\" is not a single address that may be blocked";
        return await NftAsync(script, ct).ConfigureAwait(false);
    }

    public async Task<string?> UnblockAsync(string address, CancellationToken ct)
    {
        if (ManagedRefusal() is { } refused) return refused;
        if (FloodShieldRules.UnblockOneScript(address) is not { } script) return $"\"{address}\" is not a single address";
        return await NftAsync(script, ct).ConfigureAwait(false);
    }

    public async Task<ShieldStats?> StatsAsync(CancellationToken ct)
    {
        var run = await ListAsync(ct).ConfigureAwait(false);
        return run is null ? null : FloodShieldRules.ParseStats(run);
    }

    public async Task<FirewallSnapshot?> SnapshotAsync(CancellationToken ct)
    {
        var run = await ListAsync(ct).ConfigureAwait(false);
        return run is null ? null : FloodShieldRules.ParseTable(run);
    }

    public async Task<string?> UnblockAllAsync(CancellationToken ct)
    {
        if (ManagedRefusal() is { } refused) return refused;
        return await NftAsync(FloodShieldRules.UnblockScript(), ct).ConfigureAwait(false);
    }

    private async Task<string?> ListAsync(CancellationToken ct)
    {
        if (ManagedRefusal() is not null) return null;
        var run = await ProcessRunner.RunAsync("nft", ["-j", "list", "table", "inet", FloodShieldRules.Table], NftTimeout, _logger, ct)
            .ConfigureAwait(false);
        return run.Ok ? run.Stdout : null;
    }

    /// <summary>Check, then apply, the whole policy; on success it becomes the current one.</summary>
    private async Task<string?> ApplyPolicyAsync(FirewallSettings settings, bool engaged, LogMode mode, CancellationToken ct)
    {
        if (FloodShieldRules.Problem(settings) is { } invalid) return LastProblem = invalid;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var script = FloodShieldRules.Script(settings, engaged, mode);
            var problem = await NftAsync(script, ct, check: true).ConfigureAwait(false) ??
                          await NftAsync(script, ct).ConfigureAwait(false);
            LastProblem = problem;
            if (problem is not null)
            {
                _logger.LogError("Firewall change REFUSED, the previous rules stay in force: {Problem}", problem);
                return problem;
            }

            Settings = settings;
            Engaged = engaged;
            Mode = mode;
            SaveBootScript();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The policy the boot unit loads: attack limits off, logging no higher than normal, the
    /// trusted list as it is now. Written 0600, atomically.
    /// </summary>
    private void SaveBootScript()
    {
        if (_bootScript is null) return;
        try
        {
            var script = FloodShieldRules.Script(Settings, engaged: false, Mode > LogMode.Normal ? LogMode.Normal : Mode) +
                         FloodShieldRules.TrustedScript(_trusted);
            Directory.CreateDirectory(Path.GetDirectoryName(_bootScript)!);
            Storage.AtomicFile.Write(_bootScript, script);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_bootScript, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the boot copy of the firewall to {Path}; it still comes back when the bot starts", _bootScript);
        }
    }

    private string? ManagedRefusal() =>
        !Managed ? "this bot does not manage the anti-DDoS firewall (ATTACK_CHANNEL is not set here)"
        : !Environment.IsPrivilegedProcess ? "the bot is not running as root, so it cannot change the firewall"
        : null;

    private string? EnforceRefusal() =>
        ManagedRefusal() ?? (!Settings.Enforce ? "ATTACK_MITIGATION is off on this bot, so it holds no drop rules" : null);

    private async Task<string?> NftAsync(string script, CancellationToken ct, bool check = false)
    {
        var run = await ProcessRunner.RunAsync("nft", check ? ["-c", "-f", "-"] : ["-f", "-"], NftTimeout, _logger, ct, stdin: script)
            .ConfigureAwait(false);
        if (!run.Started) return "nft is not installed - run `/packettools install`";
        if (run.TimedOut) return "nft did not answer within 15 seconds";
        return run.ExitCode == 0 ? null : $"nft {(check ? "check failed" : "refused the rules")}: {run.Combined}";
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
