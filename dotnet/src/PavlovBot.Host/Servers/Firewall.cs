using System.Net;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Servers;

/// <summary>The outcome of one firewall change. <see cref="Detail"/> is written for a human.</summary>
public sealed record FirewallResult(bool Ok, string Detail);

/// <summary>
/// An OS packet filter the bot can add and remove single-address deny rules on.
/// </summary>
/// <remarks>
/// An interface so the moderation layer can drive the firewall without taking a dependency on
/// how it is enforced, and so a test can assert what would be blocked without touching the
/// host's real firewall. The only implementation is <see cref="UfwFirewall"/>.
/// </remarks>
public interface IFirewall
{
    /// <summary>Deny all traffic from an address.</summary>
    Task<FirewallResult> DenyAsync(string ip, CancellationToken ct = default);

    /// <summary>Remove a deny rule for an address, if one exists.</summary>
    Task<FirewallResult> UndenyAsync(string ip, CancellationToken ct = default);
}

/// <summary>
/// The real firewall, driven through <c>ufw</c>.
/// </summary>
/// <remarks>
/// SAME DISCIPLINE AS <c>/firewall</c>, for the same reason. The address is PARSED, never
/// pattern-matched, so only something <see cref="IPAddress"/> accepts is ever passed on; the
/// arguments go as an ARGV ARRAY through <see cref="ProcessRunner"/>, so there is no shell to
/// inject into even if that validation were wrong; and the command is bounded, so a ufw that
/// wedges cannot hold a caller open or be left running once we stop waiting.
///
/// NON-FATAL BY CONTRACT. A missing ufw, a bot not running as root, or a rule that already
/// exists all come back as a <see cref="FirewallResult"/>, never an exception - the caller is
/// applying a blacklist, and the flag write is the part that must not be lost to a firewall
/// that could not be reached.
/// </remarks>
public sealed class UfwFirewall(ILogger<UfwFirewall> logger) : IFirewall
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public Task<FirewallResult> DenyAsync(string ip, CancellationToken ct = default) =>
        Run(ip, canonical => ["deny", "from", canonical], ct);

    public Task<FirewallResult> UndenyAsync(string ip, CancellationToken ct = default) =>
        Run(ip, canonical => ["delete", "deny", "from", canonical], ct);

    private async Task<FirewallResult> Run(string ip, Func<string, string[]> argv, CancellationToken ct)
    {
        if (!IPAddress.TryParse((ip ?? "").Trim(), out var address))
            return new FirewallResult(false, $"not an address: {ip}");

        try
        {
            var run = await ProcessRunner.RunAsync("ufw", argv(address.ToString()), Timeout, logger, ct).ConfigureAwait(false);

            if (!run.Started) return new FirewallResult(false, "ufw is not installed or not on PATH");
            if (run.TimedOut) return new FirewallResult(false, "ufw did not answer within 10s and was stopped");

            var output = run.Combined.Length > 0 ? run.Combined : "(no output)";
            return new FirewallResult(run.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ufw invocation failed");
            return new FirewallResult(false, ex.Message);
        }
    }
}
