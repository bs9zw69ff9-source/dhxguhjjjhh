using System.Globalization;
using System.Threading.Channels;
using Discord;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Network;

/// <summary>A detection and what was done about it, for <c>/antiddos detections</c>.</summary>
public sealed record DetectionRecord(DateTimeOffset At, PlannedResponse Plan, string? BanProblem);

/// <summary>
/// The anti-DDoS loop: once a minute, read the kernel's counters, write the traffic and source
/// records, judge them, respond, keep CrowdSec in step, and keep the logs inside the disk.
/// </summary>
/// <remarks>
/// NOTHING HERE IS PER PACKET. The kernel counts and filters; this reads one <c>nft -j list
/// table</c> a minute and a handful of <c>cscli</c> calls. A flood of a million packets a second
/// costs this loop exactly what a quiet minute does.
///
/// A STEP THAT FAILS NEVER STOPS THE OTHERS, AND NEVER TOUCHES THE FILTERS. The firewall rules
/// live in the kernel; if this loop dies they keep filtering. Logging, CrowdSec polling and
/// storage each run in their own try, and the only firewall change this loop makes on its own is
/// the logging mode - never a drop rule.
///
/// MODES. The resting mode is <c>ANTIDDOS_LOG_MODE</c>. An owner can raise it for a while; a
/// detected flood raises it to incident for <c>ANTIDDOS_INCIDENT_MINUTES</c>; both lapse on their
/// own. Storage pressure caps whatever was asked. The mode actually loaded is always
/// <c>min(requested, what the disk allows)</c>.
///
/// WORK FROM OTHER THREADS QUEUES. The attack monitor's callbacks and the Discord command's
/// changes go through one channel, processed in order by one worker, so a firewall change never
/// races another and no task is left unobserved.
/// </remarks>
public sealed class AntiDdosService : BackgroundService, IAttackListener
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(10);

    /// <summary>Source records written per minute: the busiest. The traffic record counts all of them.</summary>
    internal const int SourcesPerWindow = 100;

    private const int DetectionsKept = 200;

    /// <summary>How far past the reserve the guard evicts to, so it does not evict a file every minute.</summary>
    private const double EvictTo = 0.9;

    private readonly AntiDdosOptions _configured;
    private readonly ShieldLimits _configuredLimits;
    private readonly string _iface;
    private readonly IAntiDdosFirewall _firewall;
    private readonly ICrowdSec _crowdsec;
    private readonly AntiDdosLog _log;
    private readonly AntiDdosStateStore _state;
    private readonly IPlayerTraffic _players;
    private readonly PacketLog? _capture;
    private readonly IAutoPostTarget _post;
    private readonly Func<string, (long Packets, long Bytes)?> _readCounters;
    private readonly Func<string, (long Free, long Total)?> _disk;
    private readonly TimeProvider _time;
    private readonly ILogger<AntiDdosService> _logger;
    private readonly ResponsePlanner _planner;
    private readonly Channel<Func<CancellationToken, Task>> _work = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _detectionsGate = new();
    private readonly LinkedList<DetectionRecord> _detections = new();
    private readonly HashSet<string> _heldRecorded = new(StringComparer.Ordinal);

    private Accounting? _lastAccounting;
    private HashSet<string>? _lastBlocked;

    /// <summary>
    /// Addresses this service itself put in the kernel's block list (the fallback without
    /// CrowdSec), so they are not mistaken for the firewall's own blocks and escalated again.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _selfBlocked = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, Counter>? _lastCounters;
    private (long Packets, long Bytes)? _lastLink;
    private readonly Func<string, LinkHealth?> _readHealth;
    private readonly Func<string, string?> _lastLine;

    /// <summary>Each log source's parser probe: when it ran, and the fault it found (null: parsing works).</summary>
    private readonly Dictionary<string, (DateTimeOffset At, string? Problem)> _probes = new(StringComparer.Ordinal);

    /// <summary>How long a probe's verdict stands. It spawns a short crowdsec run, so not every health check.</summary>
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromHours(6);
    private readonly ReadingStore? _readings;
    private LinkHealth? _lastLinkHealth;
    private bool? _linkUp;
    private bool _conntrackFull;

    /// <summary>Alerts Discord did not take, oldest first, with when they were raised. Posted when it answers again.</summary>
    private readonly Queue<(EmbedBuilder Embed, DateTimeOffset At, string? Footer)> _outbox = new();
    private int _outboxDropped;

    /// <summary>The most alerts held back while Discord is unreachable; past it the oldest go (they are all in the event log).</summary>
    internal const int OutboxSize = 50;
    private DateTimeOffset _lastWindow;
    private long _peakPps;
    private Dictionary<long, CrowdSecDecision>? _lastDecisions;
    private DateTimeOffset _lastHealth = DateTimeOffset.MinValue;
    private string? _healthProblem;
    private StorageLevel _storageLevel = StorageLevel.Ok;
    private string? _incident;

    /// <summary>When the last flood ended; the minute's judging lags it, so detections just after still count as part of it.</summary>
    private DateTimeOffset? _floodEnded;
    private (LogMode Mode, DateTimeOffset At)? _modeFailure;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AntiDdosService(
        AntiDdosOptions options,
        ShieldLimits limits,
        string iface,
        IAntiDdosFirewall firewall,
        ICrowdSec crowdsec,
        AntiDdosLog log,
        AntiDdosStateStore state,
        IPlayerTraffic players,
        PacketLog? capture,
        IAutoPostTarget post,
        Func<string, (long Packets, long Bytes)?> readCounters,
        Func<string, (long Free, long Total)?> disk,
        TimeProvider time,
        ILogger<AntiDdosService> logger,
        Func<string, LinkHealth?>? readHealth = null,
        ReadingStore? readings = null,
        Func<string, string?>? lastLine = null)
    {
        _lastLine = lastLine ?? LogTail.LastLine;
        _readHealth = readHealth ?? (_ => null);
        _readings = readings;
        _configured = options;
        _configuredLimits = limits;
        _iface = iface;
        _firewall = firewall;
        _crowdsec = crowdsec;
        _log = log;
        _state = state;
        _players = players;
        _capture = capture;
        _post = post;
        _readCounters = readCounters;
        _disk = disk;
        _time = time;
        _logger = logger;
        Options = Thresholds.Apply(options, limits, state.Read().Overrides, out var applied);
        Limits = applied;
        _planner = new ResponsePlanner(Options.Response);
        _lastWindow = time.GetUtcNow();
    }

    /// <summary>The settings in force: <c>.env</c> with the owner's overrides on top.</summary>
    public AntiDdosOptions Options { get; private set; }

    public ShieldLimits Limits { get; private set; }

    public IAntiDdosFirewall Firewall => _firewall;

    public ICrowdSec CrowdSec => _crowdsec;

    public AntiDdosLog Log => _log;

    public PacketLog? Capture => _capture;

    public string Interface => _iface;

    public StorageVerdict? Storage { get; private set; }

    public TrafficWindow? LastWindow { get; private set; }

    public IReadOnlyList<CrowdSecDecision> Decisions => _lastDecisions?.Values.ToList() ?? [];

    public string? CrowdSecProblem { get; private set; }

    public string? HealthProblem => _healthProblem;

    public string? CurrentIncident => _incident;

    /// <summary>The mode asked for now: an owner's or a flood's while it lasts, else the configured one.</summary>
    public (LogMode Mode, DateTimeOffset? Until, string? By) Requested
    {
        get
        {
            var s = _state.Read();
            return s.Mode is { } mode && s.ModeUntil is { } until && until > _time.GetUtcNow()
                ? (mode, until, s.ModeBy)
                : (_configured.LogMode, null, null);
        }
    }

    public IReadOnlyList<TrustedEntry> TrustedEntries() => _state.Read().Trusted;

    public IReadOnlyList<DetectionRecord> RecentDetections()
    {
        lock (_detectionsGate) return [.. _detections];
    }

    /// <summary>The settings the firewall should have now.</summary>
    internal FirewallSettings FirewallSettings() => _firewall.Settings with
    {
        Interface = _iface,
        Limits = Limits,
        SshPorts = Options.SshPorts,
        SshNewPerMinute = Options.SshNewPerMinute,
        IcmpEchoPps = Options.IcmpEchoPps,
        SuspectUdpPps = Options.SuspectUdpPps,
        SuspectSynPerSecond = Options.SuspectSynPerSecond,
        NflogGroup = Options.NflogGroup,
        AlwaysPerSource = Options.AlwaysBlock,
        SynBlockPps = Options.SynBlockPps,
    };

    // ---- the attack monitor's callbacks: quick, never throwing, work queued ----

    public void OnSecond(LinkSecond second)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref _peakPps);
            if (second.Packets <= seen) return;
        }
        while (Interlocked.CompareExchange(ref _peakPps, second.Packets, seen) != seen);
    }

    public void OnStarted(DateTimeOffset at, LinkSecond second, ShieldOutcome? shield)
    {
        var incident = "flood-" + AntiDdosLog.FileStamp(at);
        _incident = incident;
        Enqueue(async ct =>
        {
            await _log.EventAsync(new AntiDdosEvent(at, "flood-start", "critical", Action: shield is { Engaged: true } ? "limits-engaged" : "detected",
                Reason: string.Create(CultureInfo.InvariantCulture, $"{second.Packets} pkt/s, {second.Mbps:0.0} Mbit/s on {_iface}"),
                Actor: "auto", Incident: incident,
                Detail: shield?.Problem is { } p ? new Dictionary<string, string> { ["shield_problem"] = p } : null), ct).ConfigureAwait(false);

            if (_configured.AutoIncident)
            {
                var until = at + TimeSpan.FromMinutes(_configured.IncidentMinutes);
                // A flood only ever raises or extends the mode; it never shortens an owner's longer one.
                var current = Requested;
                if (current.Mode < LogMode.Incident || current.Until is null || current.Until < until)
                    await RequestModeAsync(LogMode.Incident, until, "auto (flood)", incident, ct).ConfigureAwait(false);
            }
        });
    }

    public void OnEnded(AttackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var incident = _incident ?? "flood-" + AntiDdosLog.FileStamp(report.Start);
        _incident = null;
        Enqueue(_ =>
        {
            _floodEnded = report.End;
            return Task.CompletedTask;
        });
        Enqueue(ct => _log.EventAsync(new AntiDdosEvent(report.End, "flood-end", "warning", Action: "ended", Reason: report.Verdict,
            Actor: "auto", Incident: incident, Detail: new Dictionary<string, string>
            {
                ["start"] = AntiDdosRecords.Stamp(report.Start),
                ["link_packets"] = report.LinkPackets.ToString(CultureInfo.InvariantCulture),
                ["peak_pps"] = report.Peak.Packets.ToString(CultureInfo.InvariantCulture),
                ["dropped_by_filter"] = (report.Shield?.Stats?.Dropped ?? 0).ToString(CultureInfo.InvariantCulture),
                ["blocked_sources"] = (report.Shield?.Stats?.BlockedSources ?? 0).ToString(CultureInfo.InvariantCulture),
                ["pcap"] = report.PcapPath ?? "",
            }), ct));
    }

    private void Enqueue(Func<CancellationToken, Task> work)
    {
        if (!_work.Writer.TryWrite(work)) _logger.LogWarning("Anti-DDoS work queue is closed; a change was dropped");
    }

    /// <summary>Run a change on the worker and wait for it, so a command gets the real outcome.</summary>
    private Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async ct =>
        {
            try
            {
                done.TrySetResult(await work(ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    // ---- the loop ----

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = Task.Run(() => WorkAsync(stoppingToken), CancellationToken.None);
        try
        {
            try
            {
                await StartUpAsync(stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                _started.TrySetResult();
            }

            using var timer = new PeriodicTimer(Window, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Enqueue(async ct =>
                {
                    try
                    {
                        await TickAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        done.TrySetResult();
                    }
                });
                await done.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            _work.Writer.TryComplete();
            await worker.ConfigureAwait(false);
            // The last reading, so the gap until the next start is recorded from it.
            SaveReading();
            // The rules stay: a stopped bot leaves its blocks and filters in the kernel.
            await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "service", "info", Action: "stopped",
                Reason: "the firewall rules stay loaded", Actor: "bot"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var work in _work.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anti-DDoS work item failed; carrying on");
            }
        }
    }

    private async Task StartUpAsync(CancellationToken ct)
    {
        PrepareDirectory();

        var settings = FirewallSettings();
        var problem = await _firewall.ConfigureAsync(settings, ct).ConfigureAwait(false);
        var trusted = _state.Read().Trusted.Select(t => t.Network).ToList();
        problem ??= await _firewall.SetTrustedAsync(trusted, ct).ConfigureAwait(false);
        await ApplyModeAsync(ct).ConfigureAwait(false);

        await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "service", problem is null ? "info" : "critical",
            Action: problem is null ? "started" : "firewall-failed",
            Reason: problem ?? string.Create(CultureInfo.InvariantCulture,
                $"{(settings.Enforce ? "filtering" : "counting only")} on {_iface}, {trusted.Count} trusted, logging {_firewall.Mode.ToString().ToLowerInvariant()}"),
            Actor: "bot"), ct).ConfigureAwait(false);
        if (problem is not null) await AlertAsync(Theme.Failure("Anti-DDoS firewall could not load", Clip(problem)), ct).ConfigureAwait(false);

        // The first reading only sets the baseline.
        var snapshot = await _firewall.SnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot is not null)
        {
            _lastAccounting = snapshot.Accounting;
            _lastCounters = snapshot.Counters;
            _lastBlocked = snapshot.Blocked.Select(b => b.Address).ToHashSet(StringComparer.Ordinal);
        }

        await RememberRecentBansAsync(ct).ConfigureAwait(false);
        _lastLink = _readCounters(_iface);
        _lastLinkHealth = _readHealth(_iface);
        _linkUp = _lastLinkHealth?.Up;
        _lastWindow = _time.GetUtcNow();

        await RecordGapAsync(snapshot, ct).ConfigureAwait(false);
        // At once: a crash before the first minute must not record the same gap twice.
        SaveReading();
    }

    /// <summary>The longest gap a saved reading is still trusted to cover.</summary>
    private static readonly TimeSpan LongestGap = TimeSpan.FromDays(7);

    /// <summary>
    /// If the bot was not recording - down, restarted, the server rebooted - write one traffic
    /// record and its source records covering the whole gap, from the saved reading to now.
    /// </summary>
    /// <remarks>
    /// The kernel counted throughout, so the totals are exact. A reboot zeroes every counter, and
    /// then the record says so: it covers only the time since boot.
    /// </remarks>
    private async Task RecordGapAsync(FirewallSnapshot? snapshot, CancellationToken ct)
    {
        if (_readings?.Load() is not { } saved || snapshot is null) return;
        var now = _time.GetUtcNow();
        var gap = now - saved.Time;
        if (saved.Interface != _iface || gap < TimeSpan.FromSeconds(90) || gap > LongestGap) return;

        var rebooted = _lastLink is { } link && saved.Link is { } before && (link.Packets < before.Packets || link.Bytes < before.Bytes);
        var (rxPackets, rxBytes) = _lastLink is { } l
            ? rebooted || saved.Link is not { } s0 ? l : (l.Packets - s0.Packets, l.Bytes - s0.Bytes)
            : (0L, 0L);
        var reason = rebooted
            ? $"the server rebooted while the bot was down; totals cover only the time since boot"
            : $"the bot was not running from {AntiDdosRecords.Stamp(saved.Time)}";

        var sources = TrafficAccounting.Between(saved.Accounting, snapshot.Accounting);
        var traffic = new TrafficWindow(now, gap, _iface, rxPackets, rxBytes, 0, new Dictionary<string, Counter>(), sources.Count,
            Math.Min(sources.Count, SourcesPerWindow), snapshot.AccountingSaturated, _firewall.Mode, _firewall.Engaged,
            (Storage?.Level ?? StorageLevel.Ok).ToString().ToLowerInvariant())
        {
            Loss = _lastLinkHealth?.Since(rebooted ? null : saved.Health),
            Gap = reason,
        };
        await _log.WriteAsync(RecordKind.Traffic, [AntiDdosRecords.Traffic(traffic)], ct).ConfigureAwait(false);
        await _log.WriteAsync(RecordKind.Source,
            [.. sources.Take(SourcesPerWindow).Select(s => AntiDdosRecords.Source(now, gap, s, gap: true))], ct).ConfigureAwait(false);
        await _log.EventAsync(new AntiDdosEvent(now, "gap", "warning", Action: "recorded",
            Reason: string.Create(CultureInfo.InvariantCulture,
                $"{reason}: {ReadingStore.Ago(gap)} not recorded minute by minute, kept as one record - {rxPackets:N0} packets in, {sources.Count} sources, {traffic.Loss?.Lost ?? 0:N0} lost before the firewall. The packet total is exact; per-source totals miss any source quiet for 3 min before the bot returned (the capture ring has those)"),
            Actor: "bot", Detail: new Dictionary<string, string> { ["from"] = AntiDdosRecords.Stamp(saved.Time) }), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Save the counters as of the last record written, all from that one moment, so the next gap
    /// starts exactly where the records stop.
    /// </summary>
    private void SaveReading()
    {
        if (_readings is null || _lastAccounting is null) return;
        _readings.Save(new Reading(_lastWindow, _iface, _lastLink, _lastLinkHealth, _lastAccounting));
    }

    /// <summary>
    /// Reload the last day's automatic bans from the event log, so a repeat offender's next ban
    /// still doubles after a restart.
    /// </summary>
    private async Task RememberRecentBansAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var remembered = 0;
        try
        {
            await foreach (var e in _log.ReadAsync([RecordKind.Event], now.AddDays(-1), now, ct).ConfigureAwait(false))
            {
                if (e is { Category: "ban", Action: "banned", Source: { } source } && e.Line.Contains("\"actor\":\"auto\"", StringComparison.Ordinal))
                {
                    _planner.RememberBan(source, e.Time);
                    remembered++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the last day's bans; repeat offenders start from their first ban again");
        }
        if (remembered > 0) _logger.LogInformation("Anti-DDoS remembers {Count} automatic ban(s) from the last 24h", remembered);
    }

    /// <summary>
    /// Make sure the log folder exists and nobody else can list or write it - traversable only,
    /// so ulogd (another user) can reach its own subfolder.
    /// </summary>
    /// <remarks>
    /// Tightened even when it already exists: the capture can create it first as a side effect of
    /// creating its own subfolder, with the default 0755. The install script's 0710 root:ulog is
    /// already tight and is left exactly as it is.
    /// </remarks>
    private void PrepareDirectory()
    {
        try
        {
            Directory.CreateDirectory(_log.Paths.Root);
            if (OperatingSystem.IsWindows()) return;
            const UnixFileMode open = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
            var mode = File.GetUnixFileMode(_log.Paths.Root);
            if ((mode & open) != 0) File.SetUnixFileMode(_log.Paths.Root, mode & ~open);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Cannot create the anti-DDoS log folder {Path}; nothing will be recorded", _log.Paths.Root);
        }
    }

    /// <summary>Done once start-up (firewall, trusted list, mode) has run, whether or not it worked.</summary>
    internal Task Started => _started.Task;

    /// <summary>Run one minute now, on the worker, and wait for it. For tests.</summary>
    internal Task TickNowAsync() => RunAsync(async ct =>
    {
        await TickAsync(ct).ConfigureAwait(false);
        return true;
    });

    /// <summary>Wait for everything queued so far. For tests.</summary>
    internal Task DrainAsync() => RunAsync(_ => Task.FromResult(true));

    /// <summary>One minute.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await Step("alerts", () => FlushOutboxAsync(now, ct)).ConfigureAwait(false);
        await Step("traffic", () => TrafficAsync(now, ct)).ConfigureAwait(false);
        await Step("crowdsec", () => CrowdSecAsync(now, ct)).ConfigureAwait(false);
        await Step("storage", () => StorageAsync(now, ct)).ConfigureAwait(false);
        await Step("mode", () => ApplyModeAsync(ct)).ConfigureAwait(false);
    }

    private async Task Step(string name, Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Anti-DDoS {Step} step failed; the others carry on and the firewall is untouched", name);
        }
    }

    // ---- traffic, detection, response ----

    private async Task TrafficAsync(DateTimeOffset now, CancellationToken ct)
    {
        var window = now - _lastWindow;
        if (window < TimeSpan.FromSeconds(10)) return;
        _lastWindow = now;

        var link = _readCounters(_iface);
        var (rxPackets, rxBytes) = link is { } l && _lastLink is { } p && l.Packets >= p.Packets && l.Bytes >= p.Bytes
            ? (l.Packets - p.Packets, l.Bytes - p.Bytes)
            : (0L, 0L);
        _lastLink = link;
        var peak = Interlocked.Exchange(ref _peakPps, 0);

        var snapshot = await _firewall.SnapshotAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SourceWindow> sources = [];
        IReadOnlyDictionary<string, Counter> counters = new Dictionary<string, Counter>();
        if (snapshot is not null)
        {
            var trusted = _state.Read().Trusted.Select(t => IpNetworks.Canonical(t.Network)).OfType<IpNetworks.Network>().ToList();
            var players = _players.OnlineAddresses().Select(CapturedPacket.Address).ToHashSet(StringComparer.Ordinal);
            sources = [.. TrafficAccounting.Between(_lastAccounting, snapshot.Accounting)
                .Select(s => s with { Player = players.Contains(s.Source), Trusted = trusted.Any(t => t.Contains(s.Source)) })];
            counters = TrafficAccounting.CounterDeltas(_lastCounters, snapshot.Counters);
            _lastAccounting = snapshot.Accounting;
            _lastCounters = snapshot.Counters;
        }

        var health = _readHealth(_iface);
        var loss = health?.Since(_lastLinkHealth);
        _lastLinkHealth = health ?? _lastLinkHealth;

        var traffic = new TrafficWindow(now, window, _iface, rxPackets, rxBytes, peak, counters, sources.Count,
            Math.Min(sources.Count, SourcesPerWindow), snapshot?.AccountingSaturated ?? false, _firewall.Mode, _firewall.Engaged,
            (Storage?.Level ?? StorageLevel.Ok).ToString().ToLowerInvariant())
        {
            Loss = loss,
        };
        LastWindow = traffic;
        if (snapshot is not null) SaveReading();
        if (loss is not null) await LinkEventsAsync(now, loss, ct).ConfigureAwait(false);

        await _log.WriteAsync(RecordKind.Traffic, [AntiDdosRecords.Traffic(traffic)], ct).ConfigureAwait(false);
        await _log.WriteAsync(RecordKind.Source,
            [.. sources.Take(SourcesPerWindow).Select(s => AntiDdosRecords.Source(now, window, s))], ct).ConfigureAwait(false);

        if (snapshot is null) return;
        var detections = TrafficDetector.Judge(sources, window, Options.Detection).ToList();
        detections.AddRange(await FirewallBlocksAsync(now, snapshot, ct).ConfigureAwait(false));
        if (detections.Count == 0) return;

        // Every known player, online or not: a flood spoofing an offline player's address must not get them banned.
        var exempt = sources.Where(s => s.Player || s.Trusted).Select(s => s.Source)
            .Concat(_players.KnownAddresses().Select(CapturedPacket.Address)).ToHashSet(StringComparer.Ordinal);
        var plans = _planner.Plan(now, detections, sources.Count, exempt.Contains, InAttack(now), BannedForGood(now));
        var alerts = new List<(PlannedResponse Plan, string? Problem)>();

        foreach (var plan in plans)
        {
            string? banProblem = null;
            if (plan.Ban is { } length)
            {
                banProblem = await AutoBanAsync(now, plan, length, ct).ConfigureAwait(false);
                alerts.Add((plan, banProblem));
            }
            else if (plan.Alert)
            {
                alerts.Add((plan, null));
            }

            if (plan.Alert || plan.Ban is not null)
            {
                await _log.EventAsync(new AntiDdosEvent(now, "detection", plan.Ban is null ? "info" : "warning", plan.Source,
                    plan.Ban is null ? "alerted" : "escalated", string.Join("; ", plan.Detections.Select(d => d.Reason)), Actor: "auto",
                    Incident: plan.Incident,
                    Detail: new Dictionary<string, string> { ["categories"] = string.Join(",", plan.Detections.Select(d => d.Category)) }), ct)
                    .ConfigureAwait(false);
            }
            if (plan.Held is { } held && _heldRecorded.Add(plan.Incident))
            {
                await _log.EventAsync(new AntiDdosEvent(now, "ban", "info", plan.Source, "held", held, Actor: "auto", Incident: plan.Incident), ct)
                    .ConfigureAwait(false);
            }

            lock (_detectionsGate)
            {
                _detections.AddFirst(new DetectionRecord(now, plan, banProblem));
                while (_detections.Count > DetectionsKept) _detections.RemoveLast();
            }
        }
        if (_heldRecorded.Count > 10_000) _heldRecorded.Clear();

        if (alerts.Count > 0) await AlertAsync(AntiDdosEmbeds.Detections(now, alerts, Options), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The addresses the firewall blocked on its own this minute - the per-address game-port limit
    /// or the SYN-flood limit - recorded as events and passed on as evidence, so one that keeps
    /// coming back is escalated to a CrowdSec ban instead of a fresh short block each time.
    /// </summary>
    private async Task<IReadOnlyList<Detection>> FirewallBlocksAsync(DateTimeOffset now, FirewallSnapshot snapshot, CancellationToken ct)
    {
        var blocked = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var b in snapshot.Blocked) blocked[b.Address] = b.ExpiresSeconds;
        var previous = _lastBlocked;
        _lastBlocked = [.. blocked.Keys];
        foreach (var gone in _selfBlocked.Where(b => b.Value <= now).Select(b => b.Key).ToList()) _selfBlocked.Remove(gone);
        if (previous is null) return [];

        var found = new List<Detection>();
        foreach (var (address, expires) in blocked)
        {
            if (previous.Contains(address) || _selfBlocked.ContainsKey(address)) continue;
            var until = expires is { } seconds ? now.AddSeconds(seconds) : (DateTimeOffset?)null;
            await _log.EventAsync(new AntiDdosEvent(now, "firewall-block", "warning", address, "blocked",
                string.Create(CultureInfo.InvariantCulture,
                    $"over a per-address limit ({Limits.PerSourcePps} pkt/s to the game ports, or {_firewall.Settings.SynBlockPps} connections/s)"),
                until, "kernel"), ct).ConfigureAwait(false);
            found.Add(new Detection("firewall-block", address, 1, 1,
                string.Create(CultureInfo.InvariantCulture, $"blocked by the firewall for {Limits.BlockMinutes} min (over a per-address limit)")));
        }
        return found;
    }

    /// <summary>
    /// Ban every source of a flood that just ended, permanently, in one go.
    /// </summary>
    /// <remarks>
    /// ALL OF THEM, BY THE OWNER'S CHOICE. Per-minute detection only sees sources over a rate; a
    /// botnet of thousands sends a few packets a second each and never trips it. These are every
    /// address that sent at least <see cref="AntiDdosOptions.FloodBanMinPackets"/> packets to the
    /// game's ports during the attack, from the capture, which sees packets before the firewall.
    ///
    /// NEVER BANNED, whatever they sent: a known player (every address confirmed in 30 days -
    /// checked now, at the end, so one who joined during the flood has had time to appear), a
    /// trusted address, and anything not on the public internet. No spoofing guard: a spoofed
    /// flood's addresses are banned too, which bans whoever really owns them.
    ///
    /// ONE IMPORT. Thousands of <c>cscli decisions add</c> would take minutes; one import takes
    /// about a second. Each decision then shows in the event log as a crowdsec-decision; one
    /// flood-ban event sums it up.
    /// </remarks>
    public Task<FloodBans?> BanFloodSourcesAsync(IReadOnlyList<string> sources, DateTimeOffset start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return RunAsync<FloodBans?>(async token =>
        {
            var now = _time.GetUtcNow();
            if (!Options.AutoBan || !Options.AttackPermaban)
                return new FloodBans(sources.Count, 0, 0, 0, "none", !Options.AutoBan ? "automatic bans are off" : "attack_permaban is off");

            var players = _players.KnownAddresses().Select(CapturedPacket.Address).ToHashSet(StringComparer.Ordinal);
            var trusted = _state.Read().Trusted.Select(t => IpNetworks.Canonical(t.Network)).OfType<IpNetworks.Network>().ToList();
            var bannedForGood = BannedForGood(now);

            var ban = new List<string>();
            int already = 0, spared = 0;
            foreach (var source in sources.Distinct(StringComparer.Ordinal))
            {
                if (IpNetworks.Canonical(source) is not { IsSingle: true } address || !IpNetworks.IsPublic(address)
                    || players.Contains(address.Address) || trusted.Any(t => t.Contains(address.Address)))
                {
                    spared++;
                }
                else if (bannedForGood(address.Address))
                {
                    already++;
                }
                else
                {
                    ban.Add(address.Address);
                }
            }

            var reason = $"flood source ({AntiDdosLog.FileStamp(start)})";
            string via;
            string? problem = null;
            string? note = null;
            if (ban.Count == 0)
            {
                via = _crowdsec.Installed ? "crowdsec" : "nftables";
            }
            else if (_crowdsec.Installed)
            {
                via = "crowdsec";
                problem = await _crowdsec.BanManyAsync(ban, ResponsePolicy.Permanent, reason, token).ConfigureAwait(false);
            }
            else
            {
                via = "nftables";
                var cap = Options.Response.Cap;
                problem = await _firewall.BlockManyAsync(ban, (int)Math.Ceiling(cap.TotalMinutes), token).ConfigureAwait(false);
                if (problem is null)
                {
                    foreach (var address in ban) _selfBlocked[address] = now + cap;
                    note = $"kept {ReadingStore.Ago(cap)}, not permanently: permanent bans need CrowdSec";
                }
            }

            var banned = problem is null ? ban.Count : 0;
            problem ??= note;
            if (banned > 0)
                foreach (var address in ban) _planner.MarkPermanent(address, now);

            await _log.EventAsync(new AntiDdosEvent(now, "flood-ban", banned > 0 || ban.Count == 0 ? "warning" : "critical",
                Action: banned > 0 || ban.Count == 0 ? "banned" : "ban-failed",
                Reason: string.Create(CultureInfo.InvariantCulture,
                    $"{banned:N0} of {sources.Count:N0} flood source(s) banned via {via}; {already:N0} already banned; {spared:N0} spared (players, trusted, private){(problem is null ? "" : " - " + problem)}"),
                Actor: "auto", Incident: "flood-" + AntiDdosLog.FileStamp(start),
                Detail: new Dictionary<string, string> { ["via"] = via, ["permanent"] = via == "crowdsec" ? "true" : "false" }), token).ConfigureAwait(false);
            // Without CrowdSec there are no decision events to say who: one event each, for /antiddos history.
            if (via == "nftables" && banned > 0)
            {
                foreach (var address in ban)
                    await _log.EventAsync(new AntiDdosEvent(now, "ban", "warning", address, "banned", reason, now + Options.Response.Cap, "auto",
                        "flood-" + AntiDdosLog.FileStamp(start), new Dictionary<string, string> { ["via"] = via, ["permanent"] = "false" }), token)
                        .ConfigureAwait(false);
            }

            return new FloodBans(sources.Count, banned, already, spared, via, problem);
        }).WaitAsync(ct);
    }

    /// <summary>The addresses CrowdSec already holds for a year or more, as of its last listing.</summary>
    private Func<string, bool> BannedForGood(DateTimeOffset now)
    {
        if (_lastDecisions is not { Count: > 0 } decisions) return _ => false;
        var horizon = now.AddDays(365);
        var banned = decisions.Values.Where(d => d.Type == "ban" && d.Until is { } until && until >= horizon)
            .Select(d => d.Value).ToHashSet(StringComparer.Ordinal);
        return banned.Contains;
    }

    /// <summary>Whether a flood is on, or ended within the correlation window: this minute's detections are part of it.</summary>
    private bool InAttack(DateTimeOffset now) =>
        Volatile.Read(ref _incident) is not null || (_floodEnded is { } ended && now - ended <= Options.Response.CorrelationWindow);

    /// <summary>
    /// Ban through CrowdSec when it is here; otherwise in the kernel, for the same time - except a
    /// permanent ban, which the kernel cannot keep (its block list is gone at a reboot), so it gets
    /// the longest temporary ban and says so.
    /// </summary>
    private async Task<string?> AutoBanAsync(DateTimeOffset now, PlannedResponse plan, TimeSpan length, CancellationToken ct)
    {
        if (IpNetworks.Canonical(plan.Source) is not { IsSingle: true } target || IpNetworks.IsLoopback(target)) return "not a single address";

        var categories = string.Join("+", plan.Detections.Select(d => d.Category).Distinct());
        var flood = Volatile.Read(ref _incident);
        var reason = plan.Permanent
            ? $"{categories} during a flood{(flood is null ? "" : $" ({flood})")} - permanent ({plan.Incident})"
            : $"{categories} repeated ({plan.Incident})";
        var permanent = plan.Permanent;
        string via;
        string? problem;
        if (_crowdsec.Installed)
        {
            via = "crowdsec";
            problem = await _crowdsec.BanAsync(target, length, reason, ct).ConfigureAwait(false);
        }
        else
        {
            via = "nftables";
            if (permanent)
            {
                length = Options.Response.Cap;
                permanent = false;
                reason += $" - kept {ReadingStore.Ago(length)} instead: permanent bans need CrowdSec";
            }
            problem = await _firewall.BlockAsync(target.Address, (int)Math.Ceiling(length.TotalMinutes), ct).ConfigureAwait(false);
            if (problem is null) _selfBlocked[target.Address] = now + length;
        }

        await _log.EventAsync(new AntiDdosEvent(now, "ban", problem is null ? "warning" : "critical", target.Text,
            problem is null ? "banned" : "ban-failed", problem ?? reason, problem is null ? now + length : null, "auto", plan.Incident,
            new Dictionary<string, string> { ["via"] = via, ["permanent"] = permanent ? "true" : "false" }), ct).ConfigureAwait(false);
        return problem;
    }

    // ---- CrowdSec ----

    private async Task CrowdSecAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!_crowdsec.Installed) return;

        var (decisions, problem) = await _crowdsec.DecisionsAsync(ct).ConfigureAwait(false);
        if (CrowdSecProblem != problem)
        {
            await _log.EventAsync(new AntiDdosEvent(now, "crowdsec-health", problem is null ? "info" : "critical",
                Action: problem is null ? "recovered" : "unreachable", Reason: problem, Actor: "bot"), ct).ConfigureAwait(false);
            await AlertAsync(problem is null
                ? Theme.Success("CrowdSec answering again")
                : Theme.Failure("CrowdSec is not answering", Clip(problem) + "\nIts bouncer keeps enforcing the bans it already has."), ct).ConfigureAwait(false);
            CrowdSecProblem = problem;
        }

        if (decisions is not null)
        {
            var current = decisions.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
            if (_lastDecisions is { } previous)
            {
                var added = current.Values.Where(d => !previous.ContainsKey(d.Id)).ToList();
                var gone = previous.Values.Where(d => !current.ContainsKey(d.Id)).ToList();
                var players = _players.KnownAddresses().Select(CapturedPacket.Address).ToHashSet(StringComparer.Ordinal);

                foreach (var d in added)
                {
                    var player = players.Contains(d.Value);
                    await _log.EventAsync(new AntiDdosEvent(now, "crowdsec-decision", player ? "warning" : "info", d.Value, d.Type,
                        d.Scenario, d.Until, d.FromBot ? "bot" : d.Origin,
                        Detail: new Dictionary<string, string>
                        {
                            ["id"] = d.Id.ToString(CultureInfo.InvariantCulture), ["origin"] = d.Origin, ["scope"] = d.Scope,
                            ["known_player"] = player ? "true" : "false",
                        }), ct).ConfigureAwait(false);
                }
                foreach (var d in gone)
                {
                    await _log.EventAsync(new AntiDdosEvent(now, "crowdsec-decision", "info", d.Value,
                        d.Until is { } until && until <= now.AddMinutes(1) ? "expired" : "removed", d.Scenario, Actor: d.Origin,
                        Detail: new Dictionary<string, string> { ["id"] = d.Id.ToString(CultureInfo.InvariantCulture) }), ct).ConfigureAwait(false);
                }

                var local = added.Where(d => !d.FromBot).ToList();
                if (local.Count > 0) await AlertAsync(AntiDdosEmbeds.CrowdSecDecisions(local, players), ct).ConfigureAwait(false);
            }
            _lastDecisions = current;
        }

        if (now - _lastHealth >= HealthEvery)
        {
            _lastHealth = now;
            await HealthAsync(now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Is the bouncer pulling decisions, and are the parsers parsing? Alerts only on a change.</summary>
    private async Task HealthAsync(DateTimeOffset now, CancellationToken ct)
    {
        var problems = new List<string>();
        var bouncers = await _crowdsec.BouncersAsync(ct).ConfigureAwait(false);
        if (bouncers is { Count: 0 }) problems.Add("no bouncer is registered, so CrowdSec decisions are not enforced");
        foreach (var b in bouncers ?? [])
        {
            if (b.Revoked) problems.Add($"bouncer {b.Name} is revoked");
            else if (b.LastPull is not { } pull || now - pull > TimeSpan.FromMinutes(5))
                problems.Add($"bouncer {b.Name} has not pulled decisions since {(b.LastPull is { } t ? AntiDdosRecords.Stamp(t) : "it was registered")}");
        }
        foreach (var source in await _crowdsec.AcquisitionAsync(ct).ConfigureAwait(false) ?? [])
        {
            if (await ParserProblemAsync(source, now, ct).ConfigureAwait(false) is { } fault) problems.Add($"{source.Name}: {fault}");
        }

        var problem = problems.Count == 0 ? null : string.Join("; ", problems);
        if (problem == _healthProblem) return;
        _healthProblem = problem;
        await _log.EventAsync(new AntiDdosEvent(now, "crowdsec-health", problem is null ? "info" : "warning",
            Action: problem is null ? "healthy" : "degraded", Reason: problem, Actor: "bot"), ct).ConfigureAwait(false);
        await AlertAsync(problem is null ? Theme.Success("CrowdSec healthy again") : Theme.Warning("CrowdSec needs attention", Clip(problem)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a source's parser is really broken. "None parsed" alone is not it - a quiet auth.log
    /// is all cron and sudo, which no parser takes - so a line that must parse is tried, in the
    /// file's own format.
    /// </summary>
    private async Task<string?> ParserProblemAsync(CrowdSecSource source, DateTimeOffset now, CancellationToken ct)
    {
        if (!source.NoneParsed)
        {
            _probes.Remove(source.Name);
            return null;
        }
        if (_probes.TryGetValue(source.Name, out var known) && now - known.At < ProbeEvery) return known.Problem;

        string? problem = null;
        var path = source.Name.StartsWith("file:", StringComparison.Ordinal) ? source.Name["file:".Length..] : null;
        if (CrowdSecProbe.For(source.Name, path is null ? null : _lastLine(path)) is { } probe)
        {
            if (probe.Problem is not null) problem = probe.Problem;
            else if (await _crowdsec.ParsesAsync(probe.Line!, ct).ConfigureAwait(false) is false)
                problem = $"a test line in this file's own format does not parse, so {probe.Meaning} - check `cscli parsers list` and `sudo cscli explain --file {path} --type syslog --failures`";
            // Null (cscli could not tell) is not reported as a fault: the next probe tries again.
        }
        _probes[source.Name] = (now, problem);
        _logger.LogDebug("CrowdSec parser probe for {Source}: {Verdict}", source.Name, problem ?? "parses, or nothing to test");
        return problem;
    }

    // ---- storage ----

    private async Task StorageAsync(DateTimeOffset now, CancellationToken ct)
    {
        var files = _log.Paths.AllLogFiles().ToList();
        var logBytes = files.Sum(f => f.Length);
        var disk = _disk(_log.Paths.Root) ?? (long.MaxValue / 4, long.MaxValue / 2);
        var verdict = StoragePolicy.Judge(disk.Free, disk.Total, logBytes, Options.Storage);

        if (verdict.Level >= StorageLevel.Pressure)
        {
            var live = new HashSet<string>(StringComparer.Ordinal)
                { _log.Paths.Packets, _log.Paths.Sources, _log.Paths.Traffic, _log.Paths.Events };
            var evict = StoragePolicy.Evict(
                files.Select(f => (f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), live.Contains(f.FullName),
                    f.Name.StartsWith("events.jsonl", StringComparison.Ordinal))),
                (long)(Options.Storage.MaxLogBytes * EvictTo));
            foreach (var path in evict)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete {Path} to free space", path);
                }
            }
            if (evict.Count > 0)
            {
                await _log.EventAsync(new AntiDdosEvent(now, "storage", "warning", Action: "evicted",
                    Reason: string.Create(CultureInfo.InvariantCulture, $"deleted the {evict.Count} oldest rotated log file(s)"), Actor: "bot",
                    Detail: new Dictionary<string, string> { ["files"] = string.Join(",", evict.Select(Path.GetFileName)) }), ct).ConfigureAwait(false);
            }
        }

        if (_capture is not null)
        {
            if (!verdict.CaptureAllowed && !_capture.Paused) _capture.Pause();
            else if (verdict.CaptureAllowed && _capture.Paused) _capture.Resume();
        }

        Storage = verdict;
        if (verdict.Level == _storageLevel) return;
        var previous = _storageLevel;
        _storageLevel = verdict.Level;
        await _log.EventAsync(new AntiDdosEvent(now, "storage", verdict.Level >= StorageLevel.Pressure ? "critical" : verdict.Level == StorageLevel.Warning ? "warning" : "info",
            Action: verdict.Level.ToString().ToLowerInvariant(), Reason: verdict.Reason, Actor: "bot",
            Detail: new Dictionary<string, string> { ["from"] = previous.ToString().ToLowerInvariant(), ["most_logging"] = verdict.MostLogging.ToString().ToLowerInvariant() }),
            ct).ConfigureAwait(false);
        await AlertAsync(AntiDdosEmbeds.Storage(verdict, previous), ct).ConfigureAwait(false);
    }

    // ---- modes ----

    /// <summary>Load <c>min(requested, what the disk allows)</c> when it is not already loaded; record a change.</summary>
    private async Task ApplyModeAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var state = _state.Read();
        if (state.Mode is not null && state.ModeUntil is { } until && until <= now)
        {
            _state.Update(s => { s.Mode = null; s.ModeUntil = null; s.ModeBy = null; });
            await _log.EventAsync(new AntiDdosEvent(now, "mode", "info", Action: "lapsed",
                Reason: $"{state.Mode.Value.ToString().ToLowerInvariant()} logging ended; back to {_configured.LogMode.ToString().ToLowerInvariant()}",
                Actor: "bot"), ct).ConfigureAwait(false);
        }

        var requested = Requested.Mode;
        var allowed = Storage?.MostLogging ?? LogMode.Incident;
        var effective = requested < allowed ? requested : allowed;
        if (effective == _firewall.Mode && _firewall.LastProblem is null) return;
        // A mode nft keeps refusing is retried every ten minutes, not recorded every minute.
        if (_modeFailure is { } failed && failed.Mode == effective && now - failed.At < HealthEvery) return;

        var previous = _firewall.Mode;
        var problem = await _firewall.SetModeAsync(effective, ct).ConfigureAwait(false);
        _modeFailure = problem is null ? null : (effective, now);
        await _log.EventAsync(new AntiDdosEvent(now, "mode", problem is null ? "info" : "critical",
            Action: problem is null ? "applied" : "failed",
            Reason: problem ?? (effective < requested
                ? $"{effective.ToString().ToLowerInvariant()} (asked {requested.ToString().ToLowerInvariant()}, capped by storage)"
                : effective.ToString().ToLowerInvariant()),
            Actor: "bot", Detail: new Dictionary<string, string> { ["from"] = previous.ToString().ToLowerInvariant() }), ct).ConfigureAwait(false);
    }

    private async Task<string?> RequestModeAsync(LogMode mode, DateTimeOffset? until, string by, string? incident, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!_state.Update(s => { s.Mode = until is null ? null : mode; s.ModeUntil = until; s.ModeBy = until is null ? null : by; }))
            return "the state file could not be saved";
        await _log.EventAsync(new AntiDdosEvent(now, "mode", "info", Action: "requested",
            Reason: until is null ? $"back to {_configured.LogMode.ToString().ToLowerInvariant()}" : $"{mode.ToString().ToLowerInvariant()} until {AntiDdosRecords.Stamp(until.Value)}",
            Expires: until, Actor: by, Incident: incident), ct).ConfigureAwait(false);
        await ApplyModeAsync(ct).ConfigureAwait(false);
        return _firewall.LastProblem;
    }

    // ---- what the Discord command can change ----

    /// <summary>Raise logging for a while, or (with null) go back to the configured mode now.</summary>
    public Task<string?> SetModeAsync(LogMode? mode, TimeSpan duration, string actor) => RunAsync(ct =>
        mode is { } m && m is LogMode.Elevated or LogMode.Incident or LogMode.Normal
            ? RequestModeAsync(m, _time.GetUtcNow() + duration, actor, null, ct)
            : RequestModeAsync(_configured.LogMode, null, actor, null, ct));

    public Task<string?> SetThresholdAsync(Threshold threshold, int value, string actor) => RunAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(threshold);
        if (value < threshold.Min || value > threshold.Max)
            return string.Create(CultureInfo.InvariantCulture, $"{threshold.Name} must be {threshold.Min}-{threshold.Max}");

        var before = _state.Read().Overrides;
        var options = Thresholds.Apply(_configured, _configuredLimits, new Dictionary<string, int>(before) { [threshold.Name] = value }, out var limits);
        var previousSettings = _firewall.Settings;
        var problem = await _firewall.ConfigureAsync(previousSettings with
        {
            Limits = limits, SshNewPerMinute = options.SshNewPerMinute, IcmpEchoPps = options.IcmpEchoPps,
            SuspectUdpPps = options.SuspectUdpPps, SuspectSynPerSecond = options.SuspectSynPerSecond, SynBlockPps = options.SynBlockPps,
        }, ct).ConfigureAwait(false);
        if (problem is not null) return problem;

        if (!_state.Update(s => s.Overrides[threshold.Name] = value))
        {
            // Saved nowhere: put the firewall back so a restart and now agree.
            await _firewall.ConfigureAsync(previousSettings, ct).ConfigureAwait(false);
            return "the state file could not be saved; nothing was changed";
        }
        Options = options;
        Limits = limits;
        _planner.Policy = options.Response;

        await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "threshold", "info", Action: "changed",
            Reason: string.Create(CultureInfo.InvariantCulture, $"{threshold.Name} = {value}"), Actor: actor), ct).ConfigureAwait(false);
        return null;
    });

    public Task<string?> TrustAsync(IpNetworks.Network network, string comment, string actor) => RunAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(network);
        if (_state.Read().Trusted.Any(t => t.Network == network.Text)) return $"{network.Text} is already trusted";

        var networks = _state.Read().Trusted.Select(t => t.Network).Append(network.Text).ToList();
        if (await _firewall.SetTrustedAsync(networks, ct).ConfigureAwait(false) is { } problem) return problem;
        if (!_state.Update(s => s.Trusted.Add(new TrustedEntry(network.Text, CrowdSecClient.Clean(comment), actor, _time.GetUtcNow()))))
        {
            await _firewall.SetTrustedAsync(networks[..^1], ct).ConfigureAwait(false);
            return "the state file could not be saved; nothing was changed";
        }

        var crowdsec = _crowdsec.Installed ? await _crowdsec.AllowAsync(network, $"{actor}: {comment}", ct).ConfigureAwait(false) : null;
        await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "allowlist", "warning", network.Text, "added", comment, Actor: actor,
            Detail: crowdsec is null ? null : new Dictionary<string, string> { ["crowdsec"] = crowdsec }), ct).ConfigureAwait(false);
        return crowdsec is null ? null : $"trusted by the bot's firewall, but CrowdSec said: {crowdsec}";
    });

    public Task<string?> UntrustAsync(IpNetworks.Network network, string actor) => RunAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(network);
        if (!_state.Read().Trusted.Any(t => t.Network == network.Text)) return $"{network.Text} is not on the trusted list";

        var networks = _state.Read().Trusted.Where(t => t.Network != network.Text).Select(t => t.Network).ToList();
        if (await _firewall.SetTrustedAsync(networks, ct).ConfigureAwait(false) is { } problem) return problem;
        _state.Update(s => s.Trusted.RemoveAll(t => t.Network == network.Text));

        var crowdsec = _crowdsec.Installed ? await _crowdsec.DisallowAsync(network, ct).ConfigureAwait(false) : null;
        await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "allowlist", "warning", network.Text, "removed", Actor: actor,
            Detail: crowdsec is null ? null : new Dictionary<string, string> { ["crowdsec"] = crowdsec }), ct).ConfigureAwait(false);
        return null;
    });

    /// <summary>Ban by hand. Null duration is permanent (ten years: CrowdSec has no "forever").</summary>
    public Task<string?> BanAsync(IpNetworks.Network network, TimeSpan? duration, string reason, string actor) => RunAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(network);
        if (IpNetworks.IsLoopback(network)) return "the loopback range is never blocked";
        if (_state.Read().Trusted.Select(t => IpNetworks.Canonical(t.Network)).OfType<IpNetworks.Network>()
                .Any(t => t.Contains(network.Address) || network.Contains(t.Address)))
            return $"{network.Text} overlaps the trusted list - remove it from there first";

        var length = duration ?? TimeSpan.FromDays(3650);
        string via;
        string? problem;
        if (_crowdsec.Installed)
        {
            // One ban per address: a second identical decision would only be noise to expire later.
            var (existing, _) = await _crowdsec.DecisionsAsync(ct).ConfigureAwait(false);
            if (existing?.FirstOrDefault(d => d.Value == network.Text && d.Type == "ban" && d.Until >= _time.GetUtcNow() + length) is { } same)
                return string.Create(CultureInfo.InvariantCulture, $"{network.Text} is already banned until {AntiDdosRecords.Stamp(same.Until!.Value)} (decision {same.Id})");
            via = "crowdsec";
            problem = await _crowdsec.BanAsync(network, length, $"{actor}: {reason}", ct).ConfigureAwait(false);
        }
        else if (network.IsSingle && duration is { } temporary)
        {
            via = "nftables";
            problem = await _firewall.BlockAsync(network.Address, (int)Math.Ceiling(temporary.TotalMinutes), ct).ConfigureAwait(false);
            if (problem is null) _selfBlocked[network.Address] = _time.GetUtcNow() + temporary;
        }
        else
        {
            return "permanent bans and ranges need CrowdSec, which is not installed - see ANTIDDOS.md";
        }

        await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "ban", problem is null ? "warning" : "critical", network.Text,
            problem is null ? "banned" : "ban-failed", problem ?? reason, problem is null ? _time.GetUtcNow() + length : null, actor,
            Detail: new Dictionary<string, string> { ["via"] = via, ["permanent"] = duration is null ? "true" : "false" }), ct).ConfigureAwait(false);
        return problem;
    });

    /// <summary>Lift every ban on an address or range: CrowdSec's and the kernel's.</summary>
    public Task<string?> UnbanAsync(IpNetworks.Network network, string actor) => RunAsync(async ct =>
    {
        ArgumentNullException.ThrowIfNull(network);
        var problems = new List<string>();
        if (_crowdsec.Installed && await _crowdsec.UnbanAsync(network, ct).ConfigureAwait(false) is { } c) problems.Add("CrowdSec: " + c);
        if (network.IsSingle && _firewall.Managed && await _firewall.UnblockAsync(network.Address, ct).ConfigureAwait(false) is { } f)
            problems.Add("nftables: " + f);

        var problem = problems.Count == 0 ? null : string.Join("; ", problems);
        if (network.IsSingle)
        {
            // Lifted by hand: a later flood may ban it again, and the last listing no longer counts it.
            _planner.Forget(network.Address);
            _selfBlocked.Remove(network.Address);
            if (_lastDecisions is { } listed)
                foreach (var id in listed.Where(d => d.Value.Value == network.Address).Select(d => d.Key).ToList()) listed.Remove(id);
        }
        await _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), "ban", "warning", network.Text, problem is null ? "unbanned" : "unban-partial",
            problem, Actor: actor), ct).ConfigureAwait(false);
        return problem;
    });

    /// <summary>Record an administrative action that changed nothing here (an export, a search).</summary>
    public Task RecordAsync(string category, string action, string? source, string? reason, string actor, CancellationToken ct) =>
        _log.EventAsync(new AntiDdosEvent(_time.GetUtcNow(), category, "info", source, action, reason, Actor: actor), ct);

    /// <summary>The link going down or up, the kernel losing packets, the connection table filling: each recorded once per change.</summary>
    private async Task LinkEventsAsync(DateTimeOffset now, LinkHealth loss, CancellationToken ct)
    {
        if (_linkUp != loss.Up)
        {
            var was = _linkUp;
            _linkUp = loss.Up;
            if (was is not null)
            {
                await _log.EventAsync(new AntiDdosEvent(now, "link", loss.Up ? "warning" : "critical", Action: loss.Up ? "up" : "down",
                    Reason: $"{_iface} is {loss.OperState}", Actor: "kernel"), ct).ConfigureAwait(false);
                await AlertAsync(loss.Up
                    ? Theme.Success($"{_iface} is back up")
                    : Theme.Failure($"{_iface} went down", "The server lost its network link. Everything that still arrives is counted and logged on disk."), ct)
                    .ConfigureAwait(false);
            }
        }

        if (loss.Lost > 0)
        {
            await _log.EventAsync(new AntiDdosEvent(now, "kernel-loss", loss.Lost >= 1000 ? "warning" : "info", Action: "lost",
                Reason: string.Create(CultureInfo.InvariantCulture,
                    $"{loss.Lost:N0} packets lost before the firewall in the last minute - NIC {loss.NicDropped + loss.NicMissed + loss.NicFifo + loss.NicOver:N0}, input queue {loss.SoftnetDropped:N0}, connection table {loss.ConntrackDrops:N0}"),
                Actor: "kernel"), ct).ConfigureAwait(false);
        }

        var full = loss.ConntrackFill >= 0.9;
        if (full != _conntrackFull && (full || loss.ConntrackFill < 0.8))
        {
            _conntrackFull = full;
            var text = string.Create(CultureInfo.InvariantCulture, $"{loss.ConntrackCount:N0} of {loss.ConntrackMax:N0} tracked connections ({loss.ConntrackFill:P0})");
            await _log.EventAsync(new AntiDdosEvent(now, "conntrack", full ? "critical" : "info", Action: full ? "full" : "recovered", Reason: text, Actor: "kernel"), ct)
                .ConfigureAwait(false);
            if (full)
                await AlertAsync(Theme.Failure("Connection table nearly full", $"{text}. New connections are being dropped - a flood is getting past the firewall's limits."), ct)
                    .ConfigureAwait(false);
        }
    }

    // ---- alerts ----

    /// <summary>
    /// Post an alert, or hold it until Discord answers again.
    /// </summary>
    /// <remarks>
    /// HELD, NOT LOST. Discord being unreachable is often the attack itself, and the alerts raised
    /// then are the ones that matter most. They wait in order, at most <see cref="OutboxSize"/>, and
    /// are posted marked late with the time they were raised. Every one is in the event log anyway.
    /// </remarks>
    private async Task AlertAsync(EmbedBuilder embed, CancellationToken ct)
    {
        if (_configured.AlertChannel is null) return;
        var now = _time.GetUtcNow();
        if (_outbox.Count == 0 && await TrySendAsync(embed, ct).ConfigureAwait(false)) return;
        Hold(embed, now);
    }

    private void Hold(EmbedBuilder embed, DateTimeOffset at)
    {
        if (_outbox.Count == 0)
            _logger.LogWarning("Discord did not take an anti-DDoS alert; holding alerts until it answers (they are in the event log)");
        if (_outbox.Count >= OutboxSize)
        {
            _outbox.Dequeue();
            _outboxDropped++;
        }
        _outbox.Enqueue((embed, at, embed.Footer?.Text));
    }

    /// <summary>Post what was held, oldest first, stopping at the first that still fails.</summary>
    private async Task FlushOutboxAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_outbox.Count == 0) return;
        var held = _outbox.Count;
        var since = _outbox.Peek().At;
        while (_outbox.Count > 0)
        {
            // From the footer it had when held, so a retry does not stack the note twice.
            var (embed, at, footer) = _outbox.Peek();
            var before = footer is { Length: > 0 } ? footer + " · " : "";
            embed.WithFooter($"{before}Posted late: raised {AntiDdosRecords.Stamp(at)}, held while Discord was unreachable");
            if (!await TrySendAsync(embed, ct).ConfigureAwait(false)) return;
            _outbox.Dequeue();
        }

        var dropped = _outboxDropped;
        _outboxDropped = 0;
        await _log.EventAsync(new AntiDdosEvent(now, "alerts", "warning", Action: "delivered-late",
            Reason: string.Create(CultureInfo.InvariantCulture,
                $"{held} alert(s) held since {AntiDdosRecords.Stamp(since)} while Discord was unreachable were posted{(dropped > 0 ? $"; {dropped} older one(s) did not fit and are only in the event log" : "")}"),
            Actor: "bot"), ct).ConfigureAwait(false);
    }

    private async Task<bool> TrySendAsync(EmbedBuilder embed, CancellationToken ct)
    {
        if (_configured.AlertChannel is not { } channel) return true;
        try
        {
            return await _post.SendAsync(channel, embed.Build(), null, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not post an anti-DDoS alert");
            return false;
        }
    }

    private static string Clip(string text) => text.Length > 900 ? text[..900] + "…" : text;
}
