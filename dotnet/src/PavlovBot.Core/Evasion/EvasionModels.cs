namespace PavlovBot.Core.Evasion;

/// <summary>
/// How strongly one signal implicates a returning banned player. Additive, so no single
/// weak signal can reach <see cref="EvasionScorer.MatchThreshold"/> on its own.
/// </summary>
/// <remarks>
/// These are the values the Node bot used, unchanged. They are an enum rather than loose
/// ints so a caller cannot invent a weight, and so the reason kind and its weight can
/// never drift apart - in JS those were two independent fields that had to agree by
/// convention.
/// </remarks>
public enum SignalKind
{
    /// <summary>The joining EOS account is itself banned. Definitive - the same account back under any name.</summary>
    Eos = 100,

    /// <summary>The join IP appears in a ban's recorded addresses. Very strong.</summary>
    Ip = 70,

    /// <summary>The joining name matches a banned name, or a known shared-IP alias.</summary>
    Username = 50,

    /// <summary>Same consumer VPN brand as a banned account.</summary>
    Provider = 25,

    /// <summary>Same ASN AND both sides on a VPN/hosting network. Weak alone, meaningful together.</summary>
    AsnVpn = 20,

    /// <summary>
    /// Same autonomous system. The weakest signal by far - a single ASN can be a whole
    /// national ISP - so it never stands alone.
    /// </summary>
    Asn = 10,
}

/// <summary>One reason a join was correlated to a ban, carrying its own weight.</summary>
public sealed record EvasionReason(SignalKind Kind, string Detail)
{
    public int Weight => (int)Kind;
}

/// <summary>
/// Network fingerprint captured at ban time and stored alongside the ban, so a future
/// join can still be correlated after the VPN cache entry for that IP has expired.
/// </summary>
/// <remarks>
/// Every anonymising flag is <c>bool?</c> on purpose. <c>null</c> means "no detector could
/// answer", which is a different fact from <c>false</c>. Conflating the two is what made
/// the Node bot ban players on a single unconfirmed hit.
/// </remarks>
public sealed record BanNetwork
{
    public IReadOnlyList<string> EosIds { get; init; } = [];
    public IReadOnlyList<string> Ips { get; init; } = [];
    public IReadOnlyList<string> Names { get; init; } = [];

    public string? Asn { get; init; }
    public string? Organization { get; init; }
    public string? Provider { get; init; }

    public bool? Vpn { get; init; }
    public bool? Proxy { get; init; }
    public bool? Tor { get; init; }
    public bool? Hosting { get; init; }

    public int? ThreatScore { get; init; }
    public string? Country { get; init; }
    public DateTimeOffset? DetectedAt { get; init; }

    /// <summary>True when this fingerprint is on an anonymising network we could confirm.</summary>
    public bool IsAnonymising => Vpn == true || Hosting == true;
}

/// <summary>A ban record, as far as evasion scoring is concerned.</summary>
public sealed record BanRecord
{
    public required string PlayerId { get; init; }
    public string Reason { get; init; } = "unknown";
    public string Moderator { get; init; } = "unknown";
    public bool Permanent { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public BanNetwork? Network { get; init; }
}

/// <summary>Everything known about the player who just connected.</summary>
public sealed record JoinContext
{
    public string? Name { get; init; }
    public string? EosId { get; init; }
    public string? Ip { get; init; }
    public string? Asn { get; init; }
    public string? Provider { get; init; }

    public bool? Vpn { get; init; }
    public bool? Hosting { get; init; }

    /// <summary>Names this account is known to share a confirmed IP with.</summary>
    public IReadOnlyList<string> AltNames { get; init; } = [];

    public bool IsAnonymising => Vpn == true || Hosting == true;
}

/// <summary>Standing flags in the ban registry, independent of any per-player ban row.</summary>
public sealed record RegistryFlags
{
    public IReadOnlySet<string> EosIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Ips { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Names { get; init; } = new HashSet<string>();

    public static RegistryFlags Empty { get; } = new();
}

/// <summary>One ban this join was correlated to, and why.</summary>
public sealed record EvasionMatch
{
    public required string PlayerId { get; init; }
    public required string Reason { get; init; }
    public required string Moderator { get; init; }
    public bool Permanent { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public int Score { get; init; }

    /// <summary>Set when a definitive signal fired - currently only an EOS account match.</summary>
    public bool Definitive { get; init; }

    /// <summary>Strongest reason first, so a moderator reads the decisive one immediately.</summary>
    public IReadOnlyList<EvasionReason> Reasons { get; init; } = [];
}

/// <summary>A standing registry flag that fired for this join.</summary>
public sealed record FlagHit(SignalKind Kind, string Detail);

/// <summary>The verdict. Reports what matched and why; it never bans anyone.</summary>
public sealed record EvasionResult
{
    public IReadOnlyList<EvasionMatch> Matches { get; init; } = [];
    public IReadOnlyList<FlagHit> Flags { get; init; } = [];

    /// <summary>Anything at all correlated - worth a moderator's attention.</summary>
    public bool IsEvasion => Matches.Count > 0 || Flags.Count > 0;

    /// <summary>A definitive signal fired. Safe to act on without further confirmation.</summary>
    public bool IsCertain =>
        Matches.Any(m => m.Definitive) || Flags.Any(f => f.Kind == SignalKind.Eos);

    /// <summary>Score of the strongest match, or the EOS weight when only a registry flag fired.</summary>
    public int Score => Matches.Count > 0
        ? Matches[0].Score
        : Flags.Count > 0 ? (int)SignalKind.Eos : 0;

    public static EvasionResult None { get; } = new();
}
