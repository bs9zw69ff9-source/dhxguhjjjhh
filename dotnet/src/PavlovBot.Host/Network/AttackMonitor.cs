using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Network;

/// <summary>Who is playing right now, and from where.</summary>
public interface IPlayerTraffic
{
    /// <summary>Players online across every server.</summary>
    int OnlineCount { get; }

    /// <summary>The addresses of the players online now, as <see cref="CapturedPacket.Source"/> numbers.</summary>
    IReadOnlySet<uint> OnlineAddresses();
}

/// <summary>A running capture: started when an attack may be coming, stopped when it is over.</summary>
public interface IAttackCapture : IAsyncDisposable
{
    string? PcapPath { get; }

    Task<CaptureHealth> StopAsync();
}

/// <summary>
/// Watches the link for floods: an alert when one starts, a summary and a report when it ends.
/// </summary>
/// <remarks>
/// DETECT AND REPORT ONLY. Nothing here touches the firewall - the decision that a flood should
/// be dropped is left to whoever reads the report, because a wrong automatic block lands on
/// real players.
///
/// THE CAPTURE STARTS EARLY. tcpdump is switched on at half the trigger, so the breakdown already
/// covers the seconds that took traffic over it; waiting for the attack to be declared would lose
/// its opening, which is usually its peak. If traffic falls back without becoming an attack, the
/// capture is stopped and thrown away after a minute.
///
/// ONE TICK A SECOND, AND A TICK NEVER THROWS. A monitor that dies on its first odd sample is
/// worse than none, because it looks like quiet.
/// </remarks>
public sealed class AttackMonitor : BackgroundService
{
    private static readonly TimeSpan LiveEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbandonWatchAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshPlayersEvery = TimeSpan.FromSeconds(15);

    /// <summary>The longest timeline one report keeps: four hours of seconds.</summary>
    private const int MaxTimeline = 4 * 60 * 60;

    /// <summary>How many pcaps are kept on disk; older ones are deleted.</summary>
    private const int PcapsKept = 10;

    private readonly ulong _channel;
    private readonly string _host = Environment.MachineName;
    private readonly string? _iface;
    private readonly string? _pcapDirectory;
    private readonly Func<string, (long Packets, long Bytes)?> _readCounters;
    private readonly IPlayerTraffic _players;
    private readonly Func<string, string?, FloodRecorder, Func<IReadOnlySet<uint>>, (IAttackCapture? Capture, string? Problem)> _startCapture;
    private readonly IAutoPostTarget _post;
    private readonly IFilePostTarget _files;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly FloodDetector _detector;

    private (long Packets, long Bytes)? _last;
    private readonly List<LinkSecond> _timeline = [];
    private IAttackCapture? _capture;
    private string? _captureProblem;
    private FloodRecorder _recorder = new();
    private DateTimeOffset _lastWatch;
    private int _playersBefore;
    private int _playersLowest;
    private int _activeSeconds;
    private ulong? _alertMessage;
    private DateTimeOffset _lastLive;
    /// <summary>Replaced whole on the timer thread, read by tcpdump's reader: volatile, never mutated.</summary>
    private volatile IReadOnlySet<uint> _playerAddresses = new HashSet<uint>();
    private DateTimeOffset _playersRefreshed;

    public AttackMonitor(
        ulong channel,
        string? iface,
        FloodThresholds thresholds,
        string? pcapDirectory,
        Func<string, (long Packets, long Bytes)?> readCounters,
        IPlayerTraffic players,
        Func<string, string?, FloodRecorder, Func<IReadOnlySet<uint>>, (IAttackCapture? Capture, string? Problem)> startCapture,
        IAutoPostTarget post,
        IFilePostTarget files,
        TimeProvider time,
        ILogger<AttackMonitor> logger)
    {
        _channel = channel;
        _iface = iface;
        _pcapDirectory = pcapDirectory;
        _readCounters = readCounters;
        _players = players;
        _startCapture = startCapture;
        _post = post;
        _files = files;
        _time = time;
        _logger = logger;
        _detector = new FloodDetector(thresholds);
    }

    /// <summary>The interface being watched, or null when none could be found.</summary>
    public string? Interface => _iface;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_iface is null)
        {
            _logger.LogWarning("Attack monitor is OFF: no interface - set ATTACK_INTERFACE, the default route could not be read");
            return;
        }

        _logger.LogInformation("Attack monitor watching {Interface}: an attack is {Pps} pkt/s or {Mbps} Mbit/s, " +
                               "or {Factor}x normal, for {Seconds}s. Alerts to channel {Channel}",
            _iface, _detector.Thresholds.Pps, _detector.Thresholds.Mbps, _detector.Thresholds.BaselineFactor,
            _detector.Thresholds.StartSeconds, _channel);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Attack monitor tick failed; carrying on");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One second: read the counters, decide, act.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_readCounters(_iface!) is not { } counters) return;

        var previous = _last;
        _last = counters;

        // The first read has nothing to subtract from; a counter that went backwards was reset.
        if (previous is not { } before || counters.Packets < before.Packets || counters.Bytes < before.Bytes) return;

        var second = new LinkSecond(now, counters.Packets - before.Packets, counters.Bytes - before.Bytes);
        var online = _players.OnlineCount;
        RefreshPlayerAddresses(now);
        var wasRunning = _detector.InAttack || _detector.RunStartedAt is not null;
        var signal = _detector.Observe(second);

        if (signal is FloodSignal.Watch or FloodSignal.Started or FloodSignal.Ongoing) _lastWatch = now;
        if (signal != FloodSignal.Quiet && _capture is null && _captureProblem is null) StartCapture(now);

        if (_detector.InAttack || _detector.RunStartedAt is not null || signal == FloodSignal.Ended)
        {
            if (_timeline.Count < MaxTimeline) _timeline.Add(second);
            if (_detector.IsOver(second)) _activeSeconds++;
        }
        else if (!wasRunning)
        {
            _timeline.Clear();
            _activeSeconds = 0;
            _playersBefore = online;
        }

        if (_detector.InAttack) _playersLowest = Math.Min(_playersLowest, online);

        switch (signal)
        {
            case FloodSignal.Started:
                _playersLowest = online;
                _logger.LogWarning("ATTACK started on {Interface}: {Pps} pkt/s, {Mbps:0.0} Mbit/s", _iface, second.Packets, second.Mbps);
                _alertMessage = await _post.SendAsync(_channel, AttackEmbeds.Started(_host, _iface!, second, _detector).Build(), null, ct)
                    .ConfigureAwait(false);
                _lastLive = now;
                break;

            case FloodSignal.Ongoing when _alertMessage is { } id && now - _lastLive >= LiveEvery:
                _lastLive = now;
                await _post.EditAsync(_channel, id, AttackEmbeds.Live(_host, _iface!, _timeline, _activeSeconds, _detector).Build(), null, ct)
                    .ConfigureAwait(false);
                break;

            case FloodSignal.Ended:
                await FinishAsync(now, online, ct).ConfigureAwait(false);
                break;
        }

        // A watch that never became an attack: drop the capture rather than keep tcpdump running.
        if (!_detector.InAttack && _detector.RunStartedAt is null && signal != FloodSignal.Ended &&
            _capture is not null && now - _lastWatch >= AbandonWatchAfter)
        {
            await _capture.StopAsync().ConfigureAwait(false);
            await DeleteAsync(_capture).ConfigureAwait(false);
            _capture = null;
        }

        // A capture that could not start is retried on the next attack, not every second of this one.
        if (signal == FloodSignal.Quiet && _capture is null) _captureProblem = null;
    }

    private void StartCapture(DateTimeOffset now)
    {
        _recorder = new FloodRecorder();
        var pcap = _pcapDirectory is null ? null : Path.Combine(_pcapDirectory, $"attack-{now.UtcDateTime:yyyyMMdd-HHmmss}.pcap");
        if (pcap is not null) PrepareDirectory(_pcapDirectory!);

        var (capture, problem) = _startCapture(_iface!, pcap, _recorder, () => _playerAddresses);
        _capture = capture;
        _captureProblem = problem;
        if (problem is not null) _logger.LogWarning("Attack monitor cannot capture packets: {Problem}", problem);
    }

    /// <summary>The online players' addresses, refreshed at most every fifteen seconds, on this thread only.</summary>
    private void RefreshPlayerAddresses(DateTimeOffset now)
    {
        if (now - _playersRefreshed < RefreshPlayersEvery) return;
        _playersRefreshed = now;
        _playerAddresses = _players.OnlineAddresses();
    }

    private async Task FinishAsync(DateTimeOffset now, int online, CancellationToken ct)
    {
        var health = _capture is null
            ? new CaptureHealth(_captureProblem ?? "no capture was running")
            : await _capture.StopAsync().ConfigureAwait(false);

        var report = new AttackReport(
            _host, _iface!,
            Start: _timeline.Count > 0 ? _timeline[0].At : now,
            End: now,
            Timeline: [.. _timeline],
            ActiveSeconds: _activeSeconds,
            Breakdown: _capture is null ? FloodBreakdown.Empty : _recorder.Snapshot(),
            Players: new PlayerImpact(_playersBefore, _playersLowest, online, _playerAddresses.Count),
            Capture: health,
            PcapPath: _capture?.PcapPath);

        _logger.LogWarning("ATTACK ended on {Interface}: {Verdict}", _iface, report.Verdict);

        var fileName = $"attack-{report.Start.UtcDateTime:yyyyMMdd-HHmmss}.txt";
        await _files.SendWithFileAsync(_channel, AttackEmbeds.Summary(report).Build(), fileName, report.ToText(), ct)
            .ConfigureAwait(false);

        if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
        _capture = null;
        _captureProblem = null;
        _alertMessage = null;
        _timeline.Clear();
        _activeSeconds = 0;
        _playersBefore = online;
    }

    private async Task DeleteAsync(IAttackCapture capture)
    {
        await capture.DisposeAsync().ConfigureAwait(false);
        if (capture.PcapPath is not { } path) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the unused pcap {Path}", path);
        }
    }

    /// <summary>Create the pcap folder, and keep only the newest <see cref="PcapsKept"/> in it.</summary>
    private void PrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory, "attack-*.pcap")
                         .OrderByDescending(f => f, StringComparer.Ordinal).Skip(PcapsKept - 1))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prepare {Directory} for attack pcaps", directory);
        }
    }
}
