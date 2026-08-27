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
/// <c>/etc/systemd/system</c>, <c>enable</c>, creating the <c>steam</c> OS account, running
/// SteamCMD as it and <c>ufw</c> are not covered by ServiceControl's tiny sudoers line, so a
/// non-root bot is refused up front rather than left to fail obscurely halfway through.
///
/// ONE STEP HERE - granting <c>steam</c> full sudo - IS DELIBERATELY BROAD, on explicit operator
/// instruction, and is the opposite of the least-privilege pattern everything else follows. See
/// <see cref="GrantFullSudoAsync"/> for why that is dangerous and why it happens anyway.
/// </remarks>
public sealed class ServerProvisioner(ILogger<ServerProvisioner> logger) : IServerProvisioner
{
    /// <summary>SteamCMD's app id for the Pavlov VR dedicated server.</summary>
    private const string PavlovAppId = "622970";

    /// <summary>The OS user the game server runs as and owns its files.</summary>
    private const string SteamUser = "steam";

    /// <summary>
    /// The sudoers drop-in this installs. NO DOT, NO TRAILING '~' - sudo's default
    /// <c>#includedir /etc/sudoers.d</c> silently SKIPS a filename shaped like either (they read
    /// as an editor backup or a package-manager artifact), which would leave this step reporting
    /// success while granting nothing.
    /// </summary>
    private const string SudoersDropIn = "/etc/sudoers.d/pavlov-steam-full";

    // Generous by necessity: SteamCMD pulls several GB on a first install.
    private static readonly TimeSpan SteamCmdTimeout = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The ordered step labels, so the checklist reads the same every run. Internal so a test can
    /// pin the count and order directly, the same list <see cref="ProvisionAsync"/> actually
    /// drives through - the site of a real, shipped off-by-one already.
    /// </summary>
    internal static readonly string[] StepNames =
    [
        "Pre-flight checks",
        "Steam user account",
        "Steam sudo access (full, NOPASSWD)",
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

    /// <summary><c>useradd -m -s /bin/bash &lt;user&gt;</c>: a real home directory, for SteamCMD's cache.</summary>
    internal static IReadOnlyList<string> UseraddArgv(string user) => ["-m", "-s", "/bin/bash", user];

    /// <summary>
    /// The <c>user:password</c> line <c>chpasswd</c> reads from stdin, newline-terminated.
    /// </summary>
    /// <remarks>
    /// STDIN, NOT AN ARGUMENT. A password passed on a command line sits in that process's argv
    /// for as long as it runs, readable by anyone on the box via <c>ps</c> or <c>/proc</c>; stdin
    /// leaves nothing there. This is the one place a colon in the password would be read as the
    /// field separator and corrupt the line, which is why the account password is always
    /// GENERATED here from the same alphanumeric charset as the RCON one rather than accepted
    /// as free text.
    /// </remarks>
    internal static string ChpasswdStdin(string user, string password) => $"{user}:{password}\n";

    /// <summary>
    /// The full-access sudoers line, verbatim. <c>ALL=(ALL) NOPASSWD: ALL</c> is unrestricted -
    /// every command, as every user, no password - which is the whole point of this method's
    /// warnings: there is no narrower argument list to point to, because there isn't one here.
    /// </summary>
    internal static string SudoersFullAccessLine(string user) => $"{user} ALL=(ALL) NOPASSWD: ALL\n";

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

        /* STEPS ADVANCE THEMSELVES. Nothing below names its own position in StepNames - Start()
           moves to the next pending one. Inserting or reordering a step used to mean renumbering
           every call after it by hand, and that is exactly the bug (RestartAsync referencing a
           stale index) that shipped once already; a cursor makes the mistake impossible instead
           of relying on getting the renumbering right the next time too. */

        // ---- pre-flight ----
        await run.Start("checking privileges and free slots…").ConfigureAwait(false);
        if (Preflight(spec, unitPath) is { } preflightProblem)
        {
            await run.Fail(preflightProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok("root, unit name and slot are usable.").ConfigureAwait(false);

        // ---- the steam OS account - created here, REQUIRED to get a password if it is new ----
        await run.Start("checking for the steam account…").ConfigureAwait(false);
        var (userProblem, created) = await EnsureSteamUserAsync(request.SteamUserPassword, ct).ConfigureAwait(false);
        if (userProblem is not null)
        {
            await run.Fail(userProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok(created
            ? "created, with the generated password from your ephemeral reply."
            : "already existed - left untouched.").ConfigureAwait(false);

        // ---- steam's sudo access - see GrantFullSudoAsync for why this is deliberately broad ----
        await run.Start("installing its sudoers file…").ConfigureAwait(false);
        if (await GrantFullSudoAsync(ct).ConfigureAwait(false) is { } sudoProblem)
        {
            await run.Fail(sudoProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok($"{SudoersDropIn} installed - steam now has full, passwordless sudo.").ConfigureAwait(false);

        // ---- SteamCMD ----
        await run.Start("downloading the dedicated server (several GB - this is slow)…").ConfigureAwait(false);
        await RunAsync(SteamUser, "mkdir", ["-p", spec.InstallDir], QuickTimeout, ct).ConfigureAwait(false);
        var steam = await RunAsync(SteamUser, "steamcmd", SteamCmdArgv(spec.InstallDir), SteamCmdTimeout, ct).ConfigureAwait(false);
        if (!steam.Started)
        {
            await run.Fail("could not start SteamCMD - is it installed and on PATH?").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        if (steam.TimedOut)
        {
            await run.Fail($"SteamCMD did not finish within {SteamCmdTimeout.TotalMinutes:0} minutes.").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        if (steam.ExitCode != 0)
        {
            await run.Fail($"SteamCMD exited {steam.ExitCode}: {Short(steam.Combined)}").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok("installed.").ConfigureAwait(false);

        // ---- config files ----
        await run.Start("writing RconSettings.txt and Game.ini…").ConfigureAwait(false);
        if (await WriteConfigAsync(spec, ct).ConfigureAwait(false) is { } configProblem)
        {
            await run.Fail(configProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok("written and owned by steam.").ConfigureAwait(false);

        // ---- systemd unit ----
        await run.Start("installing and enabling the service…").ConfigureAwait(false);
        if (await InstallUnitAsync(spec, unitPath, ct).ConfigureAwait(false) is { } unitProblem)
        {
            await run.Fail(unitProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok($"{spec.UnitName} enabled and started.").ConfigureAwait(false);

        // ---- firewall (non-critical) ----
        await run.Start("opening the game, query and RCON ports…").ConfigureAwait(false);
        var ufw = await OpenPortsAsync(spec, ct).ConfigureAwait(false);
        if (ufw is null) await run.Ok($"allowed {spec.GamePort}/udp, {spec.QueryPort}/udp, {spec.RconPort}/tcp.").ConfigureAwait(false);
        else await run.SoftFail(ufw).ConfigureAwait(false);

        // ---- RCON reachability (non-critical) ----
        await run.Start("asking the new server for ServerInfo…").ConfigureAwait(false);
        if (await RconReachableAsync(spec, ct).ConfigureAwait(false))
            await run.Ok("answered RCON.").ConfigureAwait(false);
        else
            await run.SoftFail("no RCON answer yet - the server may still be starting; check /health after a minute.").ConfigureAwait(false);

        // ---- wire into the bot's .env (only reached when every critical step passed) ----
        await run.Start("appending the server to .env…").ConfigureAwait(false);
        if (await AppendEnvAsync(request, ct).ConfigureAwait(false) is { } envProblem)
        {
            await run.Fail(envProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok($"added as server {spec.Slot}.").ConfigureAwait(false);

        // ---- restart the bot ----
        return await RestartAsync(run, spec.Slot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every reason the run must not start, or null when it may.
    /// </summary>
    /// <remarks>
    /// Purely local checks - root, the unit name, and what is already on disk - so this is not
    /// async. Whether the <c>steam</c> account exists is a SEPARATE, later step: existence there
    /// is not a refusal but a fork (create it vs. leave it), which does not fit "every reason not
    /// to start" the way these do.
    /// </remarks>
    private static string? Preflight(ServerProvisionSpec spec, string unitPath)
    {
        if (!Environment.IsPrivilegedProcess)
            return "the bot is not running as root, so it cannot write a systemd unit, enable a service, create the steam " +
                   "account or run SteamCMD as it. Run the bot as root for this command (its other privileged actions use a " +
                   "narrow sudoers line; this one does not fit that).";

        if (!ServiceControl.IsPlausibleUnitName(spec.UnitName))
            return $"\"{spec.UnitName}\" is not a usable systemd unit name.";

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

    /// <summary>
    /// Make sure the <c>steam</c> OS account exists, creating it - WITH A PASSWORD, never left
    /// blank - if it does not.
    /// </summary>
    /// <remarks>
    /// A password is REQUIRED whenever this creates the account: an unprivileged account with no
    /// password is not a hardened account, it is a locked door nobody has the key to yet, which
    /// turns into "give it a password" as an out-of-band step somebody has to remember under
    /// pressure the first time they need to log in as steam. If the account already exists, it is
    /// left completely alone - this never resets a password an operator may already be relying on.
    /// </remarks>
    /// <returns>A problem, or null with whether the account was newly created.</returns>
    private async Task<(string? Problem, bool Created)> EnsureSteamUserAsync(string password, CancellationToken ct)
    {
        var id = await RunAsync(null, "id", [SteamUser], QuickTimeout, ct).ConfigureAwait(false);
        if (id.Ok) return (null, false);

        var add = await RunAsync(null, "useradd", UseraddArgv(SteamUser), QuickTimeout, ct).ConfigureAwait(false);
        if (!add.Ok) return ($"useradd {SteamUser} failed: {Short(add.Combined)}", false);

        // chpasswd, over STDIN - never as a command-line argument, which ps would show to
        // every other user on the box for as long as the process runs.
        var chpasswd = await RunAsync(null, "chpasswd", [], QuickTimeout, ct, ChpasswdStdin(SteamUser, password))
            .ConfigureAwait(false);
        if (!chpasswd.Ok)
            return ($"created the \"{SteamUser}\" user but could not set its password: {Short(chpasswd.Combined)}", false);

        return (null, true);
    }

    /// <summary>
    /// Grant <c>steam</c> unrestricted, passwordless sudo. Runs every provision, whether or not
    /// the account was just created.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY BROAD, ON EXPLICIT OPERATOR INSTRUCTION - and the opposite of the pattern
    /// everything else in this class follows. <see cref="ServiceControl.Advice"/> grants the
    /// BOT'S OWN user exactly <c>systemctl start/stop/restart</c> on named units and nothing
    /// else; this grants the account that runs SteamCMD and the game server - the single most
    /// exposed process on the box, the one an untrusted workshop map or a Pavlov exploit reaches
    /// first - the same access as root. A compromise of either becomes a root compromise the
    /// instant this file exists. Kept as one small, clearly-named method rather than folded into
    /// anything else so this decision's blast radius stays legible in exactly one place, not
    /// scattered across the class.
    ///
    /// A DROP-IN, NEVER AN EDIT TO <c>/etc/sudoers</c> ITSELF. The main file is parsed as a
    /// whole; a bad edit there breaks sudo for EVERY user on the box, root included if root
    /// normally reaches privilege through sudo too. A drop-in under <c>/etc/sudoers.d</c> is
    /// purely additive - this step can only ever add exactly the one file named above.
    ///
    /// VALIDATED BEFORE IT GOES LIVE. <c>visudo -c -f</c> checks a file's syntax without
    /// installing it, run here against the file we are ABOUT TO install rather than the live
    /// one - so a mistake is caught before anything is broken, not after.
    /// </remarks>
    /// <returns>A problem, or null on success.</returns>
    private async Task<string?> GrantFullSudoAsync(CancellationToken ct)
    {
        var temp = $"{SudoersDropIn}.bot.tmp";

        try
        {
            await File.WriteAllTextAsync(temp, SudoersFullAccessLine(SteamUser), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not write a temporary sudoers file: {ex.Message}";
        }

        var check = await RunAsync(null, "visudo", ["-c", "-f", temp], QuickTimeout, ct).ConfigureAwait(false);
        if (!check.Ok)
        {
            TryDelete(temp);
            return check.Started
                ? $"the generated sudoers file failed validation, so nothing was installed: {Short(check.Combined)}"
                : "could not start visudo to validate the sudoers file - is sudo installed?";
        }

        // PavlovBot.Host is Linux-only by design (CLAUDE.md), and this guard is what tells the
        // platform-compatibility analyzer that File.SetUnixFileMode below is reachable only on a
        // platform it supports - the same OperatingSystem.IsLinux() idiom ProcessRunnerTests
        // already uses to gate Unix-only behaviour.
        if (!OperatingSystem.IsLinux())
            return "this step needs a POSIX chmod, which is not available on this platform.";

        try
        {
            // The rename is the same-directory atomic swap AtomicFile uses elsewhere; the mode
            // set after it matters just as much - sudo REFUSES a drop-in that is group- or
            // world-writable rather than silently ignoring the risk, so a wrong mode here would
            // report success while granting nothing.
            File.Move(temp, SudoersDropIn, overwrite: true);
            File.SetUnixFileMode(SudoersDropIn, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"validated but could not install {SudoersDropIn}: {ex.Message}";
        }

        logger.LogWarning(
            "PROVISIONSERVER granted \"{User}\" full passwordless sudo via {Path} - deliberately broad and " +
            "operator-requested, not this bot's usual least-privilege pattern", SteamUser, SudoersDropIn);

        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

        await run.Start("checking how to bring the bot back…").ConfigureAwait(false);

        if (!underPm2)
        {
            await run.Ok($"not running under pm2 - restart the bot yourself to bring server {slot} online.").ConfigureAwait(false);
            return run.Finish(restartQueued: false);
        }

        // Announce and FLUSH the final state before we trigger our own SIGTERM.
        await run.Ok($"restarting `{appName}` now to bring server {slot} online…").ConfigureAwait(false);
        var outcome = run.Finish(restartQueued: true);

        logger.LogWarning("Provision complete; restarting {App} via pm2 to load server {Slot}", appName, slot);
        _ = RunAsync(null, "pm2", ["restart", appName], QuickTimeout, ct);
        return outcome;
    }

    /// <summary>
    /// One external command, as root or dropped to another user, always through ProcessRunner.
    /// </summary>
    /// <param name="asUser">Run as this user via <c>sudo -u</c>; null runs it as the bot (root here).</param>
    /// <param name="stdin">Passed straight through to <see cref="ProcessRunner.RunAsync"/>.</param>
    private async Task<ProcessOutcome> RunAsync(
        string? asUser, string file, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct,
        string? stdin = null)
    {
        // No shell anywhere: argv stays an array through sudo too, so there is nothing to inject
        // into even when a value is interpolated into a path above.
        var (exe, args) = asUser is null
            ? (file, argv)
            : ("sudo", (IReadOnlyList<string>)["-u", asUser, file, .. argv]);

        try
        {
            return await ProcessRunner.RunAsync(exe, args, timeout, logger, ct, stdin).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not run {File}", file);
            return ProcessOutcome.NotStarted;
        }
    }

    private static string Short(string text) => text.Length <= 300 ? text : text[..300] + "…";

    /// <summary>
    /// The mutable checklist for one run: holds the steps, tracks which one is current, and
    /// pushes a snapshot on every change.
    /// </summary>
    /// <remarks>
    /// A CURSOR, NOT AN INDEX EVERY CALLER PASSES IN. The previous shape took the step number as
    /// an argument at every call site, and inserting one step meant hand-renumbering every call
    /// after it - which is exactly how <c>RestartAsync</c> shipped once still pointing at the
    /// step before it. <see cref="Start"/> advances to the next pending step; every other method
    /// acts on whichever one that left current. Getting the ORDER of the <c>Start</c> calls in
    /// <see cref="ProvisionAsync"/> wrong is still possible - it always was, the checklist is
    /// only ever as correct as the sequence that drives it - but a stale number left behind by an
    /// insertion is not, because there is no longer a number to go stale.
    /// </remarks>
    internal sealed class Run(IReadOnlyList<string> names, Func<IReadOnlyList<ProvisionStep>, Task> onProgress)
    {
        private readonly ProvisionStep[] _steps =
            [.. names.Select(n => new ProvisionStep(n, ProvisionStatus.Pending, ""))];
        private int _current = -1;

        /// <summary>Advance to the next step and mark it Running.</summary>
        public Task Start(string detail)
        {
            _current++;
            return Set(_current, ProvisionStatus.Running, detail);
        }

        public Task Ok(string detail) => Set(_current, ProvisionStatus.Ok, detail);
        public Task Fail(string detail) => Set(_current, ProvisionStatus.Failed, detail);
        public Task SoftFail(string detail) => Set(_current, ProvisionStatus.Failed, detail);

        /// <summary>Mark every step AFTER the current one Skipped, and finish.</summary>
        public async Task<ProvisionOutcome> Abort()
        {
            for (var i = _current + 1; i < _steps.Length; i++)
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
