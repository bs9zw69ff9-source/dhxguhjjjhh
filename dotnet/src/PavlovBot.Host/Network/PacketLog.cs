using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Network;

/// <summary>
/// The bounded always-on capture: a rolling pcap of inbound packet headers, so an attack's packets
/// exist on disk whether or not it was detected in time.
/// </summary>
/// <remarks>
/// HEADERS ONLY, BOUNDED, OPTIONAL. One tshark writes the first 128 bytes of each inbound packet to
/// <c>ring_*.pcap</c> in 50 MB files, and this class deletes the oldest past <c>PACKET_RING_MB</c>
/// (tshark's own <c>-b files:</c> only prunes its current run, so restarts would leak). Writing a
/// file, tshark does not dissect at all. This is the only part of the logging that touches every
/// packet in userspace; the per-source totals come from the kernel's counters instead (see
/// <see cref="FloodShieldRules"/>). The storage guard pauses it first when the disk runs short.
///
/// AN ATTACK KEEPS ITS PART OF THE RING. While one is attached, every ring file tshark closes is
/// moved (a rename, no copy) into <c>attacks/attack-*.pcap.parts/</c>, out of reach of the
/// pruning, up to <see cref="AttackPcapBytes"/>. When it ends the parts and the file still being
/// written are joined with <c>mergecap</c> into <c>attacks/attack-*.pcap</c>. A bot that dies
/// mid-attack (or a mergecap that fails) leaves the parts folder behind, and the next start joins it.
///
/// THE FILES ARE ROOT'S. tshark writes its pcaps 0600 and the folder is 0700.
/// </remarks>
public sealed partial class PacketLog : BackgroundService
{
    /// <summary>Size of one ring file. Small enough that the ring rolls in steps, big enough not to churn.</summary>
    private const long RingFileKb = 51_200;

    /// <summary>The most one attack's pcap keeps: about 1.7 million headers. Ten are kept.</summary>
    internal const long AttackPcapBytes = 256L * 1024 * 1024;

    private const string PartsSuffix = ".parts";

    private static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HousekeepEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MergeTimeout = TimeSpan.FromMinutes(3);

    private readonly string _iface;
    private readonly string _directory;
    private readonly string _attacks;
    private readonly long _ringBytes;
    private readonly int _keepDays;
    private readonly TimeProvider _time;
    private readonly ILogger<PacketLog> _logger;
    private readonly object _attachGate = new();
    private readonly object _ringGate = new();
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>> _mergecap;

    private volatile Attached[] _attached = [];
    private volatile bool _running;
    private volatile bool _paused;
    private volatile string? _problem;

    /// <param name="keepDays">How long unjoined parts are kept for another try.</param>
    public PacketLog(
        string iface, string directory, string attacksDirectory, int ringMegabytes, int keepDays,
        TimeProvider time, ILogger<PacketLog> logger,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>>? mergecap = null)
    {
        _iface = iface;
        _directory = directory;
        _attacks = attacksDirectory;
        // Never less than two files: the one being written and the one before it.
        _ringBytes = Math.Max(RingFileKb * 1024 * 2, (long)ringMegabytes * 1024 * 1024);
        _keepDays = Math.Max(1, keepDays);
        _time = time;
        _logger = logger;
        _mergecap = mergecap ?? ((argv, ct) => ProcessRunner.RunAsync("mergecap", argv, MergeTimeout, logger, ct));
    }

    /// <summary>Whether the ring is being written now.</summary>
    public bool Running => _running;

    /// <summary>Whether the storage guard has stopped it.</summary>
    public bool Paused => _paused;

    /// <summary>Why it is not running, when it is not.</summary>
    public string? Problem => _paused ? "paused: the disk is short of space" : _problem;

    public string Directory => _directory;

    public string Interface => _iface;

    public long RingBudgetBytes => _ringBytes;

    /// <summary>Stop writing until <see cref="Resume"/>; the ring already on disk is kept.</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    internal static IReadOnlyList<string> RingArgv(string iface, string directory) =>
    [
        "-i", iface, "-n", "-s", "128", "-B", "64", "-f", TsharkCapture.Filter,
        "-b", $"filesize:{RingFileKb.ToString(CultureInfo.InvariantCulture)}", "-w", Path.Combine(directory, "ring.pcap"),
    ];

    /// <summary>tshark's ring file names: <c>ring_00042_20261029135200.pcap</c>, a run's sequence then its start time.</summary>
    [GeneratedRegex(@"^ring_(\d+)_(\d{14})\.pcap$", RegexOptions.CultureInvariant)]
    private static partial Regex RingName();

    /// <summary>
    /// The ring's files, oldest first. By name, not by time written: across restarts the start time
    /// orders the runs, and within a run the sequence does, where two files can close in the same second.
    /// </summary>
    internal static List<FileInfo> RingFiles(string directory) =>
        !System.IO.Directory.Exists(directory) ? [] :
        [.. new DirectoryInfo(directory).EnumerateFiles("ring_*.pcap")
            .Select(f => (File: f, Match: RingName().Match(f.Name)))
            .Where(f => f.Match.Success)
            .OrderBy(f => f.Match.Groups[2].Value, StringComparer.Ordinal)
            .ThenBy(f => long.Parse(f.Match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(f => f.File)];

    /// <summary>The ring's size on disk now.</summary>
    public long RingBytes() => RingFiles(_directory).Sum(f => f.Length);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!LinkCounters.IsPlausibleInterface(_iface)) { Fail($"\"{_iface}\" is not a usable interface name"); return; }
        if (!Environment.IsPrivilegedProcess) { Fail("the bot is not running as root, so tshark cannot capture"); return; }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"cannot create {_directory}: {ex.Message}");
            return;
        }

        await RecoverAsync(stoppingToken).ConfigureAwait(false);

        var housekeeping = Task.Run(() => HousekeepAsync(stoppingToken), CancellationToken.None);
        _logger.LogInformation("Packet capture on {Interface}: headers to {Directory}, a ring of {Ring} MB",
            _iface, _directory, _ringBytes / 1024 / 1024);

        string? reported = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_paused)
            {
                if (!await DelayAsync(CheckEvery * 15, stoppingToken).ConfigureAwait(false)) break;
                continue;
            }

            Process? ring = null;
            try
            {
                ring = Launch(RingArgv(_iface, _directory));
                _running = true;
                _problem = null;
                reported = null;
                while (!ring.HasExited && !_paused && !stoppingToken.IsCancellationRequested)
                {
                    if (!await DelayAsync(CheckEvery, stoppingToken).ConfigureAwait(false)) break;
                }
                if (ring.HasExited && !stoppingToken.IsCancellationRequested) _problem = "tshark stopped";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _problem = "tshark is not installed - run `/packettools install`";
            }
            finally
            {
                _running = false;
                Stop(ring);
            }

            if (stoppingToken.IsCancellationRequested) break;
            if (_paused)
            {
                _logger.LogWarning("Packet capture PAUSED: the disk is short of space. The ring on disk is kept");
                continue;
            }

            // Once per problem: a box without tshark would otherwise say so twice a minute forever.
            if (_problem != reported)
                _logger.LogWarning("Packet capture is down ({Problem}); retrying every {Seconds}s", _problem, RestartAfter.TotalSeconds);
            reported = _problem;
            if (!await DelayAsync(RestartAfter, stoppingToken).ConfigureAwait(false)) break;
        }

        await housekeeping.ConfigureAwait(false);
    }

    private async Task<bool> DelayAsync(TimeSpan span, CancellationToken ct)
    {
        try
        {
            await Task.Delay(span, _time, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Fail(string problem)
    {
        _problem = problem;
        _logger.LogWarning("Packet capture is OFF: {Problem}", problem);
    }

    private async Task HousekeepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HousekeepEvery, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Housekeep();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Packet capture housekeeping failed; carrying on");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Move closed ring files to the attacks they belong to, then hold the ring to its budget.</summary>
    internal void Housekeep()
    {
        lock (_ringGate)
        {
            var rings = RingFiles(_directory);
            // The newest is the one tshark is writing; it never moves and is never deleted.
            List<FileInfo> closed = rings.Count > 0 ? rings[..^1] : [];

            foreach (var attached in _attached) attached.Collect(closed);

            long total = 0;
            for (var i = rings.Count - 1; i >= 0; i--)
            {
                rings[i].Refresh();
                if (!rings[i].Exists) continue;
                total += rings[i].Length;
                if (total > _ringBytes && i < rings.Count - 1) TryDelete(rings[i].FullName);
            }
        }
    }

    /// <summary>
    /// Join what an attack left in a parts folder: one the bot was restarted during, or one
    /// mergecap failed on. Parts that still cannot be joined are kept for a few days.
    /// </summary>
    internal async Task RecoverAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(_attacks)) return;

        foreach (var parts in System.IO.Directory.EnumerateDirectories(_attacks, "attack-*.pcap" + PartsSuffix))
        {
            var target = parts[..^PartsSuffix.Length];
            var files = RingFiles(parts).Select(f => f.FullName).ToList();
            var done = files.Count == 0 || File.Exists(target);
            if (!done && await MergeAsync(files, target, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("Joined the pcap of an attack that was not finished: {Pcap}", target);
                done = true;
            }

            if (done || System.IO.Directory.GetLastWriteTimeUtc(parts) < _time.GetUtcNow().UtcDateTime.AddDays(-_keepDays))
                TryDeleteDirectory(parts);
        }
    }

    /// <summary>
    /// Join pcaps into one. True when the target was written.
    /// </summary>
    /// <remarks>
    /// The newest ring file is still being written, so mergecap finds it "cut short in the middle of
    /// a packet" and exits 2 - having written everything before the cut. That counts as success.
    /// </remarks>
    private async Task<bool> MergeAsync(IReadOnlyList<string> files, string target, CancellationToken ct)
    {
        var run = await _mergecap(["-w", target, .. files], ct).ConfigureAwait(false);
        if (MergeSucceeded(run) && File.Exists(target))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }

        // Half a pcap would pass for a whole one, and stop the next start from retrying.
        if (File.Exists(target)) TryDelete(target);
        _logger.LogWarning("mergecap could not join {Count} file(s) into {Target}: {Output}",
            files.Count, target, run.Started ? run.Combined : "mergecap is not installed - run `/packettools install`");
        return false;
    }

    internal static bool MergeSucceeded(ProcessOutcome run) =>
        run.Started && !run.TimedOut &&
        (run.ExitCode == 0 || (run.ExitCode == 2 && run.Combined.Contains("cut short", StringComparison.Ordinal)));

    /// <summary>
    /// Keep the ring from now on for an attack: its pcap is cut from the ring when it stops.
    /// </summary>
    /// <param name="pcapPath">Where the attack's pcap goes.</param>
    public IAttackCapture Attach(string pcapPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pcapPath);

        string? parts = pcapPath + PartsSuffix;
        try
        {
            System.IO.Directory.CreateDirectory(parts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create {Parts}; the attack will have no pcap", parts);
            parts = null;
        }

        // The ring file being written now holds the attack's first seconds; it is collected once it closes.
        var attached = new Attached(this, pcapPath, parts, _time.GetUtcNow());
        lock (_attachGate) _attached = [.. _attached, attached];
        return attached;
    }

    private void Detach(Attached attached)
    {
        lock (_attachGate) _attached = [.. _attached.Where(a => a != attached)];
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    private static Process Launch(IReadOnlyList<string> argv)
    {
        var info = new ProcessStartInfo("tshark")
        {
            UseShellExecute = false,
            // Inherited rather than redirected: a long-running tshark's stderr is start-up chatter,
            // it lands in the bot's journal, and an undrained pipe would eventually block it.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var arg in argv) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("tshark did not start");
    }

    private void Stop(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "tshark was already gone");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>An attack keeping its part of the ring.</summary>
    private sealed class Attached(PacketLog log, string target, string? parts, DateTimeOffset since) : IAttackCapture
    {
        private long _collectedBytes;
        private int _stopped;

        public string? PcapPath { get; private set; }

        /// <summary>Take the closed ring files written during the attack, up to the attack's budget. Under the ring lock.</summary>
        public void Collect(IReadOnlyList<FileInfo> closed)
        {
            if (parts is null || Volatile.Read(ref _stopped) == 1) return;
            foreach (var file in closed)
            {
                file.Refresh();
                if (!file.Exists || file.LastWriteTimeUtc < since.UtcDateTime) continue;
                if (_collectedBytes + file.Length > AttackPcapBytes) return;
                try
                {
                    var length = file.Length;
                    file.MoveTo(Path.Combine(parts, file.Name));
                    _collectedBytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log._logger.LogWarning(ex, "Could not keep {File} for the attack's pcap", file.Name);
                }
            }
        }

        /// <summary>Join the attack's pcap. Its health is the breakdown capture's, not this one's.</summary>
        public async Task<CaptureHealth> StopAsync()
        {
            if (Volatile.Read(ref _stopped) == 1) return new CaptureHealth(null);
            log.Detach(this);

            if (parts is not null)
            {
                List<string> files;
                lock (log._ringGate)
                {
                    var rings = RingFiles(log._directory);
                    Collect(rings.Count > 0 ? rings[..^1] : []);
                    files = [.. RingFiles(parts).Select(f => f.FullName)];
                    // The file tshark is writing now holds the attack's last seconds.
                    if (rings.Count > 0 && _collectedBytes < AttackPcapBytes) files.Add(rings[^1].FullName);
                    Volatile.Write(ref _stopped, 1);
                }

                if (files.Count == 0)
                {
                    log.TryDeleteDirectory(parts);
                }
                else if (await log.MergeAsync(files, target, CancellationToken.None).ConfigureAwait(false))
                {
                    PcapPath = target;
                    log.TryDeleteDirectory(parts);
                }
                // Otherwise the parts stay, and the next start tries again.
            }

            Volatile.Write(ref _stopped, 1);
            return new CaptureHealth(null);
        }

        public Task<string?> StatisticsAsync(CancellationToken ct) =>
            PcapPath is { } path ? TsharkCapture.StatisticsAsync(path, log._logger, ct) : Task.FromResult<string?>(null);

        /// <summary>
        /// Detach. An attack thrown away before it stopped (a blip too short to report) gives its
        /// ring files back rather than deleting them.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            log.Detach(this);
            if (Interlocked.Exchange(ref _stopped, 1) == 1 || parts is null || !System.IO.Directory.Exists(parts))
                return ValueTask.CompletedTask;

            lock (log._ringGate)
            {
                foreach (var file in RingFiles(parts))
                {
                    try
                    {
                        file.MoveTo(Path.Combine(log._directory, file.Name));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log._logger.LogWarning(ex, "Could not return {File} to the ring", file.Name);
                    }
                }
            }
            log.TryDeleteDirectory(parts);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An attack's capture in two parts: a tshark stream for the breakdown, and the ring for the pcap.
/// </summary>
public sealed class CompositeCapture(IAttackCapture breakdown, IAttackCapture pcap) : IAttackCapture
{
    public string? PcapPath => pcap.PcapPath;

    public async Task<CaptureHealth> StopAsync()
    {
        var health = await breakdown.StopAsync().ConfigureAwait(false);
        await pcap.StopAsync().ConfigureAwait(false);
        return health;
    }

    public Task<string?> StatisticsAsync(CancellationToken ct) => pcap.StatisticsAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await breakdown.DisposeAsync().ConfigureAwait(false);
        await pcap.DisposeAsync().ConfigureAwait(false);
    }
}
