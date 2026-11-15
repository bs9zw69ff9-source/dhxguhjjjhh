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
/// <c>steam</c> GETS NO SUDO AND NO PASSWORD. Earlier versions granted it
/// <c>ALL=(ALL) NOPASSWD: ALL</c> and a fixed password, which made the account that runs the game
/// server and untrusted workshop content equivalent to root. Nothing here needs it: every step
/// that acts as steam is run BY root through <c>sudo -u steam</c>, which never consults steam's
/// own sudo rights. See <see cref="RemoveLegacySudoGrant"/>.
/// </remarks>
public sealed class ServerProvisioner(ILogger<ServerProvisioner> logger) : IServerProvisioner
{
    /// <summary>SteamCMD's app id for the Pavlov VR dedicated server.</summary>
    private const string PavlovAppId = "622970";

    /// <summary>The OS user the game server runs as and owns its files.</summary>
    private const string SteamUser = "steam";

    /// <summary>
    /// The full-sudo drop-in that earlier versions of this provisioner installed for steam. It is
    /// only ever REMOVED now - the name is this bot's own, so deleting it cannot touch anything an
    /// operator wrote.
    /// </summary>
    internal const string LegacySudoersDropIn = "/etc/sudoers.d/pavlov-steam-full";

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
        "Operating system and dependencies",
        "Steam user account",
        "Remove legacy steam sudo grant",
        "SteamCMD (locate or bootstrap)",
        "steamclient.so (SteamCMD app 1007)",
        "SteamCMD install",
        "libc++ and the launch script",
        "Server config (RconSettings.txt, Game.ini)",
        "Restore kept player data",
        "systemd unit (write, daemon-reload, enable --now)",
        "Firewall (ufw)",
        "RCON reachability",
        "Wire into the bot (.env)",
        "Engine.ini (net.MaxPlayersOverride)",
        "Restart the bot",
    ];

    /// <summary>The same run, with the two SteamCMD steps replaced by one copy.</summary>
    internal static readonly string[] CopyStepNames =
    [
        "Pre-flight checks",
        "Operating system and dependencies",
        "Steam user account",
        "Remove legacy steam sudo grant",
        "Copy an existing install",
        "steamclient.so (SteamCMD app 1007)",
        "libc++ and the launch script",
        "Server config (RconSettings.txt, Game.ini)",
        "Restore kept player data",
        "systemd unit (write, daemon-reload, enable --now)",
        "Firewall (ufw)",
        "RCON reachability",
        "Wire into the bot (.env)",
        "Engine.ini (net.MaxPlayersOverride)",
        "Restart the bot",
    ];

    /// <summary>Which checklist a run follows. Copying skips SteamCMD entirely.</summary>
    internal static IReadOnlyList<string> StepsFor(bool copying) => copying ? CopyStepNames : StepNames;

    /// <summary>The checklist for taking a server back off the box.</summary>
    internal static readonly string[] DeleteStepNames =
    [
        "Pre-flight checks",
        "Stop and disable the service",
        "Remove the systemd unit",
        "Delete the install directory",
        "Unwire from the bot (.env)",
        "Restart the bot",
    ];

    /// <summary>
    /// <c>cp -r &lt;source&gt;/. &lt;destination&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The trailing <c>/.</c> copies the source's CONTENTS rather than the source directory
    /// itself. Plain <c>cp -r src dst</c> means two different things depending on whether dst
    /// already exists - it becomes dst, or it becomes dst/src - and the second would bury the
    /// install one level down where nothing would find it. This form is the same either way.
    /// </remarks>
    internal static IReadOnlyList<string> CopyInstallArgv(string source, string destination) =>
        ["-r", $"{source.TrimEnd('/')}/.", destination];

    /// <summary>
    /// The SteamCMD argument list, verbatim. Extracted so the exact command line - the app id
    /// and the branch above all - is pinned by a test rather than discovered against a live Steam.
    /// </summary>
    /// <remarks>
    /// <c>-beta shack</c>, NOT <c>-beta default</c>. Those are two different builds of the game:
    /// <c>default</c> is the PC one and <c>shack</c> is the Quest/standalone one, and this bot is
    /// a Shack bot throughout - its master-server list is the shack/oculus endpoint and it counts
    /// Shack players. Installing <c>default</c> here produced a server the rest of the bot could
    /// never see, and nothing about that failure would have named the branch as the reason.
    /// </remarks>
    internal static IReadOnlyList<string> SteamCmdArgv(string installDir) =>
        ["+force_install_dir", installDir, "+login", "anonymous",
         "+app_update", PavlovAppId, "-beta", "shack", "+exit"];

    /// <summary><c>systemctl enable --now &lt;unit&gt;</c>, as an argv array.</summary>
    internal static IReadOnlyList<string> EnableArgv(string unit) => ["enable", "--now", unit];

    /// <summary>
    /// The wiki's <c>sudo ufw allow 7777</c>: the port alone, which ufw opens for TCP and UDP.
    /// </summary>
    /// <remarks>
    /// NO PROTOCOL, as the wiki writes it. The server's ports need UDP, and a Shack server also needs
    /// them over TCP to hand out downloadable maps, which the wiki says in the same section.
    /// </remarks>
    internal static IReadOnlyList<string> UfwAllowArgv(int port) =>
        ["allow", port.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <summary><c>useradd -m -s /bin/bash &lt;user&gt;</c>: a real home directory, for SteamCMD's cache.</summary>
    internal static IReadOnlyList<string> UseraddArgv(string user) => ["-m", "-s", "/bin/bash", user];

    /// <summary>Where Valve publishes the Linux SteamCMD tarball.</summary>
    private const string SteamCmdTarballUrl =
        "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";

    /// <summary>The steam account's home, parsed out of a <c>getent passwd</c> line.</summary>
    /// <remarks>
    /// Field 6 of the seven colon-separated fields
    /// (<c>name:passwd:uid:gid:gecos:HOME:shell</c>). Parsed rather than assumed because the
    /// whole point of this step is not to guess where things are; the caller still falls back to
    /// <c>/home/steam</c> when getent says nothing useful, which is what <c>useradd -m</c> makes.
    /// </remarks>
    internal static string? HomeFromGetent(string? getentOutput)
    {
        var line = (getentOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null) return null;

        var fields = line.Split(':');
        return fields.Length >= 6 && fields[5].StartsWith('/') ? fields[5] : null;
    }

    /// <summary>
    /// Where the wiki puts SteamCMD: <c>~/Steam/steamcmd.sh</c>, an absolute path.
    /// </summary>
    /// <remarks>
    /// THE WIKI'S LOCATION ONLY, and a list of one so the lookup keeps its shape. The wiki's later
    /// steps name paths under it: <c>steamclient.so</c> comes from
    /// <c>~/Steam/steamapps/common/Steamworks SDK Redist</c>. A distro package puts SteamCMD
    /// elsewhere, and its files would not be where those steps look. ABSOLUTE, NOT A <c>PATH</c>
    /// LOOKUP: the tarball puts nothing on anyone's PATH, which is what once made a provision die
    /// on "command not found" after it had created the account.
    /// </remarks>
    internal static IReadOnlyList<string> SteamCmdCandidates(string home)
    {
        var root = home.TrimEnd('/');
        return [$"{root}/Steam/steamcmd.sh"];
    }

    /// <summary>
    /// The documented install, as far as it can be run without a shell.
    /// </summary>
    /// <remarks>
    /// The published one-liner is
    /// <c>mkdir ~/Steam &amp;&amp; cd ~/Steam &amp;&amp; curl -sqL "…" | tar zxvf -</c>. The
    /// <c>curl</c> flags and the URL are kept exactly; the only departure is that the pipe becomes
    /// a download followed by a separate <c>tar</c>, because a pipe needs a shell and nothing in
    /// this class runs one. Same bytes, same destination.
    /// </remarks>
    internal static IReadOnlyList<string> SteamCmdDownloadArgv(string tarballPath) =>
        ["-sqL", SteamCmdTarballUrl, "-o", tarballPath];

    /// <summary><c>tar -xzf &lt;tarball&gt; -C &lt;dir&gt;</c>.</summary>
    internal static IReadOnlyList<string> SteamCmdExtractArgv(string tarballPath, string directory) =>
        ["-xzf", tarballPath, "-C", directory];

    // ---- the wiki's step 2: dependencies ----

    /// <summary>The <c>/etc/os-release</c> fields: <c>ID=ubuntu</c>, <c>VERSION_ID="24.04"</c>.</summary>
    internal static IReadOnlyDictionary<string, string> ParseOsRelease(string? text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || line.StartsWith('#')) continue;
            fields[line[..equals]] = line[(equals + 1)..].Trim('"', '\'');
        }
        return fields;
    }

    /// <summary>
    /// The packages of the wiki's step 2 for this system, or null when it is not Debian-like.
    /// </summary>
    /// <remarks>
    /// <c>gdb curl lib32gcc-s1 libc++-dev unzip</c> for Ubuntu 22.10 and later, and
    /// <c>lib32gcc1</c> in place of <c>lib32gcc-s1</c> for an older Ubuntu - the wiki's two lines.
    /// The wiki's third line, for Rocky, CentOS and RHEL, switches SELinux to permissive for the
    /// whole machine; that is not something to do on someone's behalf, so those systems get null.
    /// </remarks>
    internal static IReadOnlyList<string>? DependencyPackages(string? osRelease)
    {
        var fields = ParseOsRelease(osRelease);
        var id = fields.GetValueOrDefault("ID", "").ToLowerInvariant();
        var like = fields.GetValueOrDefault("ID_LIKE", "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (id is not ("ubuntu" or "debian") && !like.Contains("debian") && !like.Contains("ubuntu")) return null;

        var olderUbuntu = id == "ubuntu" && VersionBefore(fields.GetValueOrDefault("VERSION_ID", ""), 22, 10);
        return ["gdb", "curl", olderUbuntu ? "lib32gcc1" : "lib32gcc-s1", "libc++-dev", "unzip"];
    }

    /// <summary>Whether <c>22.04</c> is before <c>22.10</c>. An unreadable version is not "older".</summary>
    internal static bool VersionBefore(string version, int major, int minor)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var n)) return false;
        return m < major || (m == major && n < minor);
    }

    /// <summary>The wiki's <c>sudo apt update</c>, run through <c>env</c> so nothing waits at a prompt.</summary>
    internal static IReadOnlyList<string> AptUpdateArgv() => ["DEBIAN_FRONTEND=noninteractive", "apt-get", "update"];

    /// <summary>The wiki's <c>sudo apt install -y ...</c>.</summary>
    internal static IReadOnlyList<string> AptInstallArgv(IEnumerable<string> packages) =>
        ["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", .. packages];

    private static readonly TimeSpan AptTimeout = TimeSpan.FromMinutes(15);

    // ---- the wiki's post-installation steps ----

    /// <summary>Where <c>libc++.so</c> lives on Debian and Ubuntu, the path the wiki's commands name.</summary>
    internal const string LibcxxDirectory = "/usr/lib/x86_64-linux-gnu";

    /// <summary><c>steamcmd.sh +login anonymous +app_update 1007 +quit</c>: Steamworks SDK Redist, which holds steamclient.so.</summary>
    internal static IReadOnlyList<string> SteamClientUpdateArgv() => ["+login", "anonymous", "+app_update", "1007", "+quit"];

    /// <summary>
    /// <c>~/Steam/steamapps/common/Steamworks SDK Redist/linux64/steamclient.so</c>: beside the
    /// <c>steamcmd.sh</c> that downloaded it.
    /// </summary>
    internal static string SteamClientSource(string steamCmd) =>
        Path.Combine(Path.GetDirectoryName(steamCmd) ?? "/", "steamapps", "common", "Steamworks SDK Redist", "linux64", "steamclient.so");

    /// <summary><c>~/.steam/sdk64</c>.</summary>
    internal static string SteamClientSdkDirectory(string home) => $"{home.TrimEnd('/')}/.steam/sdk64";

    /// <summary><c>&lt;install&gt;/Pavlov/Binaries/Linux/steamclient.so</c>, the second copy the wiki makes.</summary>
    internal static string SteamClientInstallPath(string installDir) => $"{installDir.TrimEnd('/')}/Pavlov/Binaries/Linux/steamclient.so";

    /// <summary>The wiki's <c>sudo rm /usr/lib/x86_64-linux-gnu/libc++.so</c>; <c>-f</c> so a re-run is not an error.</summary>
    internal static IReadOnlyList<string> LibcxxRemoveArgv() => ["-f", $"{LibcxxDirectory}/libc++.so"];

    /// <summary>The wiki's <c>sudo ln -s .../libc++.so.1 .../libc++.so</c>.</summary>
    internal static IReadOnlyList<string> LibcxxLinkArgv() => ["-s", $"{LibcxxDirectory}/libc++.so.1", $"{LibcxxDirectory}/libc++.so"];

    /// <summary>The wiki's <c>chmod +x ~/pavlovserver/PavlovServer.sh</c>.</summary>
    internal static IReadOnlyList<string> LaunchScriptArgv(string installDir) => ["+x", $"{installDir.TrimEnd('/')}/PavlovServer.sh"];

    /// <summary>
    /// Whether an install folder holds nothing but an old server's <c>Pavlov/Saved</c> data.
    /// </summary>
    /// <remarks>
    /// THE ONE EXISTING FOLDER A PROVISION MAY GO INTO. Rebuilding a server over its own saved data -
    /// its Game.ini, ModSave, whitelist, blacklist - is the point; anything else in the folder is a
    /// real install, which this refuses to install over.
    /// </remarks>
    internal static bool HoldsOnlySavedData(string installDir)
    {
        var top = Directory.EnumerateFileSystemEntries(installDir).Select(Path.GetFileName).ToList();
        if (top.Count == 0) return true;
        if (top.Count != 1 || top[0] != "Pavlov") return false;
        return Directory.EnumerateFileSystemEntries(Path.Combine(installDir, "Pavlov")).Select(Path.GetFileName).All(n => n == "Saved");
    }

    public async Task<ProvisionOutcome> ProvisionAsync(
        ProvisionRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onProgress);

        var copying = request.CopyFromInstallDir is { Length: > 0 };
        var run = new Run(StepsFor(copying), onProgress);
        var spec = request.Spec;
        var unitPath = $"/etc/systemd/system/{spec.UnitName}.service";

        /* STEPS ADVANCE THEMSELVES. Nothing below names its own position in StepNames - Start()
           moves to the next pending one. Inserting or reordering a step used to mean renumbering
           every call after it by hand, and that is exactly the bug (RestartAsync referencing a
           stale index) that shipped once already; a cursor makes the mistake impossible instead
           of relying on getting the renumbering right the next time too. */

        // ---- pre-flight ----
        await run.Start("checking privileges and free slots…").ConfigureAwait(false);
        if (Preflight(spec, unitPath, request.RebuildingExistingSlot) is { } preflightProblem)
        {
            await run.Fail(preflightProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok("root, unit name and slot are usable.").ConfigureAwait(false);

        // ---- the wiki's steps 1 and 2: the operating system, and the packages the server needs ----
        await run.Start("checking the operating system and installing the dependencies…").ConfigureAwait(false);
        var (dependencyProblem, dependencyNote) = await InstallDependenciesAsync(ct).ConfigureAwait(false);
        if (dependencyProblem is not null)
        {
            await run.Fail(dependencyProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        if (dependencyNote.SoftFail) await run.SoftFail(dependencyNote.Text).ConfigureAwait(false);
        else await run.Ok(dependencyNote.Text).ConfigureAwait(false);

        // ---- the steam OS account - created locked: no password, no sudo ----
        await run.Start("checking for the steam account…").ConfigureAwait(false);
        var (userProblem, created) = await EnsureSteamUserAsync(ct).ConfigureAwait(false);
        if (userProblem is not null)
        {
            await run.Fail(userProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }

        /* AN OLD SERVER'S SAVED DATA IS NOT steam'S. A server that ran in a container left its
           Pavlov/Saved owned by the container's user, and the game and SteamCMD run as steam and must
           write there. Only a folder that exists already needs this; a new one is made by steam. */
        var handedOver = false;
        if (Directory.Exists(spec.InstallDir))
        {
            var chown = await RunAsync(null, "chown", ["-R", $"{SteamUser}:{SteamUser}", spec.InstallDir], SteamCmdTimeout, ct).ConfigureAwait(false);
            if (!chown.Ok)
            {
                await run.Fail($"could not hand {spec.InstallDir} to {SteamUser}: {Tail(chown.Combined)}").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            handedOver = true;
        }
        await run.Ok((created
            ? "created with a locked password - reachable only from root (sudo -u steam / su - steam)."
            : "already existed - left untouched.") +
            (handedOver ? $" The existing {spec.InstallDir} (a server's saved data) now belongs to it." : "")).ConfigureAwait(false);

        // ---- undo the full-sudo grant earlier versions installed ----
        await run.Start($"checking for {LegacySudoersDropIn}…").ConfigureAwait(false);
        var (sudoProblem, removed) = RemoveLegacySudoGrant();
        if (sudoProblem is not null)
        {
            await run.Fail(sudoProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok(removed
            ? $"removed {LegacySudoersDropIn} - steam no longer has sudo."
            : "none present - steam has no sudo grant from this bot.").ConfigureAwait(false);

        // ---- the game files: copied from a working install, or downloaded with SteamCMD ----
        string? steamCmd = null;

        /* THE STEAMCLIENT STEP, RIGHT AFTER STEAMCMD IS THERE. In a download run that is straight
           after SteamCMD is located and before the game is fetched; in a copy run SteamCMD was never
           looked for, so it is found here. One run.Start for both, which is what the checklist
           count test expects of a step shared by the two branches. Returns null to carry on, or the
           aborted outcome. */
        async Task<ProvisionOutcome?> InstallSteamClientAsync()
        {
            await run.Start("fetching steamclient.so with SteamCMD (app 1007) and copying it into place…").ConfigureAwait(false);
            if (steamCmd is null)
            {
                var (located, locateFailure, _) = await EnsureSteamCmdAsync(ct).ConfigureAwait(false);
                steamCmd = located;
                if (steamCmd is null)
                {
                    await run.Fail(locateFailure ?? "could not find or install SteamCMD.").ConfigureAwait(false);
                    return await run.Abort().ConfigureAwait(false);
                }
            }

            var (clientProblem, clientNote) = await SteamClientAsync(spec, steamCmd, ct).ConfigureAwait(false);
            if (clientProblem is not null)
            {
                await run.Fail(clientProblem).ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            await run.Ok(clientNote).ConfigureAwait(false);
            return null;
        }

        if (copying)
        {
            var source = request.CopyFromInstallDir!;
            await run.Start($"copying {source}…").ConfigureAwait(false);

            if (!Directory.Exists(source))
            {
                await run.Fail($"{source} does not exist - there is nothing to copy from.").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }

            var mkTarget = await RunAsync(SteamUser, "mkdir", ["-p", spec.InstallDir], QuickTimeout, ct).ConfigureAwait(false);
            if (!mkTarget.Ok)
            {
                await run.Fail($"could not create {spec.InstallDir}: {Tail(mkTarget.Combined)}").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }

            // As slow as the disk and as large as the install, so it gets SteamCMD's budget.
            var copy = await RunAsync(SteamUser, "cp", CopyInstallArgv(source, spec.InstallDir), SteamCmdTimeout, ct).ConfigureAwait(false);
            if (!copy.Started)
            {
                await run.Fail("could not start cp.").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            if (copy.TimedOut)
            {
                await run.Fail($"the copy did not finish within {SteamCmdTimeout.TotalMinutes:0} minutes.").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            if (copy.ExitCode != 0)
            {
                await run.Fail($"cp exited {copy.ExitCode}: {Tail(copy.Combined)}").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }

            await run.Ok($"copied from {source}.").ConfigureAwait(false);

            if (await InstallSteamClientAsync().ConfigureAwait(false) is { } copyAborted) return copyAborted;
        }
        else
        {
            // ---- find SteamCMD, or unpack Valve's tarball where the steam account can run it ----
            await run.Start("looking for steamcmd…").ConfigureAwait(false);
            var (found, locateProblem, bootstrapped) = await EnsureSteamCmdAsync(ct).ConfigureAwait(false);
            steamCmd = found;
            if (steamCmd is null)
            {
                await run.Fail(locateProblem ?? "could not find or install SteamCMD.").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            await run.Ok(bootstrapped
                ? $"not installed, so it was unpacked to {steamCmd}."
                : $"found at {steamCmd}.").ConfigureAwait(false);

            if (await InstallSteamClientAsync().ConfigureAwait(false) is { } downloadAborted) return downloadAborted;

            // ---- SteamCMD ----
            await run.Start("downloading the dedicated server (several GB - this is slow)…").ConfigureAwait(false);
            await RunAsync(SteamUser, "mkdir", ["-p", spec.InstallDir], QuickTimeout, ct).ConfigureAwait(false);
            var steam = await RunAsync(SteamUser, steamCmd, SteamCmdArgv(spec.InstallDir), SteamCmdTimeout, ct).ConfigureAwait(false);

            if (!steam.Started)
            {
                await run.Fail($"could not execute {steamCmd}.").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            if (steam.TimedOut)
            {
                await run.Fail($"SteamCMD did not finish within {SteamCmdTimeout.TotalMinutes:0} minutes.").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            if (steam.ExitCode != 0)
            {
                // What SteamCMD said, and nothing invented on top of it.
                await run.Fail($"SteamCMD exited {steam.ExitCode}: {Tail(steam.Combined)}").ConfigureAwait(false);
                return await run.Abort().ConfigureAwait(false);
            }
            await run.Ok("installed.").ConfigureAwait(false);
        }

        // ---- the wiki's remaining post-installation steps ----
        await run.Start("fixing libc++ and making PavlovServer.sh executable…").ConfigureAwait(false);
        var (postProblem, postNote) = await LaunchPrepAsync(spec, ct).ConfigureAwait(false);
        if (postProblem is not null)
        {
            await run.Fail(postProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        if (postNote.SoftFail) await run.SoftFail(postNote.Text).ConfigureAwait(false);
        else await run.Ok(postNote.Text).ConfigureAwait(false);

        // ---- config files ----
        await run.Start("writing RconSettings.txt and Game.ini…").ConfigureAwait(false);
        var (configProblem, configNote) = await WriteConfigAsync(spec, ct).ConfigureAwait(false);
        if (configProblem is not null)
        {
            await run.Fail(configProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok(configNote).ConfigureAwait(false);

        /* ---- player data kept by an earlier /deleteserver of this slot ----
           ALWAYS A STEP, even with nothing to restore. The checklist is a cursor over a fixed
           list: a Start() that only happens sometimes shifts every later step by one, and the
           last one then indexes past the end - which is exactly how this shipped the first time. */
        await run.Start("looking for player data kept when this server was deleted…").ConfigureAwait(false);
        if (!Directory.Exists(PreservedPlayerData.PreservedDir(spec.InstallDir)))
        {
            await run.Ok("none kept - nothing to restore.").ConfigureAwait(false);
        }
        else
        {
            try
            {
                var restored = PreservedPlayerData.Restore(spec.InstallDir);
                var summary = $"restored {(restored.Moved.Count > 0 ? string.Join(", ", restored.Moved) : "nothing")}";
                if (restored.Skipped.Count > 0)
                {
                    await run.SoftFail($"{summary}; left in {PreservedPlayerData.PreservedDir(spec.InstallDir)}: " +
                                       string.Join(", ", restored.Skipped)).ConfigureAwait(false);
                }
                else
                {
                    await run.Ok(summary + ".").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                /* Not fatal: the server itself is fine, and the data is still where it was kept. */
                await run.SoftFail($"could not restore it ({ex.Message}); it is still at " +
                                   $"{PreservedPlayerData.PreservedDir(spec.InstallDir)}.").ConfigureAwait(false);
            }
        }

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
        if (ufw is null) await run.Ok($"allowed {spec.GamePort}, {spec.QueryPort} and {spec.RconPort} (ufw allow <port>, as the wiki does).").ConfigureAwait(false);
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

        /* ---- the final step: Engine.ini ----
           Last of the provision's own work, and BEFORE the bot restarts itself - once that SIGTERM
           is sent nothing after it runs. The game reads Engine.ini only when it starts, and by now
           it is already running, so the server is restarted for the setting to count. */
        await run.Start("adding net.MaxPlayersOverride=32 to Engine.ini if it exists…").ConfigureAwait(false);
        var (engineProblem, engineNote) = await WriteEngineIniAsync(spec, ct).ConfigureAwait(false);
        if (engineProblem is not null)
        {
            await run.Fail(engineProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        if (engineNote.SoftFail) await run.SoftFail(engineNote.Text).ConfigureAwait(false);
        else await run.Ok(engineNote.Text).ConfigureAwait(false);

        // ---- restart the bot ----
        return await RestartAsync(run, $"to bring server {spec.Slot} online", ct).ConfigureAwait(false);
    }

    /// <summary>Whether a Docker container of this name exists, running or not.</summary>
    internal static IReadOnlyList<string> DockerExistsArgv(string container) =>
        ["container", "inspect", "--format", "{{.Id}}", container];

    /// <summary><c>docker rm -f</c>: stop and remove in one step.</summary>
    internal static IReadOnlyList<string> DockerRemoveArgv(string container) => ["rm", "-f", container];

    /// <summary>
    /// Remove the Docker container a pre-systemd build made for this unit name, if there is one.
    /// </summary>
    /// <returns>Whether one was removed, or a problem when it exists and could not be.</returns>
    private async Task<(bool Removed, string? Problem)> RemoveLegacyContainerAsync(string name, CancellationToken ct)
    {
        var exists = await RunAsync(null, "docker", DockerExistsArgv(name), SystemctlTimeout, ct).ConfigureAwait(false);
        // Not started: no docker binary. Not Ok: no such container. Either way there is nothing to remove.
        if (!exists.Started || !exists.Ok) return (false, null);

        var remove = await RunAsync(null, "docker", DockerRemoveArgv(name), SteamCmdTimeout, ct).ConfigureAwait(false);
        return remove.Ok
            ? (true, null)
            : (false, $"Docker container {name} exists but could not be removed: {Tail(remove.Combined)}");
    }

    public async Task<ProvisionOutcome> DeleteAsync(
        DeleteRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onProgress);

        var run = new Run(DeleteStepNames, onProgress);
        var unitPath = $"/etc/systemd/system/{request.UnitName}.service";

        // ---- pre-flight ----
        await run.Start("checking privileges and the unit name…").ConfigureAwait(false);
        if (!Environment.IsPrivilegedProcess)
        {
            await run.Fail("the bot is not running as root, so it cannot stop a service or remove its unit.").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        if (!ServiceControl.IsPlausibleUnitName(request.UnitName))
        {
            await run.Fail($"\"{request.UnitName}\" is not a usable systemd unit name.").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok("root, and the unit name is usable.").ConfigureAwait(false);

        // ---- stop and disable ----
        /* Neither is fatal. A unit that is already stopped, already disabled, or was never there
           at all is the state we want to end in, and systemctl says so with a non-zero exit -
           refusing to continue would leave the install and the .env entry behind for a
           "failure" that is actually the goal. */
        await run.Start($"stopping {request.UnitName}…").ConfigureAwait(false);
        var stop = await RunAsync(null, "systemctl", ["disable", "--now", request.UnitName], SystemctlTimeout, ct).ConfigureAwait(false);
        var stopNote = stop.Ok ? "stopped and disabled." : $"already stopped or absent ({Tail(stop.Combined, 200)}).";

        /* A SERVER THAT STILL RUNS AS A DOCKER CONTAINER IS REMOVED TOO. Servers built by the Docker
           version of this bot are containers named after their unit, and a delete that only knew
           systemd would leave one running on the same ports, with its restart policy, long after
           its install and .env entry were gone. Looked for on every delete rather than flagged, so
           it needs no setting and a box with no Docker is simply skipped. */
        var container = await RemoveLegacyContainerAsync(request.UnitName, ct).ConfigureAwait(false);
        if (container.Problem is not null)
        {
            await run.Fail(container.Problem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok(container.Removed ? $"{stopNote} Docker container {request.UnitName} stopped and removed too." : stopNote).ConfigureAwait(false);

        // ---- remove the unit ----
        await run.Start($"removing {unitPath}…").ConfigureAwait(false);
        try
        {
            if (File.Exists(unitPath)) File.Delete(unitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await run.Fail($"could not remove {unitPath}: {ex.Message}").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }

        var reload = await RunAsync(null, "systemctl", ["daemon-reload"], SystemctlTimeout, ct).ConfigureAwait(false);
        if (!reload.Ok)
        {
            await run.Fail($"systemctl daemon-reload failed: {Tail(reload.Combined)}").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok("removed, and systemd reloaded.").ConfigureAwait(false);

        // ---- delete the install ----
        await run.Start($"deleting {request.InstallDir}…").ConfigureAwait(false);
        if (DeletableInstallProblem(request.InstallDir) is { } pathProblem)
        {
            await run.Fail(pathProblem).ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }

        /* THE PLAYER DATA GOES FIRST, and the delete does not happen if it cannot. ModSave holds
           the faction rosters and the caps ledgers; an rm -rf that took them with it is how a
           delete-and-reprovision wiped every whitelist. */
        var keptAt = PreservedPlayerData.PreservedDir(request.InstallDir);
        try
        {
            var kept = PreservedPlayerData.Preserve(request.InstallDir, DateTimeOffset.UtcNow);
            if (kept.Moved.Count > 0)
                logger.LogWarning("Preserved {Items} from {Install} at {Path}",
                    string.Join(", ", kept.Moved), request.InstallDir, keptAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await run.Fail($"could not move the player data (ModSave, mods/whitelist/blacklist) out to {keptAt}, " +
                           $"so nothing was deleted: {ex.Message}").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }

        var remove = await RunAsync(null, "rm", ["-rf", request.InstallDir], SteamCmdTimeout, ct).ConfigureAwait(false);
        if (!remove.Ok)
        {
            await run.Fail($"could not delete {request.InstallDir}: {Tail(remove.Combined)}").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        logger.LogWarning("DELETED the install at {Path} for server {Slot}", request.InstallDir, request.Slot);
        await run.Ok(Directory.Exists(keptAt)
            ? $"deleted. Player data (rosters, ledgers, mod saves) kept at `{keptAt}` and restored by the next /provisionserver into this slot."
            : "deleted.").ConfigureAwait(false);

        // ---- unwire ----
        await run.Start("clearing it from .env…").ConfigureAwait(false);
        try
        {
            var existing = File.Exists(request.EnvPath)
                ? await File.ReadAllTextAsync(request.EnvPath, ct).ConfigureAwait(false)
                : "";

            var updated = ProvisionText.EnvWithoutServer(
                existing, request.Slot, request.FinalPavlovUnits, request.FinalPavlovBases,
                DateOnly.FromDateTime(DateTime.UtcNow));

            await AtomicFile.WriteAsync(request.EnvPath, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await run.Fail($"could not update {request.EnvPath}: {ex.Message}").ConfigureAwait(false);
            return await run.Abort().ConfigureAwait(false);
        }
        await run.Ok($"server {request.Slot} cleared.").ConfigureAwait(false);

        /* THE LAST SERVER RESTARTS LIKE ANY OTHER. This used to skip the restart because the bot
           exited 78 with no RCON server and would crash-loop; it now starts with none, so the
           restart is what lets the next /provisionserver plan from the real, empty layout. */
        // ---- restart ----
        return await RestartAsync(run, $"to drop server {request.Slot}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuse to <c>rm -rf</c> anything that is not plausibly one server's install.
    /// </summary>
    /// <remarks>
    /// The one guard worth having in front of a recursive delete run as root. The path is
    /// configuration rather than anything typed into Discord, but "/" or "/home" reaching this
    /// through a mis-set PAVLOV_BASE is not a risk worth carrying for the sake of trusting it.
    /// </remarks>
    internal static string? DeletableInstallProblem(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !installDir.StartsWith('/') ||
            installDir.Contains("..", StringComparison.Ordinal))
        {
            return $"\"{installDir}\" is not an absolute, traversal-free path - refusing to delete it.";
        }

        var trimmed = installDir.TrimEnd('/');

        // At least three levels down, e.g. /home/steam/pavlovserver. Anything shallower is a
        // system directory, not one server's install.
        if (trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 3)
            return $"\"{installDir}\" is too close to the root of the filesystem to be one server's install - refusing to delete it.";

        return null;
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
    private static string? Preflight(ServerProvisionSpec spec, string unitPath, bool rebuildingExistingSlot)
    {
        if (!Environment.IsPrivilegedProcess)
            return "the bot is not running as root, so it cannot write a systemd unit, enable a service, create the steam " +
                   "account or run SteamCMD as it. Run the bot as root for this command (its other privileged actions use a " +
                   "narrow sudoers line; this one does not fit that).";

        if (!ServiceControl.IsPlausibleUnitName(spec.UnitName))
            return $"\"{spec.UnitName}\" is not a usable systemd unit name.";

        // Overwriting this slot's OWN unit is the point when rebuilding it; refusing would block
        // exactly the repair being asked for. Only an unexpected one blocks a fresh slot.
        if (!rebuildingExistingSlot && File.Exists(unitPath))
            return $"{unitPath} already exists - refusing to overwrite an existing unit.";

        try
        {
            if (Directory.Exists(spec.InstallDir) && !HoldsOnlySavedData(spec.InstallDir))
                return $"{spec.InstallDir} already exists and holds more than a server's Pavlov/Saved data - refusing to install over it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not inspect {spec.InstallDir}: {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// Make sure the <c>steam</c> OS account exists, creating it LOCKED if it does not.
    /// </summary>
    /// <remarks>
    /// <c>useradd</c> leaves the password field locked, which is the point: the account runs the
    /// game server and SteamCMD, and nothing ever needs to log in to it with a password. Root
    /// reaches it with <c>sudo -u steam</c> or <c>su - steam</c>, neither of which asks for one.
    /// An existing account is left completely alone.
    /// </remarks>
    /// <returns>A problem, or null with whether the account was newly created.</returns>
    private async Task<(string? Problem, bool Created)> EnsureSteamUserAsync(CancellationToken ct)
    {
        var id = await RunAsync(null, "id", [SteamUser], QuickTimeout, ct).ConfigureAwait(false);
        if (id.Ok) return (null, false);

        var add = await RunAsync(null, "useradd", UseraddArgv(SteamUser), QuickTimeout, ct).ConfigureAwait(false);
        if (!add.Ok) return ($"useradd {SteamUser} failed: {Tail(add.Combined)}", false);

        return (null, true);
    }

    /// <summary>
    /// Delete the full-sudo drop-in earlier versions installed for steam, if it is there.
    /// </summary>
    /// <returns>A problem, or null with whether a file was removed.</returns>
    private (string? Problem, bool Removed) RemoveLegacySudoGrant()
    {
        try
        {
            if (!File.Exists(LegacySudoersDropIn)) return (null, false);
            File.Delete(LegacySudoersDropIn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"could not remove {LegacySudoersDropIn}, which gives steam full root: {ex.Message}", false);
        }

        logger.LogWarning("PROVISIONSERVER removed {Path} - the steam account no longer has sudo", LegacySudoersDropIn);
        return (null, true);
    }

    /// <summary>The steam account's home directory, asked for rather than assumed.</summary>
    private async Task<string> SteamHomeAsync(CancellationToken ct)
    {
        var getent = await RunAsync(null, "getent", ["passwd", SteamUser], QuickTimeout, ct).ConfigureAwait(false);
        return (getent.Ok ? HomeFromGetent(getent.Stdout) : null) ?? $"/home/{SteamUser}";
    }

    /// <summary>
    /// An executable SteamCMD, unpacking Valve's tarball if the box does not already have one.
    /// </summary>
    /// <remarks>
    /// WRITTEN FOR A REAL FAILURE: this used to invoke <c>steamcmd</c> and trust PATH, and a
    /// provision died on "sudo: steamcmd: command not found" after having already created the
    /// steam account and granted it sudo - three steps of real, privileged change followed by a
    /// stop for a missing tool. The ordinary Linux install is a tarball unpacked into
    /// <c>~/Steam</c>, which puts nothing on PATH, so there was nothing for that lookup to find
    /// even on a box where SteamCMD was present and working.
    ///
    /// Every path here is absolute and every step runs AS THE STEAM USER, so the cache and
    /// binaries it unpacks are owned by the account that will run them.
    /// </remarks>
    /// <returns>The executable, a problem when there is none, and whether it had to be installed.</returns>
    private async Task<(string? Path, string? Problem, bool Bootstrapped)> EnsureSteamCmdAsync(CancellationToken ct)
    {
        var home = await SteamHomeAsync(ct).ConfigureAwait(false);

        foreach (var candidate in SteamCmdCandidates(home))
            if (File.Exists(candidate)) return (candidate, null, false);

        // Nothing on the box - unpack Valve's tarball into ~/Steam, which is where the
        // documented install puts it and where the candidate list looks first among homes.
        var steamDir = $"{home.TrimEnd('/')}/Steam";
        var tarball = $"{steamDir}/steamcmd_linux.tar.gz";

        var mk = await RunAsync(SteamUser, "mkdir", ["-p", steamDir], QuickTimeout, ct).ConfigureAwait(false);
        if (!mk.Ok) return (null, $"could not create {steamDir}: {Tail(mk.Combined)}", false);

        // Two minutes: the tarball is a few MB, so anything slower is a broken route rather
        // than a slow one, and this must not sit on the deadline of the 45-minute step after it.
        var download = await RunAsync(SteamUser, "curl", SteamCmdDownloadArgv(tarball),
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!download.Started)
            return (null, "steamcmd is not installed and curl is not available to fetch it - install either one.", false);
        if (!download.Ok)
            return (null, $"could not download SteamCMD: {Tail(download.Combined)}", false);

        var extract = await RunAsync(SteamUser, "tar", SteamCmdExtractArgv(tarball, steamDir),
            TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (!extract.Started)
            return (null, "downloaded SteamCMD but tar is not available to unpack it.", false);
        if (!extract.Ok)
            return (null, $"could not unpack SteamCMD: {Tail(extract.Combined)}", false);

        var path = $"{steamDir}/steamcmd.sh";
        if (!File.Exists(path))
            return (null, $"unpacked SteamCMD but {path} is not there - the tarball layout was not what was expected.", false);

        logger.LogWarning("SteamCMD was not installed; unpacked Valve's tarball to {Path}", path);
        return (path, null, true);
    }

    /// <summary>
    /// The directories the guide has you create by hand before first start.
    /// </summary>
    /// <remarks>
    /// The documented setup is to start the server once and let it build these, with creating them
    /// by hand as the fallback for when that does not work. Doing it up front is the same end state
    /// and skips a start-then-stop cycle that a provision has no way to supervise.
    /// </remarks>
    internal static IReadOnlyList<string> ConfigDirectories(string installDir)
    {
        var saved = Path.Combine(installDir, "Pavlov", "Saved");
        return
        [
            Path.Combine(saved, "Logs"),
            Path.Combine(saved, "Config", "LinuxServer"),
            Path.Combine(saved, "maps"),
        ];
    }

    /// <summary>
    /// The three optional files the guide creates empty: mods, blacklist, whitelist.
    /// </summary>
    /// <remarks>
    /// EMPTY, AND ONLY IF ABSENT. Each is a list the operator fills in later, and on a copied
    /// install they may already have contents worth keeping - so these are touched, never written,
    /// and an existing one is left exactly as it is.
    /// </remarks>
    internal static IReadOnlyList<string> OptionalConfigFiles(string installDir)
    {
        var config = Path.Combine(installDir, "Pavlov", "Saved", "Config");
        return
        [
            Path.Combine(config, "mods.txt"),
            Path.Combine(config, "blacklist.txt"),
            Path.Combine(config, "whitelist.txt"),
        ];
    }

    /// <summary>Create the directories and files, then write the two configs. Null on success.</summary>
    private async Task<(string? Problem, string Note)> WriteConfigAsync(ServerProvisionSpec spec, CancellationToken ct)
    {
        var configDir = Path.Combine(spec.InstallDir, "Pavlov", "Saved", "Config");
        var linuxDir = Path.Combine(configDir, "LinuxServer");

        foreach (var directory in ConfigDirectories(spec.InstallDir))
        {
            var mk = await RunAsync(SteamUser, "mkdir", ["-p", directory], QuickTimeout, ct).ConfigureAwait(false);
            if (!mk.Ok) return ($"could not create {directory}: {Tail(mk.Combined)}", "");
        }

        foreach (var file in OptionalConfigFiles(spec.InstallDir))
        {
            // touch: creates it empty, and leaves an existing one untouched.
            var mkFile = await RunAsync(SteamUser, "touch", [file], QuickTimeout, ct).ConfigureAwait(false);
            if (!mkFile.Ok) return ($"could not create {file}: {Tail(mkFile.Combined)}", "");
        }

        string note;
        try
        {
            var rconPath = Path.Combine(configDir, "RconSettings.txt");
            await AtomicFile.WriteAsync(rconPath, ProvisionText.RconSettings(spec.RconPassword, spec.RconPort), ct).ConfigureAwait(false);

            /* AN EXISTING GAME.INI IS LEFT ALONE. The server generated one, or this is a rebuild over a
               server's saved data, or the install was copied: it is the owner's file. The one thing
               added is ServerKey, and only when a key was given and the file has none. The wiki's
               block is written only when there is no file. */
            var gameIniPath = Path.Combine(linuxDir, "Game.ini");
            var existing = File.Exists(gameIniPath) ? await File.ReadAllTextAsync(gameIniPath, ct).ConfigureAwait(false) : "";
            if (existing.Trim().Length == 0)
            {
                await AtomicFile.WriteAsync(gameIniPath, ProvisionText.GameIni(spec), ct).ConfigureAwait(false);
                note = "RconSettings.txt and Game.ini (the wiki's) written, owned by steam.";
            }
            else
            {
                var updated = string.IsNullOrEmpty(spec.ServerKey) ? existing : ProvisionText.EnsureGameIniSetting(existing, "ServerKey", spec.ServerKey);
                var added = !ReferenceEquals(updated, existing) && updated != existing;
                if (added) await AtomicFile.WriteAsync(gameIniPath, updated, ct).ConfigureAwait(false);
                note = "RconSettings.txt written; the existing Game.ini was kept" +
                       (added ? " and ServerKey added to it" : "") + ", owned by steam.";
            }

            // Written by root into a steam-owned tree; hand ownership back so the server reads them.
            foreach (var path in new[] { rconPath, gameIniPath })
            {
                var chown = await RunAsync(null, "chown", [SteamUser, path], QuickTimeout, ct).ConfigureAwait(false);
                if (!chown.Ok) return ($"could not chown {path} to {SteamUser}: {Tail(chown.Combined)}", "");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ex.Message, "");
        }

        return (null, note);
    }

    /// <summary>
    /// Append <c>[ConsoleVariables]</c> / <c>net.MaxPlayersOverride=32</c> to the server's Engine.ini,
    /// then restart the server so it reads it.
    /// </summary>
    private async Task<(string? Problem, (bool SoftFail, string Text) Note)> WriteEngineIniAsync(ServerProvisionSpec spec, CancellationToken ct)
    {
        var path = Path.Combine(spec.InstallDir, "Pavlov", "Saved", "Config", "LinuxServer", "Engine.ini");

        // NEVER CREATED. The server writes its own Engine.ini; a file this step invented would be a
        // second source of truth. No file, nothing to add to, and nothing to restart for.
        if (!File.Exists(path))
            return (null, (true, $"{path} does not exist, so it was not created and net.MaxPlayersOverride was not added."));

        try
        {
            var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var updated = ProvisionText.EnsureEngineIni(existing);
            if (!ReferenceEquals(updated, existing) && updated != existing)
                await AtomicFile.WriteAsync(path, updated, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"could not write {path}: {ex.Message}", (false, ""));
        }

        var chown = await RunAsync(null, "chown", [SteamUser, path], QuickTimeout, ct).ConfigureAwait(false);
        if (!chown.Ok) return ($"could not chown {path} to {SteamUser}: {Tail(chown.Combined)}", (false, ""));

        var restart = await RunAsync(null, "systemctl", ["restart", spec.UnitName], SystemctlTimeout, ct).ConfigureAwait(false);
        return restart.Ok
            ? (null, (false, $"{ProvisionText.EngineIniSection} {ProvisionText.EngineIniKey}={ProvisionText.EngineIniMaxPlayers} is in Engine.ini and {spec.UnitName} was restarted to read it."))
            : (null, (true, $"{ProvisionText.EngineIniKey} is in Engine.ini, but restarting {spec.UnitName} failed ({Tail(restart.Combined, 200)}) - restart it yourself for the setting to apply."));
    }

    /// <summary>The wiki's steps 1 and 2. A problem stops the run; a note is what the checklist says.</summary>
    private async Task<(string? Problem, (bool SoftFail, string Text) Note)> InstallDependenciesAsync(CancellationToken ct)
    {
        string osRelease;
        try
        {
            osRelease = File.Exists("/etc/os-release") ? await File.ReadAllTextAsync("/etc/os-release", ct).ConfigureAwait(false) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            osRelease = "";
        }

        var fields = ParseOsRelease(osRelease);
        var name = fields.GetValueOrDefault("PRETTY_NAME", "an unknown system");
        if (DependencyPackages(osRelease) is not { } packages)
        {
            return (null, (true, $"{name} is not Debian or Ubuntu, so the wiki's package step was not run: its Rocky/RHEL commands " +
                                 "switch SELinux to permissive for the whole machine. If the server will not start, do step 2 of the " +
                                 "wiki for this system by hand."));
        }

        var update = await RunAsync(null, "env", AptUpdateArgv(), AptTimeout, ct).ConfigureAwait(false);
        if (!update.Started) return ("could not run apt-get.", (false, ""));
        if (!update.Ok) return ($"apt-get update failed: {Tail(update.Combined)}", (false, ""));

        var install = await RunAsync(null, "env", AptInstallArgv(packages), AptTimeout, ct).ConfigureAwait(false);
        if (!install.Ok) return ($"apt-get install {string.Join(' ', packages)} failed: {Tail(install.Combined)}", (false, ""));

        return (null, (false, $"{name}: {string.Join(", ", packages)} installed."));
    }

    /// <summary>
    /// <c>steamcmd.sh +login anonymous +app_update 1007 +quit</c>, then <c>steamclient.so</c> copied to
    /// <c>~/.steam/sdk64</c> and to the install's <c>Pavlov/Binaries/Linux</c>.
    /// </summary>
    /// <remarks>
    /// The install's <c>Binaries/Linux</c> is created first (<c>mkdir -p</c>), because this runs
    /// before the game is downloaded and the folder is not there yet.
    /// </remarks>
    private async Task<(string? Problem, string Note)> SteamClientAsync(ServerProvisionSpec spec, string steamCmd, CancellationToken ct)
    {
        var home = await SteamHomeAsync(ct).ConfigureAwait(false);

        var update = await RunAsync(SteamUser, steamCmd, SteamClientUpdateArgv(), SteamCmdTimeout, ct).ConfigureAwait(false);
        if (!update.Started) return ($"could not execute {steamCmd}.", "");
        if (!update.Ok) return ($"SteamCMD could not fetch the Steamworks SDK Redist (app 1007): {Tail(update.Combined)}", "");

        var source = SteamClientSource(steamCmd);
        if (!File.Exists(source)) return ($"SteamCMD finished but {source} is not there.", "");

        var sdk = SteamClientSdkDirectory(home);
        var installed = SteamClientInstallPath(spec.InstallDir);
        foreach (var directory in new[] { sdk, Path.GetDirectoryName(installed)! })
        {
            var mk = await RunAsync(SteamUser, "mkdir", ["-p", directory], QuickTimeout, ct).ConfigureAwait(false);
            if (!mk.Ok) return ($"could not create {directory}: {Tail(mk.Combined)}", "");
        }

        foreach (var target in new[] { $"{sdk}/steamclient.so", installed })
        {
            var copy = await RunAsync(SteamUser, "cp", [source, target], QuickTimeout, ct).ConfigureAwait(false);
            if (!copy.Ok) return ($"could not copy steamclient.so to {target}: {Tail(copy.Combined)}", "");
        }

        return (null, $"steamclient.so copied to {sdk} and {installed}.");
    }

    /// <summary>The wiki's libc++ link and <c>chmod +x PavlovServer.sh</c>, once the game is installed.</summary>
    private async Task<(string? Problem, (bool SoftFail, string Text) Note)> LaunchPrepAsync(
        ServerProvisionSpec spec, CancellationToken ct)
    {
        // The wiki: "All versions are required to run these commands to change the libc++ that steamclient uses".
        var libcxx = "libc++ link skipped: this system keeps its libraries somewhere other than " + LibcxxDirectory + ".";
        var softFail = true;
        if (Directory.Exists(LibcxxDirectory))
        {
            if (!File.Exists($"{LibcxxDirectory}/libc++.so.1"))
                return ($"{LibcxxDirectory}/libc++.so.1 is not there, so libc++.so cannot be linked to it - the libc++-dev package should have provided it.", (false, ""));

            var rm = await RunAsync(null, "rm", LibcxxRemoveArgv(), QuickTimeout, ct).ConfigureAwait(false);
            if (!rm.Ok) return ($"could not remove {LibcxxDirectory}/libc++.so: {Tail(rm.Combined)}", (false, ""));
            var link = await RunAsync(null, "ln", LibcxxLinkArgv(), QuickTimeout, ct).ConfigureAwait(false);
            if (!link.Ok) return ($"could not link libc++.so: {Tail(link.Combined)}", (false, ""));
            libcxx = "libc++.so linked to libc++.so.1";
            softFail = false;
        }

        var chmod = await RunAsync(SteamUser, "chmod", LaunchScriptArgv(spec.InstallDir), QuickTimeout, ct).ConfigureAwait(false);
        if (!chmod.Ok) return ($"could not make PavlovServer.sh executable: {Tail(chmod.Combined)}", (false, ""));

        return (null, (softFail, $"{libcxx}; PavlovServer.sh is executable."));
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
        if (!reload.Ok) return $"systemctl daemon-reload failed: {Tail(reload.Combined)}";

        var enable = await RunAsync(null, "systemctl", EnableArgv(spec.UnitName), SystemctlTimeout, ct).ConfigureAwait(false);
        if (!enable.Ok) return $"systemctl enable --now {spec.UnitName} failed: {Tail(enable.Combined)}";

        return null;
    }

    /// <summary>Open the three ports. Null on success, else a note (non-fatal).</summary>
    private async Task<string?> OpenPortsAsync(ServerProvisionSpec spec, CancellationToken ct)
    {
        // The wiki's three: the game port, the query port (the game port + 400), and RCON.
        foreach (var port in new[] { spec.GamePort, spec.QueryPort, spec.RconPort })
        {
            var run = await RunAsync(null, "ufw", UfwAllowArgv(port), QuickTimeout, ct).ConfigureAwait(false);
            if (!run.Started) return "ufw is not installed or not on PATH - open the ports yourself.";
            if (!run.Ok) return $"ufw allow {port} did not succeed: {Tail(run.Combined)}";
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

            var updated = ProvisionText.EnvWithServer(
                existing, request.Spec, request.RconHost,
                request.FinalPavlovUnits, request.FinalPavlovBases,
                request.FinalPlayerCountChannels, DateOnly.FromDateTime(DateTime.UtcNow));

            await AtomicFile.WriteAsync(request.EnvPath, updated, ct).ConfigureAwait(false);
            logger.LogWarning("Wrote server {Slot} into {Path}", request.Spec.Slot, request.EnvPath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not update {request.EnvPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Restart the bot so it picks up the changed server list, reporting BEFORE the process can die.
    /// </summary>
    /// <remarks>
    /// The channel checklist is completed and flushed first, because the restart sends this
    /// process SIGTERM and nothing after that is guaranteed to run. Only a pm2- or systemd-managed
    /// bot can be restarted safely from inside (see <see cref="BotSupervisor"/>); anywhere else this
    /// reports the manual step instead of killing a process nothing will bring back.
    /// </remarks>
    /// <param name="why">What the restart achieves, e.g. "to bring server 3 online".</param>
    private async Task<ProvisionOutcome> RestartAsync(Run run, string why, CancellationToken ct)
    {
        var supervisor = BotSupervisor.Current();

        await run.Start("checking how to bring the bot back…").ConfigureAwait(false);

        if (supervisor.RestartCommand() is not { } restart)
        {
            await run.Ok($"not running under pm2 or systemd - restart the bot yourself {why}.").ConfigureAwait(false);
            return run.Finish(restartQueued: false);
        }

        // Announce and FLUSH the final state before we trigger our own SIGTERM.
        await run.Ok($"restarting {supervisor.Describe()} now {why}…").ConfigureAwait(false);
        var outcome = run.Finish(restartQueued: true);

        logger.LogWarning("Restarting via {Supervisor} {Why}", supervisor.Describe(), why);
        _ = RunAsync(null, restart.File, restart.Args, QuickTimeout, ct);
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

    /// <summary>
    /// The END of a command's output, which is where a failure explains itself.
    /// </summary>
    /// <remarks>
    /// THE TAIL, NOT THE HEAD, and that is a fix rather than a preference. This used to keep the
    /// first 300 characters, which for anything chatty is the startup banner - a real SteamCMD
    /// failure was reported as three lines of copyright notice and a progress bar cut off
    /// mid-word, with the actual error trimmed away entirely. Every caller here passes the output
    /// of a command that just failed, and tools put the reason last.
    ///
    /// Kept well inside Discord's limits: this lands in an embed description alongside every
    /// other step, and an over-long one costs the whole message rather than just its tail.
    /// </remarks>
    internal static string Tail(string? text, int limit = 600)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= limit ? trimmed : "…" + trimmed[^limit..];
    }

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
