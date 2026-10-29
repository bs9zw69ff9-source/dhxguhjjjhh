using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Network;

/// <summary>
/// Always-on packet logging: a rolling pcap of every inbound header and a per-minute log of every
/// source address, so after a DDoS the data is there whether or not the attack was detected.
/// </summary>
/// <remarks>
/// WHY ALWAYS ON. The attack monitor only captures once it sees a flood, and the worst floods are
/// the ones it can miss: a host that null-routes the server cuts its traffic to nothing, and a
/// flood that fills the uplink takes the bot's own connection with it. A record that was being
/// written all along does not depend on any of that.
///
/// TWO TSHARK PROCESSES FOR THE LIFE OF THE BOT:
///   - the RING writes headers only (<c>-s 128</c>) to <c>ring_*.pcap</c> in 50 MB files, and this
///     class deletes the oldest past <c>PACKET_RING_MB</c>. tshark's own <c>-b files:</c> only
///     prunes the files of its current run, so every restart would leak a ring;
///   - the STREAM prints fields with every dissector but IP/UDP/TCP/ICMP off (see
///     <see cref="TsharkCapture"/>), feeding <see cref="SourceLog"/> and any attack attached to
///     it. An attack attaches here instead of starting its own tshark, so it costs no extra
///     capture. tshark keeps per-conversation state for as long as it runs, so the stream is
///     restarted every <see cref="RecycleStreamEvery"/> while no attack is attached.
///
/// AN ATTACK KEEPS ITS PART OF THE RING. While one is attached, every ring file tshark closes is
/// moved (a rename, no copy) into <c>attacks/attack-*.pcap.parts/</c>, out of reach of the
/// pruning, up to <see cref="AttackPcapBytes"/>. When it ends the parts and the file still being
/// written are joined with <c>mergecap</c> into <c>attacks/attack-*.pcap</c>. A bot that dies
/// mid-attack (or a mergecap that fails) leaves the parts folder behind, and the next start joins it.
///
/// THE FILES ARE ROOT'S. The logs hold every player's address; tshark writes its pcaps 0600, the
/// folder is 0700 and the CSVs are written 0600.
/// </remarks>
public sealed partial class PacketLog : BackgroundService
{
    /// <summary>Size of one ring file. Small enough that the ring rolls in steps, big enough not to churn.</summary>
    private const long RingFileKb = 51_200;

    /// <summary>The most one attack's pcap keeps: about 1.7 million headers. Ten are kept.</summary>
    internal const long AttackPcapBytes = 256L * 1024 * 1024;

    /// <summary>The share of the interface's packets the stream may miss before it counts as dropping.</summary>
    /// <remarks>The interface also counts what the capture filter leaves out (ARP, IPv6), so a perfect capture still trails it.</remarks>
    private const double UncountedShare = 0.01;

    private const string PartsSuffix = ".parts";

    internal static readonly TimeSpan RecycleStreamEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HousekeepEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RingCheckEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MergeTimeout = TimeSpan.FromMinutes(3);

    private readonly string _iface;
    private readonly string _directory;
    private readonly long _ringBytes;
    private readonly int _keepDays;
    private readonly IPlayerTraffic _players;
    private readonly TimeProvider _time;
    private readonly ILogger<PacketLog> _logger;
    private readonly SourceLog _sources = new();
    private readonly object _attachGate = new();
    private readonly object _ringGate = new();

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>> _mergecap;

    private volatile Attached[] _attached = [];
    private volatile IReadOnlySet<uint> _playerAddresses = new HashSet<uint>();
    private DateTimeOffset _minute;
    private volatile bool _running;
    private volatile string? _problem;

    public PacketLog(
        string iface, string directory, int ringMegabytes, int keepDays, IPlayerTraffic players,
        TimeProvider time, ILogger<PacketLog> logger,
        Func<IReadOnlyList<string>, CancellationToken, Task<ProcessOutcome>>? mergecap = null)
    {
        ArgumentNullException.ThrowIfNull(players);
        _iface = iface;
        _directory = directory;
        // Never less than two files: the one being written and the one before it.
        _ringBytes = Math.Max(RingFileKb * 1024 * 2, (long)ringMegabytes * 1024 * 1024);
        _keepDays = Math.Max(1, keepDays);
        _players = players;
        _time = time;
        _logger = logger;
        _mergecap = mergecap ?? ((argv, ct) => ProcessRunner.RunAsync("mergecap", argv, MergeTimeout, logger, ct));
        _minute = Floor(time.GetUtcNow());
    }

    /// <summary>Whether the stream is up, so an attack can attach to it instead of starting its own capture.</summary>
    public bool Running => _running;

    /// <summary>Why it is not running, when it is not.</summary>
    public string? Problem => _problem;

    public string Directory => _directory;

    public string Interface => _iface;

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
        [.. new DirectoryInfo(directory).EnumerateFiles("ring_*.pcap")
            .Select(f => (File: f, Match: RingName().Match(f.Name)))
            .Where(f => f.Match.Success)
            .OrderBy(f => f.Match.Groups[2].Value, StringComparer.Ordinal)
            .ThenBy(f => long.Parse(f.Match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(f => f.File)];

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
        _logger.LogInformation(
            "Packet log on {Interface}: headers to {Directory} (ring of {Ring} MB), source addresses every minute, kept {Days} days",
            _iface, _directory, _ringBytes / 1024 / 1024, _keepDays);

        Process? ring = null;
        string? reported = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            Process? stream = null;
            var recycled = false;
            try
            {
                if (ring is null || ring.HasExited)
                {
                    Stop(ring);
                    ring = Launch(RingArgv(_iface, _directory), readOutput: false);
                }
                stream = Launch(TsharkCapture.StreamArgv(_iface), readOutput: true);
                _running = true;
                _problem = null;
                reported = null;
                recycled = await ReadAsync(stream, ring, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _problem = "tshark is not installed - run `/packettools install`";
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            finally
            {
                _running = false;
                Stop(stream);
            }

            if (stoppingToken.IsCancellationRequested) break;
            if (recycled) continue;

            _problem ??= "tshark stopped";
            // Once per problem: a box without tshark would otherwise say so twice a minute forever.
            if (_problem != reported)
                _logger.LogWarning("Packet log is down ({Problem}); retrying every {Seconds}s", _problem, RestartAfter.TotalSeconds);
            reported = _problem;
            try
            {
                await Task.Delay(RestartAfter, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Stop(ring);
        await housekeeping.ConfigureAwait(false);
        Flush(_time.GetUtcNow());
    }

    private void Fail(string problem)
    {
        _problem = problem;
        _logger.LogWarning("Packet log is OFF: {Problem}", problem);
    }

    /// <summary>
    /// Feed every packet to the source log and the attached attacks. True when it stopped only to
    /// recycle the stream; false when tshark went away.
    /// </summary>
    private async Task<bool> ReadAsync(Process stream, Process ring, CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        var checkedRing = started;
        while (await stream.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (CapturedPacket.ParseFields(line) is { } packet)
            {
                _sources.Add(packet, _playerAddresses.Contains(packet.Source));
                foreach (var attached in _attached) attached.Add(packet);
            }

            var now = _time.GetUtcNow();
            if (now - checkedRing < RingCheckEvery) continue;
            checkedRing = now;

            if (ring.HasExited)
            {
                _problem = "the ring writer stopped";
                return false;
            }
            // Only between attacks: a restart costs a few seconds of capture.
            if (now - started >= RecycleStreamEvery && _attached.Length == 0) return true;
        }
        return false;
    }

    /// <summary>Every fifteen seconds: refresh who the players are, write a finished minute, keep the ring.</summary>
    private async Task HousekeepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HousekeepEvery, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    _playerAddresses = _players.OnlineAddresses();
                    var now = _time.GetUtcNow();
                    if (Floor(now) > _minute) Flush(now);
                    Housekeep(now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Packet log housekeeping failed; carrying on");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Count a packet into the source log, as the stream does. For tests.</summary>
    internal void Record(in CapturedPacket packet, bool fromPlayer)
    {
        _sources.Add(packet, fromPlayer);
        foreach (var attached in _attached) attached.Add(packet);
    }

    /// <summary>Append the finished minute's sources to its day's file.</summary>
    internal void Flush(DateTimeOffset now)
    {
        var minute = _minute;
        _minute = Floor(now);
        var lines = _sources.Flush(minute);
        if (lines.Count == 0) return;

        var path = Path.Combine(_directory, SourceLog.FileName(minute));
        try
        {
            var fresh = !File.Exists(path);
            using (var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)))
            {
                if (fresh) writer.Write(SourceLog.Header + "\n");
                writer.Write(SourceLog.Join(lines));
            }
            if (fresh && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the source log {Path}", path);
        }
    }

    /// <summary>Move closed ring files to the attacks they belong to, then hold the ring and the daily logs to their budgets.</summary>
    internal void Housekeep(DateTimeOffset now)
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

        var oldest = now.UtcDateTime.Date.AddDays(-_keepDays);
        foreach (var log in new DirectoryInfo(_directory).EnumerateFiles("sources-*.csv"))
        {
            if (DateTime.TryParseExact(log.Name["sources-".Length..^".csv".Length], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) &&
                day < oldest)
            {
                TryDelete(log.FullName);
            }
        }
    }

    /// <summary>
    /// Join what an attack left in a parts folder: one the bot was restarted during, or one
    /// mergecap failed on. Parts that still cannot be joined are kept for <c>PACKET_LOG_DAYS</c>.
    /// </summary>
    internal async Task RecoverAsync(CancellationToken ct)
    {
        if (AttacksDirectory() is not { } attacks || !System.IO.Directory.Exists(attacks)) return;

        foreach (var parts in System.IO.Directory.EnumerateDirectories(attacks, "attack-*.pcap" + PartsSuffix))
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

    /// <summary>The attack monitor's folder, beside this one.</summary>
    private string? AttacksDirectory() =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_directory)) is { Length: > 0 } parent ? Path.Combine(parent, "attacks") : null;

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
    /// Hand an attack the live stream: every packet from now until it stops goes to <paramref name="recorder"/>.
    /// </summary>
    /// <param name="pcapPath">Where the attack's pcap goes when it stops; null for none.</param>
    public IAttackCapture Attach(FloodRecorder recorder, Func<IReadOnlySet<uint>> players, string? pcapPath)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(players);

        string? parts = null;
        if (pcapPath is not null)
        {
            try
            {
                parts = pcapPath + PartsSuffix;
                System.IO.Directory.CreateDirectory(parts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not create {Parts}; the attack will have no pcap", parts);
                parts = null;
            }
        }

        // The ring file being written now holds the attack's first seconds; it is collected once it closes.
        var attached = new Attached(this, recorder, players, pcapPath, parts, _time.GetUtcNow(), LinkCounters.Read(_iface)?.Packets);
        lock (_attachGate) _attached = [.. _attached, attached];
        return attached;
    }

    private void Detach(Attached attached)
    {
        lock (_attachGate) _attached = [.. _attached.Where(a => a != attached)];
    }

    private static DateTimeOffset Floor(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);

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

    private static Process Launch(IReadOnlyList<string> argv, bool readOutput)
    {
        var info = new ProcessStartInfo("tshark")
        {
            UseShellExecute = false,
            RedirectStandardOutput = readOutput,
            // Inherited rather than redirected: a long-running tshark's stderr is start-up chatter,
            // it lands in the bot's journal, and an undrained pipe would eventually block it.
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

    /// <summary>An attack riding on the always-on stream.</summary>
    private sealed class Attached(
        PacketLog log, FloodRecorder recorder, Func<IReadOnlySet<uint>> players, string? target, string? parts,
        DateTimeOffset since, long? linkAtStart) : IAttackCapture
    {
        private long _delivered;
        private long _collectedBytes;
        private int _stopped;

        public string? PcapPath { get; private set; }

        public void Add(in CapturedPacket packet)
        {
            Interlocked.Increment(ref _delivered);
            recorder.Add(packet, players().Contains(packet.Source));
        }

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

        /// <summary>
        /// Stop receiving, join the attack's pcap, and judge the capture against the interface's own count.
        /// </summary>
        /// <remarks>
        /// A long-running tshark prints no drop count until it exits, so completeness is the packets it
        /// delivered against the packets the interface received.
        /// </remarks>
        public async Task<CaptureHealth> StopAsync()
        {
            if (Volatile.Read(ref _stopped) == 1) return Health();
            log.Detach(this);
            var health = Health();

            if (target is not null && parts is not null)
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
            return health;
        }

        private CaptureHealth Health()
        {
            var delivered = Interlocked.Read(ref _delivered);
            if (linkAtStart is not { } start || LinkCounters.Read(log._iface)?.Packets is not { } end) return new CaptureHealth(null, delivered);

            var missed = end - start - delivered;
            return new CaptureHealth(null, delivered, missed > (end - start) * UncountedShare ? missed : 0);
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
