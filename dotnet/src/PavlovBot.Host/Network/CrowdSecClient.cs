using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Network;

/// <summary>CrowdSec, as far as the bot uses it: decisions, alerts, health, and its allow list.</summary>
public interface ICrowdSec
{
    /// <summary>Whether cscli is installed and enabled here. It may still fail to reach its local API.</summary>
    bool Installed { get; }

    Task<(IReadOnlyList<CrowdSecDecision>? Decisions, string? Problem)> DecisionsAsync(CancellationToken ct);

    Task<IReadOnlyList<CrowdSecAlert>?> AlertsAsync(TimeSpan since, string? address, CancellationToken ct);

    Task<IReadOnlyList<CrowdSecBouncer>?> BouncersAsync(CancellationToken ct);

    Task<IReadOnlyList<CrowdSecSource>?> AcquisitionAsync(CancellationToken ct);

    /// <summary>Ban an address or range. Null on success, else why not.</summary>
    Task<string?> BanAsync(IpNetworks.Network target, TimeSpan duration, string reason, CancellationToken ct);

    /// <summary>Lift every decision on an address or range. Null on success, else why not.</summary>
    Task<string?> UnbanAsync(IpNetworks.Network target, CancellationToken ct);

    /// <summary>Put a network on the bot's allowlist (which also removes its existing decisions), or take it off.</summary>
    Task<string?> AllowAsync(IpNetworks.Network target, string comment, CancellationToken ct);

    Task<string?> DisallowAsync(IpNetworks.Network target, CancellationToken ct);
}

/// <summary>
/// <see cref="ICrowdSec"/> over <c>cscli</c>.
/// </summary>
/// <remarks>
/// CSCLI, NOT THE LOCAL API. cscli is CrowdSec's own supported interface, it authenticates with
/// the machine credentials CrowdSec installed, and the bot needs no API key of its own - so there
/// is no secret to keep in <c>.env</c>.
///
/// ARGV ONLY. Every value is canonical (<see cref="IpNetworks"/>) or a constant before it gets
/// here; reasons are passed as one argument, so nothing typed in Discord can become a flag or a
/// second command.
///
/// THE ALLOWLIST NEEDS CROWDSEC 1.6.8 OR LATER (centralized allowlists). On an older version
/// <see cref="AllowAsync"/> says so; trusted addresses are still exempt from the bot's own limits.
/// </remarks>
public sealed class CrowdSecClient(bool enabled, ILogger<CrowdSecClient> logger, TimeProvider time) : ICrowdSec
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private bool? _installed;
    private bool _allowlistReady;

    public bool Installed => _installed ??= enabled && Environment.IsPrivilegedProcess && Which("cscli");

    public async Task<(IReadOnlyList<CrowdSecDecision>? Decisions, string? Problem)> DecisionsAsync(CancellationToken ct)
    {
        var run = await RunAsync(["decisions", "list", "-o", "json"], ct).ConfigureAwait(false);
        return run.Problem is { } problem ? (null, problem) : (CrowdSecOutput.Decisions(run.Output, time.GetUtcNow()), null);
    }

    public async Task<IReadOnlyList<CrowdSecAlert>?> AlertsAsync(TimeSpan since, string? address, CancellationToken ct)
    {
        List<string> argv = ["alerts", "list", "-o", "json", "--since", CrowdSecOutput.DurationArgument(since), "-l", "0"];
        if (address is not null) argv.AddRange(["--ip", address]);
        var run = await RunAsync(argv, ct).ConfigureAwait(false);
        return run.Problem is null ? CrowdSecOutput.Alerts(run.Output) : null;
    }

    public async Task<IReadOnlyList<CrowdSecBouncer>?> BouncersAsync(CancellationToken ct)
    {
        var run = await RunAsync(["bouncers", "list", "-o", "json"], ct).ConfigureAwait(false);
        return run.Problem is null ? CrowdSecOutput.Bouncers(run.Output) : null;
    }

    public async Task<IReadOnlyList<CrowdSecSource>?> AcquisitionAsync(CancellationToken ct)
    {
        var run = await RunAsync(["metrics", "show", "acquisition", "-o", "json"], ct).ConfigureAwait(false);
        return run.Problem is null ? CrowdSecOutput.Acquisition(run.Output) : null;
    }

    public async Task<string?> BanAsync(IpNetworks.Network target, TimeSpan duration, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        List<string> argv =
        [
            "decisions", "add", target.IsSingle ? "--ip" : "--range", target.Text,
            "--duration", CrowdSecOutput.DurationArgument(duration), "--type", "ban",
            "--reason", CrowdSecOutput.BotReason + " " + Clean(reason),
        ];
        return (await RunAsync(argv, ct).ConfigureAwait(false)).Problem;
    }

    public async Task<string?> UnbanAsync(IpNetworks.Network target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        return (await RunAsync(["decisions", "delete", target.IsSingle ? "--ip" : "--range", target.Text], ct).ConfigureAwait(false)).Problem;
    }

    public async Task<string?> AllowAsync(IpNetworks.Network target, string comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (await EnsureAllowlistAsync(ct).ConfigureAwait(false) is { } problem) return problem;
        return (await RunAsync(["allowlists", "add", CrowdSecOutput.Allowlist, target.Text, "-d", Clean(comment)], ct).ConfigureAwait(false)).Problem;
    }

    public async Task<string?> DisallowAsync(IpNetworks.Network target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (await EnsureAllowlistAsync(ct).ConfigureAwait(false) is { } problem) return problem;
        return (await RunAsync(["allowlists", "remove", CrowdSecOutput.Allowlist, target.Text], ct).ConfigureAwait(false)).Problem;
    }

    private async Task<string?> EnsureAllowlistAsync(CancellationToken ct)
    {
        if (_allowlistReady) return null;
        var inspect = await RunAsync(["allowlists", "inspect", CrowdSecOutput.Allowlist, "-o", "json"], ct).ConfigureAwait(false);
        if (inspect.Problem is null)
        {
            _allowlistReady = true;
            return null;
        }
        if (inspect.Problem.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
            return "this CrowdSec has no allowlists (they need 1.6.8 or later) - trusted addresses are exempt from the bot's limits only";

        var create = await RunAsync(["allowlists", "create", CrowdSecOutput.Allowlist, "-d", "Trusted addresses managed by the Pavlov bot"], ct)
            .ConfigureAwait(false);
        _allowlistReady = create.Problem is null;
        return create.Problem;
    }

    private async Task<(string? Output, string? Problem)> RunAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        if (!Installed) return (null, enabled ? "CrowdSec is not installed - see ANTIDDOS.md" : "ANTIDDOS_CROWDSEC is off");

        var run = await ProcessRunner.RunAsync("cscli", [.. argv, "--color", "no"], Timeout, logger, ct).ConfigureAwait(false);
        if (!run.Started)
        {
            _installed = false;
            return (null, "cscli is not installed");
        }
        if (run.TimedOut) return (null, "cscli did not answer within 20 seconds - is crowdsec running? `systemctl status crowdsec`");
        if (run.ExitCode != 0)
        {
            var message = run.Stderr.Trim() is { Length: > 0 } e ? e : run.Stdout.Trim();
            logger.LogWarning("cscli {Command} failed ({Exit}): {Message}", argv[0] + " " + argv[1], run.ExitCode, message);
            return (null, message.Length > 300 ? message[..300] + "…" : message);
        }
        return (run.Stdout, null);
    }

    /// <summary>A reason or comment as one plain line: no control characters, bounded length.</summary>
    internal static string Clean(string text)
    {
        var plain = new string([.. (text ?? "").Where(c => !char.IsControl(c))]).Trim();
        return plain.Length > 200 ? plain[..200] : plain;
    }

    private static bool Which(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/sbin:/usr/bin:/sbin:/bin")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));
}
