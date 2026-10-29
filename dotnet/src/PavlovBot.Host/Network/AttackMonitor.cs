using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Network;

/// <summary>Who is playing right now, and from where.</summary>
public interface IPlayerTraffic
{
    /// <summary>The names of the players online across every server.</summary>
    IReadOnlyCollection<string> OnlineNames();

    /// <summary>Whether the rosters are fresh. False while RCON cannot be read - often the case under attack.</summary>
    bool RosterFresh { get; }

    /// <summary>The addresses of the players online now, as <see cref="CapturedPacket.Source"/> numbers.</summary>
    IReadOnlySet<uint> OnlineAddresses();

    /// <summary>Every confirmed address of a player seen recently, online or not: who the shield must never block.</summary>
    IReadOnlySet<uint> KnownAddresses();
}

/// <summary>A running capture: started when an attack may be coming, stopped when it is over.</summary>
public interface IAttackCapture : IAsyncDisposable
{
    string? PcapPath { get; }

    Task<CaptureHealth> StopAsync();

    /// <summary>Wireshark's statistics for the pcap, once stopped. Null when there is none.</summary>
    Task<string?> StatisticsAsync(CancellationToken ct);
}

/// <summary>What else wants to know about floods: the anti-DDoS service, for its event log and logging mode.</summary>
/// <remarks>Called on the monitor's one-second tick; an implementation must be quick and must not throw.</remarks>
public interface IAttackListener
{
    /// <summary>Every second's link reading, whether or not anything is happening.</summary>
    void OnSecond(LinkSecond second);

    void OnStarted(DateTimeOffset at, LinkSecond second, ShieldOutcome? shield);

    void OnEnded(AttackReport report);
}

/// <summary>
/// Watches the link for floods: an alert when one starts, a summary and a report when it ends.
/// </summary>
/// <remarks>
/// DETECT AND REPORT ONLY. Nothing here touches the firewall - the decision that a flood should
/// be dropped is left to whoever reads the report, because a wrong automatic block lands on
/// real players.
///
/// THE CAPTURE STARTS EARLY. tshark is switched on at half the trigger, so the breakdown already
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
    private static readonly TimeSpan RefreshAllowListEvery = TimeSpan.FromMinutes(1);

    /// <summary>The longest timeline one report keeps: four hours of seconds.</summary>
    private const int MaxTimeline = 4 * 60 * 60;

    /// <summary>How many pcaps and reports are kept on disk; older ones are deleted.</summary>
    private const int PcapsKept = 10;

    /// <summary>How often a summary that could not be posted is tried again.</summary>
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    /// <summary>The most summaries held back while Discord is unreachable; the oldest go first past this.</summary>
    private const int MaxPending = 20;

    private readonly ulong _channel;
    private readonly string _host = Environment.MachineName;
    private readonly string? _iface;
    private readonly string? _pcapDirectory;
    private readonly Func<string, (long Packets, long Bytes)?> _readCounters;
    private readonly IPlayerTraffic _players;
    private readonly Func<string, string?, FloodRecorder, Func<IReadOnlySet<uint>>, (IAttackCapture? Capture, string? Problem)> _startCapture;
    private readonly IFloodShield _shield;
    private readonly Func<bool> _discordConnected;
    private readonly Func<string, CancellationToken, Task<string?>> _iftopSnapshot;
    private readonly IAttackListener? _listener;
    private readonly List<AttackReport> _pending = [];
    private DateTimeOffset _lastRetry;
    private int _offlineSeconds;
    private DateTimeOffset? _offlineFrom;
    private DateTimeOffset _offlineUntil;
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
    private readonly PlayerDropTracker _drops = new();
    private int _activeSeconds;
    private ulong? _alertMessage;
    private DateTimeOffset _lastLive;
    /// <summary>Replaced whole on the timer thread, read by tshark's reader: volatile, never mutated.</summary>
    private volatile IReadOnlySet<uint> _playerAddresses = new HashSet<uint>();
    private DateTimeOffset _playersRefreshed;
    private ShieldOutcome? _shieldOutcome;
    private DateTimeOffset _allowRefreshed;
    private Task<string?>? _iftop;

    public AttackMonitor(
        ulong channel,
        string? iface,
        FloodThresholds thresholds,
        string? pcapDirectory,
        Func<string, (long Packets, long Bytes)?> readCounters,
        IPlayerTraffic players,
        Func<string, string?, FloodRecorder, Func<IReadOnlySet<uint>>, (IAttackCapture? Capture, string? Problem)> startCapture,
        IFloodShield shield,
        IAutoPostTarget post,
        IFilePostTarget files,
        TimeProvider time,
        ILogger<AttackMonitor> logger,
        Func<bool>? discordConnected = null,
        Func<string, CancellationToken, Task<string?>>? iftopSnapshot = null,
        IAttackListener? listener = null)
    {
        _listener = listener;
        _discordConnected = discordConnected ?? (() => true);
        _iftopSnapshot = iftopSnapshot ?? ((iface, token) => IftopSnapshot.TakeAsync(iface, logger, token));
        _channel = channel;
        _iface = iface;
        _pcapDirectory = pcapDirectory;
        _readCounters = readCounters;
        _players = players;
        _startCapture = startCapture;
        _shield = shield;
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

        // Stand the shield up with the limits off, so blocks carried over from before a restart still apply.
        if (_shield.Enabled && await _shield.ApplyAsync(engaged: false, stoppingToken).ConfigureAwait(false) is { } standBy)
            _logger.LogWarning("DDoS shield is unavailable: {Problem}", standBy);

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

            // Never leave the limits on behind a bot that is no longer watching to lift them.
            if (_shieldOutcome is { Engaged: true }) await _shield.ApplyAsync(engaged: false, CancellationToken.None).ConfigureAwait(false);
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
        Notify(l => l.OnSecond(second));
        var names = _players.OnlineNames();
        var online = names.Count;
        RefreshPlayerAddresses(now);
        var wasRunning = _detector.InAttack || _detector.RunStartedAt is not null;
        var signal = _detector.Observe(second);

        if (signal is FloodSignal.Watch or FloodSignal.Started or FloodSignal.Ongoing) _lastWatch = now;
        if (signal != FloodSignal.Quiet && _capture is null && _captureProblem is null) StartCapture(now);

        if (_detector.InAttack || _detector.RunStartedAt is not null || signal == FloodSignal.Ended)
        {
            if (_timeline.Count < MaxTimeline) _timeline.Add(second);
            if (_detector.IsOver(second)) _activeSeconds++;
            _drops.Observe(names, now, _players.RosterFresh);
        }
        else if (!wasRunning)
        {
            _timeline.Clear();
            _activeSeconds = 0;
            _playersBefore = online;
            _drops.Baseline(names);
        }

        if (_detector.InAttack)
        {
            _playersLowest = Math.Min(_playersLowest, online);

            // A bot cut off from Discord mid-attack is a bot whose own connection was full.
            if (!_discordConnected())
            {
                _offlineSeconds++;
                _offlineFrom ??= now;
                _offlineUntil = now;
            }
        }

        if (_pending.Count > 0 && now - _lastRetry >= RetryEvery && _discordConnected())
        {
            _lastRetry = now;
            await RetryPendingAsync(ct).ConfigureAwait(false);
        }

        switch (signal)
        {
            case FloodSignal.Started:
                _playersLowest = online;
                await EngageShieldAsync(now, ct).ConfigureAwait(false);
                _iftop = Task.Run(() => _iftopSnapshot(_iface!, CancellationToken.None), CancellationToken.None);
                _logger.LogWarning("ATTACK started on {Interface}: {Pps} pkt/s, {Mbps:0.0} Mbit/s", _iface, second.Packets, second.Mbps);
                Notify(l => l.OnStarted(now, second, _shieldOutcome));
                _alertMessage = await _post.SendAsync(_channel, AttackEmbeds.Started(_host, _iface!, second, _detector, _shieldOutcome).Build(), null, ct)
                    .ConfigureAwait(false);
                _lastLive = now;
                break;

            case FloodSignal.Ongoing when _shieldOutcome is { Engaged: true } && now - _allowRefreshed >= RefreshAllowListEvery:
                _allowRefreshed = now;
                await _shield.AllowAsync(_players.KnownAddresses(), ct).ConfigureAwait(false);
                break;

            case FloodSignal.Ongoing when _alertMessage is { } id && now - _lastLive >= LiveEvery:
                _lastLive = now;
                await _post.EditAsync(_channel, id, AttackEmbeds.Live(_host, _iface!, _timeline, _activeSeconds, _detector, _drops.Drops, _shieldOutcome).Build(), null, ct)
                    .ConfigureAwait(false);
                break;

            case FloodSignal.Ended:
                await FinishAsync(now, online, ct).ConfigureAwait(false);
                break;
        }

        // A watch that never became an attack: drop the capture rather than keep tshark running.
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

    private void Notify(Action<IAttackListener> call)
    {
        if (_listener is null) return;
        try
        {
            call(_listener);
        }
        catch (Exception ex)
        {
            // The listener is bookkeeping; a fault in it must never cost the monitor a tick.
            _logger.LogWarning(ex, "Attack listener failed; carrying on");
        }
    }

    /// <summary>
    /// Turn the limits on, players first.
    /// </summary>
    /// <remarks>
    /// THE ALLOW LIST GOES IN BEFORE THE LIMITS. Otherwise the second between the two would be one
    /// in which a real player sending a burst could be blocked for ten minutes.
    /// </remarks>
    private async Task EngageShieldAsync(DateTimeOffset now, CancellationToken ct)
    {
        _shieldOutcome = null;
        if (!_shield.Enabled) return;

        var standBy = await _shield.ApplyAsync(engaged: false, ct).ConfigureAwait(false);
        if (standBy is null) await _shield.AllowAsync(_players.KnownAddresses(), ct).ConfigureAwait(false);
        var problem = standBy ?? await _shield.ApplyAsync(engaged: true, ct).ConfigureAwait(false);

        _allowRefreshed = now;
        _shieldOutcome = new ShieldOutcome(problem, null, _shield.Limits);
        if (problem is not null) _logger.LogWarning("DDoS shield could not engage: {Problem}", problem);
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
        var statistics = _capture is null ? null : await _capture.StatisticsAsync(ct).ConfigureAwait(false);

        ShieldOutcome? shield = null;
        if (_shieldOutcome is { } outcome)
        {
            var stats = outcome.Engaged ? await _shield.StatsAsync(ct).ConfigureAwait(false) : null;
            shield = outcome with { Stats = stats };
            if (outcome.Engaged) await _shield.ApplyAsync(engaged: false, ct).ConfigureAwait(false);
        }

        var iftop = _iftop is null ? null : await _iftop.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        var report = new AttackReport(
            _host, _iface!,
            Start: _timeline.Count > 0 ? _timeline[0].At : now,
            End: now,
            Timeline: [.. _timeline],
            ActiveSeconds: _activeSeconds,
            Breakdown: _capture is null ? FloodBreakdown.Empty : _recorder.Snapshot(),
            Players: new PlayerImpact(_playersBefore, _playersLowest, online, _playerAddresses.Count, _drops.Drops, _drops.StaleSeconds),
            Capture: health,
            PcapPath: _capture?.PcapPath,
            Statistics: statistics,
            Shield: shield,
            Iftop: iftop,
            Connection: _offlineFrom is { } from ? new ConnectionLoss(_offlineSeconds, from, _offlineUntil) : null);

        _logger.LogWarning("ATTACK ended on {Interface}: {Verdict}", _iface, report.Verdict);
        Notify(l => l.OnEnded(report));

        // ON DISK FIRST, so a report survives a Discord that is still unreachable - or a bot restart.
        SaveReport(report);

        if (await PostAsync(report, postedLate: false, ct).ConfigureAwait(false) is null)
        {
            _logger.LogWarning("Could not post the attack summary; it will be retried every {Seconds}s until Discord answers",
                RetryEvery.TotalSeconds);
            if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
            _pending.Add(report);
            _lastRetry = now;
        }

        if (_capture is not null) await _capture.DisposeAsync().ConfigureAwait(false);
        _capture = null;
        _captureProblem = null;
        _alertMessage = null;
        _shieldOutcome = null;
        _iftop = null;
        _offlineSeconds = 0;
        _offlineFrom = null;
        _timeline.Clear();
        _activeSeconds = 0;
        _playersBefore = online;
        _drops.Baseline(_players.OnlineNames());
    }

    private static string ReportName(AttackReport report) => $"attack-{report.Start.UtcDateTime:yyyyMMdd-HHmmss}.txt";

    private Task<ulong?> PostAsync(AttackReport report, bool postedLate, CancellationToken ct) =>
        _files.SendWithFileAsync(_channel, AttackEmbeds.Summary(report, postedLate).Build(), ReportName(report), report.ToText(), ct);

    /// <summary>Post the held-back summaries, oldest first, stopping at the first that still fails.</summary>
    private async Task RetryPendingAsync(CancellationToken ct)
    {
        while (_pending.Count > 0)
        {
            if (await PostAsync(_pending[0], postedLate: true, ct).ConfigureAwait(false) is null) return;
            _logger.LogInformation("Posted the held-back summary of the attack at {Start}", _pending[0].Start);
            _pending.RemoveAt(0);
        }
    }

    /// <summary>The report beside its pcap, so nothing depends on Discord being reachable.</summary>
    private void SaveReport(AttackReport report)
    {
        if (_pcapDirectory is null) return;
        try
        {
            PrepareDirectory(_pcapDirectory);
            File.WriteAllText(Path.Combine(_pcapDirectory, ReportName(report)), report.ToText());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the attack report to {Directory}", _pcapDirectory);
        }
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
            foreach (var pattern in new[] { "attack-*.pcap", "attack-*.txt" })
            {
                foreach (var old in Directory.EnumerateFiles(directory, pattern)
                             .OrderByDescending(f => f, StringComparer.Ordinal).Skip(PcapsKept - 1))
                {
                    File.Delete(old);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prepare {Directory} for attack pcaps", directory);
        }
    }
}
