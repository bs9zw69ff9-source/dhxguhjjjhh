namespace PavlovBot.Core.Vpn;

/// <summary>
/// What one detector said about one IP.
/// </summary>
/// <remarks>
/// EVERY BOOLEAN HERE IS NULLABLE, AND THAT IS THE WHOLE POINT. <c>null</c> means the
/// provider did not answer that question - not that the answer is no. Conflating the two
/// is the single most productive source of bugs this subsystem has had: a detector that
/// was down read as "clean", an exhausted quota read as innocence, and a body encoding
/// booleans as 0/1 read as having no verdict at all.
/// </remarks>
public sealed record DetectorReading
{
    public required string Name { get; init; }
    public required int Tier { get; init; }

    /// <summary>
    /// Whether this detector considers the address anonymising. Not nullable: a reading
    /// only exists when the detector answered, and "answered but has no opinion" is the
    /// same as "not flagged" for consensus purposes.
    /// </summary>
    public required bool Flagged { get; init; }

    /// <summary>Short human summary, e.g. "vpn+tor risk:82". Shown in the audit trail.</summary>
    public string? Detail { get; init; }

    public bool? Vpn { get; init; }
    public bool? Proxy { get; init; }
    public bool? Tor { get; init; }
    public bool? Relay { get; init; }
    public bool? Abuser { get; init; }

    /// <summary>Datacenter / non-residential. NOT a hit on its own - honest players sit
    /// behind carrier and cloud ranges.</summary>
    public bool? Hosting { get; init; }

    public bool? Residential { get; init; }
    public bool? Mobile { get; init; }

    /// <summary>0-100 risk, where the provider publishes one.</summary>
    public double? ThreatScore { get; init; }

    /// <summary>The CONSUMER BRAND ("NordVPN"), not the underlying network.</summary>
    public string? Provider { get; init; }

    public string? Asn { get; init; }
    public string? Organization { get; init; }
    public string? Isp { get; init; }
    public string? Country { get; init; }
    public string? CountryCode { get; init; }
    public string? Region { get; init; }
    public string? City { get; init; }

    /// <summary>IPHub's raw block value. Kept for backwards compatibility with stored records.</summary>
    public int? IpHubBlock { get; init; }
}

/// <param name="Hits">Detectors that answered AND flagged.</param>
/// <param name="Answered">
/// Detectors that returned a verdict. Tracked separately from the count run, so a total
/// outage is distinguishable from a unanimous all-clear - which is the difference between
/// "nobody could check" and "everybody says it is fine".
/// </param>
/// <param name="Failed">Detectors that errored, timed out, or were rate limited.</param>
public sealed record TierOutcome(
    int Hits,
    int Answered,
    int Failed,
    IReadOnlyList<DetectorReading> Readings)
{
    public static TierOutcome From(IEnumerable<DetectorReading?> results)
    {
        var readings = results.Where(r => r is not null).Select(r => r!).ToList();
        var attempted = results.Count();
        return new TierOutcome(readings.Count(r => r.Flagged), readings.Count, attempted - readings.Count, readings);
    }
}

/// <param name="ScreenMin">Tier-1 hits needed to escalate to tier 2.</param>
/// <param name="ConfirmMin">Tier-2 hits needed to ban.</param>
/// <param name="ScreenBanMin">
/// Tier-1 hits needed to ban when NO tier-2 detector could answer. Deliberately higher
/// than <see cref="ScreenMin"/>: individual providers do produce false positives on
/// legitimate infrastructure - ipapi.is reports Cloudflare's 1.1.1.1 as "vpn+abuser" - so
/// a single unconfirmed source must never be enough to ban somebody.
/// </param>
public sealed record VpnThresholds(int ScreenMin = 1, int ConfirmMin = 1, int ScreenBanMin = 2)
{
    public static VpnThresholds Default { get; } = new();

    /// <summary>Clamp to at least 1. A threshold of zero would flag every address.</summary>
    public VpnThresholds Sanitised() => new(Math.Max(1, ScreenMin), Math.Max(1, ConfirmMin), Math.Max(1, ScreenBanMin));
}

/// <param name="Flagged">The regular checks reached <see cref="VpnThresholds.ScreenMin"/>.</param>
/// <param name="Confirmed">
/// The CONFIRMATION verdict, for display: true agreed, false disputed, null inconclusive.
/// </param>
/// <param name="Actionable">
/// The separate, explicit "may we auto-ban" decision.
/// </param>
/// <param name="Reason">Why, in words that go straight into the log.</param>
public sealed record VpnDecision(bool Flagged, bool? Confirmed, bool Actionable, string Reason)
{
    /// <summary>The verdict word shown to staff.</summary>
    public string Label => !Flagged ? "CLEAN"
        : Confirmed == true ? "CONFIRMED"
        : Confirmed == false ? "DISPUTED"
        : "FLAGGED (inconclusive)";
}
