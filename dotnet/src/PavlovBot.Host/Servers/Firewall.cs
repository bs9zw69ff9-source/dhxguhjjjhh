using System.Net;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Servers;

/// <summary>The outcome of one firewall change. <see cref="Detail"/> is written for a human.</summary>
public sealed record FirewallResult(bool Ok, string Detail);

/// <summary>
/// An OS packet filter the bot can add and remove single-address deny rules on.
/// </summary>
/// <remarks>
/// An interface so the moderation layer and <c>/firewall</c> can drive the firewall without
/// depending on how it is enforced, and so a test can assert what would be blocked without
/// touching the host's real firewall. The only implementation is <see cref="UfwFirewall"/>.
/// </remarks>
public interface IFirewall
{
    /// <summary>Deny all traffic from an address, ahead of any allow rule.</summary>
    Task<FirewallResult> DenyAsync(string ip, CancellationToken ct = default);

    /// <summary>Remove a deny rule for an address, if one exists.</summary>
    Task<FirewallResult> UndenyAsync(string ip, CancellationToken ct = default);

    /// <summary>The current rule table, for reporting. <see cref="FirewallResult.Detail"/> is the text.</summary>
    Task<FirewallResult> StatusAsync(CancellationToken ct = default);
}

/// <summary>
/// The real firewall, driven through <c>ufw</c>.
/// </summary>
/// <remarks>
/// A DENY IS INSERTED AT RULE 1, NOT APPENDED, and that is the whole correctness of this. ufw
/// evaluates rules top to bottom and takes the FIRST match, and the provisioner has already
/// added <c>allow &lt;gameport&gt;</c> rules. A deny added at the end sits after those, so the
/// allow above it matches first and the "blocked" address connects anyway - which is exactly
/// the bug this fixes. Rule 1 puts the deny ahead of every allow.
///
/// SAME DISCIPLINE AS THE REST OF THE HOST LAYER. The address is PARSED, never pattern-matched,
/// so only something <see cref="IPAddress"/> accepts is ever passed on; the arguments go as an
/// ARGV ARRAY through <see cref="ProcessRunner"/>, so there is no shell to inject into even if
/// that validation were wrong; and the command is bounded, so a ufw that wedges cannot hold a
/// caller open or be left running once we stop waiting.
///
/// NON-FATAL BY CONTRACT. A missing ufw, a bot not running as root, or a rule that already
/// exists all come back as a <see cref="FirewallResult"/>, never an exception.
/// </remarks>
public sealed class UfwFirewall(ILogger<UfwFirewall> logger) : IFirewall
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Insert the deny at position 1, ahead of the allow rules. See the class remarks.</summary>
    internal static string[] InsertDenyArgv(string canonicalIp) => ["insert", "1", "deny", "from", canonicalIp];

    /// <summary>Plain append, used only when there are no rules to sit in front of.</summary>
    internal static string[] AppendDenyArgv(string canonicalIp) => ["deny", "from", canonicalIp];

    /// <summary>Delete by rule specification, which matches wherever the rule sits.</summary>
    internal static string[] DeleteDenyArgv(string canonicalIp) => ["delete", "deny", "from", canonicalIp];

    public async Task<FirewallResult> DenyAsync(string ip, CancellationToken ct = default)
    {
        if (!Canonical(ip, out var canonical)) return NotAnAddress(ip);

        var inserted = await RunUfw(InsertDenyArgv(canonical), ct).ConfigureAwait(false);
        if (inserted.Ok) return inserted;

        /* `insert 1` fails on an EMPTY rule set ("Invalid position 1"). With no allow rules to
           sit in front of, a plain append is equivalent - so fall back to it rather than
           reporting a failure the owner cannot act on. If ufw is simply absent or unprivileged,
           the append fails the same way and that real reason is what comes back. */
        return await RunUfw(AppendDenyArgv(canonical), ct).ConfigureAwait(false);
    }

    public async Task<FirewallResult> UndenyAsync(string ip, CancellationToken ct = default)
    {
        if (!Canonical(ip, out var canonical)) return NotAnAddress(ip);
        return await RunUfw(DeleteDenyArgv(canonical), ct).ConfigureAwait(false);
    }

    public Task<FirewallResult> StatusAsync(CancellationToken ct = default) =>
        RunUfw(["status", "numbered"], ct);

    private static bool Canonical(string? ip, out string canonical)
    {
        if (IPAddress.TryParse((ip ?? "").Trim(), out var address))
        {
            canonical = address.ToString();
            return true;
        }
        canonical = "";
        return false;
    }

    private static FirewallResult NotAnAddress(string? ip) => new(false, $"not an address: {ip}");

    private async Task<FirewallResult> RunUfw(string[] argv, CancellationToken ct)
    {
        try
        {
            var run = await ProcessRunner.RunAsync("ufw", argv, Timeout, logger, ct).ConfigureAwait(false);

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
