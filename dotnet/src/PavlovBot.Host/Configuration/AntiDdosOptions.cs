using PavlovBot.Core.Network;

namespace PavlovBot.Host.Configuration;

/// <summary>
/// The anti-DDoS settings: firewall thresholds, detection, automatic response, logging and storage.
/// </summary>
/// <remarks>
/// DEFAULTS ARE THE SAFE ONES. Every threshold is far above what a Pavlov client or a person at an
/// SSH prompt produces, logging starts at <see cref="Core.Network.LogMode.Normal"/>, and nothing
/// here drops a packet unless <c>ATTACK_MITIGATION=on</c>. The thresholds an owner may change from
/// Discord are layered over these at runtime (see <c>AntiDdosState</c>); these stay the floor a
/// restart without that file comes back to.
/// </remarks>
public sealed record AntiDdosOptions
{
    public const string DefaultLogDirectory = "/var/log/anti-ddos";

    /// <summary>Where every anti-DDoS log lives (<c>ANTIDDOS_LOG_DIR</c>). Persistent storage, never /tmp.</summary>
    public string LogDirectory { get; init; } = DefaultLogDirectory;

    public string SshPorts { get; init; } = "22";
    public int SshNewPerMinute { get; init; } = 30;
    public int IcmpEchoPps { get; init; } = 50;
    public int SuspectUdpPps { get; init; } = 300;
    public int SuspectSynPerSecond { get; init; } = 20;
    public int NflogGroup { get; init; } = 7;

    /// <summary>The per-address game-port limit all the time, not only in a flood (<c>ANTIDDOS_ALWAYS_BLOCK</c>, on unless off).</summary>
    public bool AlwaysBlock { get; init; } = true;

    /// <summary>New TCP connections a second from one address, not to SSH, before it is blocked (<c>ANTIDDOS_SYN_BLOCK_PPS</c>).</summary>
    public int SynBlockPps { get; init; } = 50;

    public int SynPerMinute { get; init; } = 120;
    public int PortSpread { get; init; } = 50;

    public bool AutoBan { get; init; } = true;
    public int EscalateAfter { get; init; } = 3;
    public int CorrelationMinutes { get; init; } = 10;
    public int BanMinutes { get; init; } = 60;
    public int MaxBanHours { get; init; } = 168;
    public int MaxBansPerHour { get; init; } = 30;
    public int SpoofSources { get; init; } = 5000;

    /// <summary>The logging mode the bot returns to (<c>ANTIDDOS_LOG_MODE</c>): normal or elevated.</summary>
    public LogMode LogMode { get; init; } = LogMode.Normal;

    /// <summary>How long incident logging lasts before it returns to <see cref="LogMode"/> on its own.</summary>
    public int IncidentMinutes { get; init; } = 30;

    /// <summary>Whether a detected flood switches incident logging on by itself (<c>ANTIDDOS_AUTO_INCIDENT</c>).</summary>
    public bool AutoIncident { get; init; } = true;

    public int MaxLogMegabytes { get; init; } = 2048;
    public int WarnFreePercent { get; init; } = 20;
    public int PressureFreePercent { get; init; } = 10;
    public int ReserveMegabytes { get; init; } = 2048;

    /// <summary>Whether CrowdSec is used when installed (<c>ANTIDDOS_CROWDSEC</c>, on unless set off).</summary>
    public bool CrowdSec { get; init; } = true;

    /// <summary>Where detections, bans and health alerts go: <c>ANTIDDOS_ALERT_CHANNEL</c>, else <c>ATTACK_CHANNEL</c>.</summary>
    public ulong? AlertChannel { get; init; }

    /// <summary>The configured mode. Only normal and elevated may be the resting mode; incident is always time-boxed.</summary>
    public static LogMode ModeFrom(string? setting) =>
        setting?.Trim().ToLowerInvariant() == "elevated" ? Core.Network.LogMode.Elevated : Core.Network.LogMode.Normal;

    public StorageLimits Storage => new(
        (long)MaxLogMegabytes * 1024 * 1024, WarnFreePercent, PressureFreePercent, (long)ReserveMegabytes * 1024 * 1024);

    public DetectionThresholds Detection => new(SuspectUdpPps, SynPerMinute, PortSpread);

    public ResponsePolicy Response => new(
        AutoBan, EscalateAfter, TimeSpan.FromMinutes(CorrelationMinutes), TimeSpan.FromMinutes(BanMinutes),
        TimeSpan.FromHours(MaxBanHours), MaxBansPerHour, SpoofSources);
}
