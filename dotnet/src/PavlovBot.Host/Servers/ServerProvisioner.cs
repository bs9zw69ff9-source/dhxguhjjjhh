using Microsoft.Extensions.Logging;
using PavlovBot.Core.Provisioning;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon;

namespace PavlovBot.Host.Servers;

/// <summary>
/// The real, privileged provisioner: SteamCMD, config, systemd, ufw, then wire-in and restart.
/// </summary>
/// <remarks>
/// A SIBLING of <see cref="ServiceControl"/>, not an extension of it. ServiceControl's whole
/// safety story is that it only ever start/stop/restarts units that are ALREADY configured, by
/// index, with an allow-listed name - a minimal surface with tests to match. Creating an
/// install, writing a unit file and enabling a service is a categorically larger capability, so
/// it lives here rather than widening that surface.
///
/// EVERYTHING PRIVILEGED GOES THROUGH <see cref="ProcessRunner"/> with an argv array and a
/// bounded timeout, so there is no shell to inject into. The unit name is validated by
/// <see cref="ServiceControl.IsPlausibleUnitName"/> before it can reach a command line. The one
/// thing this cannot design around is that it needs to be ROOT: writing
/// <c>/etc/systemd/system</c>, <c>enable</c>, running SteamCMD as <c>steam</c> and <c>ufw</c>
/// are not covered by ServiceControl's tiny sudoers line, so a non-root bot is refused up front
/// rather than left to fail obscurely halfway through.
/// </remarks>
public sealed class ServerProvisioner(ILogger<ServerProvisioner> logger) : IServerProvisioner
{
    /// <summary>SteamCMD's app id for the Pavlov VR dedicated server.</summary>
    private const string PavlovAppId = "622970";

    /// <summary>The OS user the game server runs as and owns its files.</summary>
    private const string SteamUser = "steam";

    // Generous by necessity: SteamCMD pulls several GB on a first install.
    private static readonly TimeSpan SteamCmdTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The ordered step labels, so the checklist reads the same every run.</summary>
    private static readonly string[] StepNames =
    [
        "Pre-flight checks",
        "SteamCMD install",
        "Server config (RconSettings.txt, Game.ini)",
        "systemd unit (write, daemon-reload, enable --now)",
        "Firewall (ufw)",
        "RCON reachability",
        "Wire into the bot (.env)",
        "Restart the bot",
    ];

    /// <summary>
    /// The SteamCMD argument list, verbatim. Extracted so the exact command line - the app id
    /// above all - is pinned by a test rather than discovered against a live Steam.
    /// </summary>
    internal static IReadOnlyList<string> SteamCmdArgv(string installDir) =>
        ["+force_install_dir", installDir, "+login", "anonymous",
         "+app_update", PavlovAppId, "-beta", "default", "+quit"];

    /// <summary><c>systemctl enable --now &lt;unit&gt;</c>, as an argv array.</summary>
    internal static IReadOnlyList<string> EnableArgv(string unit) => ["enable", "--now", unit];

    /// <summary>A ufw port rule, e.g. <c>7777/udp</c>.</summary>
    internal static string UfwRule(int port, string proto) =>
        $"{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{proto}";

    public async Task<ProvisionOutcome> ProvisionAsync(
        ProvisionRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onProgress);

        var run = new Run(StepNames, onProgress);
        var spec = request.Spec;
        var unitPath = $"/etc/systemd/system/{spec.UnitName}.service";

        // ---- 0: pre-flight ----
        await run.Start(0, "checking privileges, the steam user and free slots…").ConfigureAwait(false);
        if (await PreflightAsync(spec, unitPath, ct).ConfigureAwait(false) is { } preflightProblem)
        {
            await run.Fail(0, preflightProblem).ConfigureAwait(false);
            return await run.Abort(1).ConfigureAwait(false);
        }
        await run.Ok(0, "root, steam user present, slot free.").ConfigureAwait(false);

        // ---- 1: SteamCMD ----
        await run.Start(1, "downloading the dedicated server (several GB - this is slow)…").ConfigureAwait(false);
        await RunAsync(SteamUser, "mkdir", ["-p", spec.InstallDir], QuickTimeout, ct).ConfigureAwait(false);
        var steam = await RunAsync(SteamUser, "steamcmd", SteamCmdArgv(spec.InstallDir), SteamCmdTimeout, ct).ConfigureAwait(false);
        if (!steam.Started)
        {
            await run.Fail(1, "could not start SteamCMD - is it installed and on PATH?").ConfigureAwait(false);
            return await run.Abort(2).ConfigureAwait(false);
        }
        if (steam.TimedOut)
        {
            await run.Fail(1, $"SteamCMD did not finish within {SteamCmdTimeout.TotalMinutes:0} minutes.").ConfigureAwait(false);
            return await run.Abort(2).ConfigureAwait(false);
        }
        if (steam.ExitCode != 0)
        {
            await run.Fail(1, $"SteamCMD exited {steam.ExitCode}: {Short(steam.Combined)}").ConfigureAwait(false);
            return await run.Abort(2).ConfigureAwait(false);
        }
        await run.Ok(1, "installed.").ConfigureAwait(false);

        // ---- 2: config files ----
        await run.Start(2, "writing RconSettings.txt and Game.ini…").ConfigureAwait(false);
        if (await WriteConfigAsync(spec, ct).ConfigureAwait(false) is { } configProblem)
        {
            await run.Fail(2, configProblem).ConfigureAwait(false);
            return await run.Abort(3).ConfigureAwait(false);
        }
        await run.Ok(2, "written and owned by steam.").ConfigureAwait(false);

        // ---- 3: systemd unit ----
        await run.Start(3, "installing and enabling the service…").ConfigureAwait(false);
        if (await InstallUnitAsync(spec, unitPath, ct).ConfigureAwait(false) is { } unitProblem)
        {
            await run.Fail(3, unitProblem).ConfigureAwait(false);
            return await run.Abort(4).ConfigureAwait(false);
        }
        await run.Ok(3, $"{spec.UnitName} enabled and started.").ConfigureAwait(false);

        // ---- 4: firewall (non-critical) ----
        await run.Start(4, "opening the game, query and RCON ports…").ConfigureAwait(false);
        var ufw = await OpenPortsAsync(spec, ct).ConfigureAwait(false);
        if (ufw is null) await run.Ok(4, $"allowed {spec.GamePort}/udp, {spec.QueryPort}/udp, {spec.RconPort}/tcp.").ConfigureAwait(false);
        else await run.SoftFail(4, ufw).ConfigureAwait(false);

        // ---- 5: RCON reachability (non-critical) ----
        await run.Start(5, "asking the new server for ServerInfo…").ConfigureAwait(false);
        if (await RconReachableAsync(spec, ct).ConfigureAwait(false))
            await run.Ok(5, "answered RCON.").ConfigureAwait(false);
        else
            await run.SoftFail(5, "no RCON answer yet - the server may still be starting; check /health after a minute.").ConfigureAwait(false);

        // ---- 6: wire into the bot's .env (only reached when every critical step passed) ----
        await run.Start(6, "appending the server to .env…").ConfigureAwait(false);
        if (await AppendEnvAsync(request, ct).ConfigureAwait(false) is { } envProblem)
        {
            await run.Fail(6, envProblem).ConfigureAwait(false);
            return await run.Abort(7).ConfigureAwait(false);
        }
        await run.Ok(6, $"added as server {spec.Slot}.").ConfigureAwait(false);

        // ---- 7: restart the bot ----
        return await RestartAsync(run, spec.Slot, ct).ConfigureAwait(false);
    }

    /// <summary>Every reason the run must not start, or null when it may.</summary>
    private async Task<string?> PreflightAsync(ServerProvisionSpec spec, string unitPath, CancellationToken ct)
    {
        if (!Environment.IsPrivilegedProcess)
            return "the bot is not running as root, so it cannot write a systemd unit, enable a service or run SteamCMD as steam. " +
                   "Run the bot as root for this command (its other privileged actions use a narrow sudoers line; this one does not fit that).";

        if (!ServiceControl.IsPlausibleUnitName(spec.UnitName))
            return $"\"{spec.UnitName}\" is not a usable systemd unit name.";

        var id = await RunAsync(null, "id", [SteamUser], QuickTimeout, ct).ConfigureAwait(false);
        if (!id.Ok)
            return $"the \"{SteamUser}\" user does not exist on this box - create it before provisioning a server it will own.";

        if (File.Exists(unitPath))
            return $"{unitPath} already exists - refusing to overwrite an existing unit.";

        try
        {
            if (Directory.Exists(spec.InstallDir) && Directory.EnumerateFileSystemEntries(spec.InstallDir).Any())
                return $"{spec.InstallDir} already exists and is not empty - refusing to install over it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not inspect {spec.InstallDir}: {ex.Message}";
        }

        return null;
    }

    /// <summary>Write the two config files and hand them to steam. Null on success.</summary>
    private async Task<string?> WriteConfigAsync(ServerProvisionSpec spec, CancellationToken ct)
    {
        var configDir = Path.Combine(spec.InstallDir, "Pavlov", "Saved", "Config");
        var linuxDir = Path.Combine(configDir, "LinuxServer");

        var mk = await RunAsync(SteamUser, "mkdir", ["-p", linuxDir], QuickTimeout, ct).ConfigureAwait(false);
        if (!mk.Ok) return $"could not create {linuxDir}: {Short(mk.Combined)}";

        try
        {
            var rconPath = Path.Combine(configDir, "RconSettings.txt");
            await AtomicFile.WriteAsync(rconPath, ProvisionText.RconSettings(spec.RconPassword, spec.RconPort), ct).ConfigureAwait(false);

            var gameIniPath = Path.Combine(linuxDir, "Game.ini");
            await AtomicFile.WriteAsync(gameIniPath, ProvisionText.GameIni(spec), ct).ConfigureAwait(false);

            // Written by root into a steam-owned tree; hand ownership back so the server reads them.
            foreach (var path in new[] { rconPath, gameIniPath })
            {
                var chown = await RunAsync(null, "chown", [SteamUser, path], QuickTimeout, ct).ConfigureAwait(false);
                if (!chown.Ok) return $"could not chown {path} to {SteamUser}: {Short(chown.Combined)}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>Write the unit, reload systemd, enable and start it. Null on success.</summary>
    private async Task<string?> InstallUnitAsync(ServerProvisionSpec spec, string unitPath, CancellationToken ct)
    {
        try
        {
            await AtomicFile.WriteAsync(unitPath, ProvisionText.SystemdUnit(spec), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not write {unitPath}: {ex.Message}";
        }

        var reload = await RunAsync(null, "systemctl", ["daemon-reload"], SystemctlTimeout, ct).ConfigureAwait(false);
        if (!reload.Ok) return $"systemctl daemon-reload failed: {Short(reload.Combined)}";

        var enable = await RunAsync(null, "systemctl", EnableArgv(spec.UnitName), SystemctlTimeout, ct).ConfigureAwait(false);
        if (!enable.Ok) return $"systemctl enable --now {spec.UnitName} failed: {Short(enable.Combined)}";

        return null;
    }

    /// <summary>Open the three ports. Null on success, else a note (non-fatal).</summary>
    private async Task<string?> OpenPortsAsync(ServerProvisionSpec spec, CancellationToken ct)
    {
        (int Port, string Proto)[] rules =
        [
            (spec.GamePort, "udp"),
            (spec.QueryPort, "udp"),
            (spec.RconPort, "tcp"),
        ];

        foreach (var (port, proto) in rules)
        {
            var run = await RunAsync(null, "ufw", ["allow", UfwRule(port, proto)], QuickTimeout, ct).ConfigureAwait(false);
            if (!run.Started) return "ufw is not installed or not on PATH - open the ports yourself.";
            if (!run.Ok) return $"ufw allow {UfwRule(port, proto)} did not succeed: {Short(run.Combined)}";
        }

        return null;
    }

    /// <summary>Whether the new server answers RCON on loopback.</summary>
    private async Task<bool> RconReachableAsync(ServerProvisionSpec spec, CancellationToken ct)
    {
        // A throwaway client - the registry is fixed at startup and cannot hold a server that
        // does not exist yet. One attempt, short timeout: this is a smoke test, not a wait.
        var options = new RconOptions
        {
            Host = "127.0.0.1",
            Port = spec.RconPort,
            Password = spec.RconPassword,
            Name = $"provision-{spec.Slot}",
            MaxAttempts = 1,
            CommandTimeout = TimeSpan.FromSeconds(3),
        };

        await using var client = new RconClient(options);
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(8));
            var reply = await client.SendAsync("ServerInfo", probe.Token).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "New server {Slot} did not answer RCON on its first probe", spec.Slot);
            return false;
        }
    }

    /// <summary>Append the override block to .env. Null on success.</summary>
    private async Task<string?> AppendEnvAsync(ProvisionRequest request, CancellationToken ct)
    {
        try
        {
            var existing = File.Exists(request.EnvPath)
                ? await File.ReadAllTextAsync(request.EnvPath, ct).ConfigureAwait(false)
                : "";

            var block = ProvisionText.EnvOverrideBlock(
                request.Spec, request.FinalPavlovUnits, request.FinalPavlovBases,
                request.FinalPlayerCountChannels, DateOnly.FromDateTime(DateTime.UtcNow));

            await AtomicFile.WriteAsync(request.EnvPath, existing + block, ct).ConfigureAwait(false);
            logger.LogWarning("Wrote server {Slot} into {Path}", request.Spec.Slot, request.EnvPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not update {request.EnvPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Restart the bot so it picks up the new server, reporting BEFORE the process can die.
    /// </summary>
    /// <remarks>
    /// The channel checklist is completed and flushed first, because a pm2 restart sends this
    /// process SIGTERM and nothing after that is guaranteed to run. Only pm2-managed deployments
    /// can be restarted safely from inside; anywhere else this reports the manual step instead of
    /// killing a process nothing will bring back.
    /// </remarks>
    private async Task<ProvisionOutcome> RestartAsync(Run run, int slot, CancellationToken ct)
    {
        var appName = Environment.GetEnvironmentVariable("PAVLOV_PM2_APP")
                      ?? Environment.GetEnvironmentVariable("name")
                      ?? "pavlov-bot-cs";
        var underPm2 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("pm_id"));

        if (!underPm2)
        {
            await run.Ok(7, $"not running under pm2 - restart the bot yourself to bring server {slot} online.").ConfigureAwait(false);
            return run.Finish(restartQueued: false);
        }

        // Announce and FLUSH the final state before we trigger our own SIGTERM.
        await run.Start(7, $"restarting `{appName}` now to bring server {slot} online…").ConfigureAwait(false);
        var outcome = run.Finish(restartQueued: true);

        logger.LogWarning("Provision complete; restarting {App} via pm2 to load server {Slot}", appName, slot);
        _ = RunAsync(null, "pm2", ["restart", appName], QuickTimeout, ct);
        return outcome;
    }

    /// <summary>
    /// One external command, as root or dropped to another user, always through ProcessRunner.
    /// </summary>
    /// <param name="asUser">Run as this user via <c>sudo -u</c>; null runs it as the bot (root here).</param>
    private async Task<ProcessOutcome> RunAsync(
        string? asUser, string file, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        // No shell anywhere: argv stays an array through sudo too, so there is nothing to inject
        // into even when a value is interpolated into a path above.
        var (exe, args) = asUser is null
            ? (file, argv)
            : ("sudo", (IReadOnlyList<string>)["-u", asUser, file, .. argv]);

        try
        {
            return await ProcessRunner.RunAsync(exe, args, timeout, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not run {File}", file);
            return ProcessOutcome.NotStarted;
        }
    }

    private static string Short(string text) => text.Length <= 300 ? text : text[..300] + "…";

    /// <summary>
    /// The mutable checklist for one run: holds the steps and pushes a snapshot on every change.
    /// </summary>
    private sealed class Run(IReadOnlyList<string> names, Func<IReadOnlyList<ProvisionStep>, Task> onProgress)
    {
        private readonly ProvisionStep[] _steps =
            [.. names.Select(n => new ProvisionStep(n, ProvisionStatus.Pending, ""))];

        public Task Start(int i, string detail) => Set(i, ProvisionStatus.Running, detail);
        public Task Ok(int i, string detail) => Set(i, ProvisionStatus.Ok, detail);
        public Task Fail(int i, string detail) => Set(i, ProvisionStatus.Failed, detail);
        public Task SoftFail(int i, string detail) => Set(i, ProvisionStatus.Failed, detail);

        /// <summary>Mark every step from <paramref name="from"/> onward Skipped and finish.</summary>
        public async Task<ProvisionOutcome> Abort(int from)
        {
            for (var i = from; i < _steps.Length; i++)
                _steps[i] = _steps[i] with { Status = ProvisionStatus.Skipped, Detail = "skipped - an earlier step failed" };
            await Emit().ConfigureAwait(false);
            return Finish(restartQueued: false);
        }

        public ProvisionOutcome Finish(bool restartQueued) => new([.. _steps], restartQueued);

        private async Task Set(int i, ProvisionStatus status, string detail)
        {
            _steps[i] = _steps[i] with { Status = status, Detail = detail };
            await Emit().ConfigureAwait(false);
        }

        private async Task Emit()
        {
            try
            {
                await onProgress([.. _steps]).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Progress reporting is best-effort; a failed channel edit must not abort a
                // provision that is otherwise working.
            }
        }
    }
}
