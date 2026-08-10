namespace PavlovBot.Core.Intelligence;

/// <summary>
/// What a risk signal is evidence OF, and how much it is worth.
/// </summary>
/// <remarks>
/// THE WEIGHT IS THE ENUM VALUE, the same trick <see cref="Evasion.SignalKind"/> uses, so a
/// signal and its weight cannot drift apart into two fields that have to agree by convention.
///
/// The weights are deliberately NOT tuned to make anything reach 100. A player who is the
/// same banned EOS account back under a new name should score decisively on that one fact;
/// somebody who merely shares an ASN with a banned player should score almost nothing however
/// many other weak signals pile up. See <see cref="RiskScorer"/> for how they combine, which
/// is the part that stops weak signals summing their way to a ban.
/// </remarks>
public enum RiskSignalKind
{
    /// <summary>This exact account is banned. Not a guess.</summary>
    BannedAccount = 100,

    /// <summary>An address they have used is one a banned player used. Very strong.</summary>
    SharedAddressWithBanned = 60,

    /// <summary>The existing evasion scorer reported a match at or above its threshold.</summary>
    EvasionMatch = 55,

    /// <summary>A name they have used matches a banned player's name.</summary>
    NameMatchesBanned = 40,

    /// <summary>They are on a standing ban-evasion flag.</summary>
    Flagged = 35,

    /// <summary>Prior moderation on this account: a served ban, or one that was lifted.</summary>
    PriorBan = 25,

    /// <summary>Connecting through a VPN, proxy, Tor or a hosting network.</summary>
    Anonymising = 15,

    /// <summary>Active warnings inside the decay window.</summary>
    ActiveWarnings = 10,

    /// <summary>An account seen for the first time very recently. Context, not evidence.</summary>
    BrandNewAccount = 5,
}

/// <summary>
/// One reason a player scored, with the evidence behind it.
/// </summary>
/// <param name="Kind">What this is evidence of. Carries the weight.</param>
/// <param name="Summary">One sentence a moderator can read and act on.</param>
/// <param name="Evidence">
/// Where the claim came from, so it can be checked. A record id, a name, an account id.
/// </param>
/// <param name="Sensitive">
/// True when <paramref name="Evidence"/> contains network information. Redacted for anybody
/// without network access, which is why it is a flag on the signal rather than a judgement
/// made at the point of display.
/// </param>
/// <remarks>
/// EVERY SIGNAL CARRIES ITS OWN EXPLANATION. A score with no reasons is a number a moderator
/// has to either trust blindly or ignore, and both are worse than no score. The brief for
/// this system says so explicitly: never a black box.
/// </remarks>
public sealed record RiskSignal(
    RiskSignalKind Kind,
    string Summary,
    string? Evidence = null,
    bool Sensitive = false)
{
    public int Weight => (int)Kind;
}

/// <summary>How much the evidence behind a score can be relied on.</summary>
/// <remarks>
/// SEPARATE FROM THE SCORE, and the separation is the point. "80 out of 100 on one weak
/// signal" and "80 out of 100 on three independent strong ones" are different situations and
/// a single number cannot tell them apart - so a moderator reading only the score would treat
/// them the same.
/// </remarks>
public enum RiskConfidence
{
    /// <summary>Nothing corroborates anything. Treat as a prompt to look, not a finding.</summary>
    Low,

    /// <summary>One strong signal, or several weak ones agreeing.</summary>
    Medium,

    /// <summary>Independent strong signals, or a definitive one.</summary>
    High,
}

/// <summary>What the bot suggests a human does next. It never does any of it by itself.</summary>
public enum RiskRecommendation
{
    /// <summary>Nothing here. No action.</summary>
    None,

    /// <summary>Worth a look when convenient.</summary>
    Monitor,

    /// <summary>A person should look at this before the player plays much longer.</summary>
    ReviewManually,

    /// <summary>Strong enough to open a case on. Still not a ban.</summary>
    Investigate,
}

/// <param name="Score">0-100. Higher is more concerning.</param>
/// <param name="Confidence">How much the score can be relied on.</param>
/// <param name="Signals">Every reason, strongest first.</param>
/// <param name="Assessment">One sentence naming what this looks like.</param>
/// <param name="Recommendation">What a human should do. Never what the bot has done.</param>
public sealed record RiskAssessment(
    int Score,
    RiskConfidence Confidence,
    IReadOnlyList<RiskSignal> Signals,
    string Assessment,
    RiskRecommendation Recommendation)
{
    public static RiskAssessment Clean { get; } =
        new(0, RiskConfidence.Low, [], "Nothing on record.", RiskRecommendation.None);

    /// <summary>The band a score falls in, for colour and wording.</summary>
    public RiskBand Band => RiskScorer.BandOf(Score);
}

public enum RiskBand { None, Low, Medium, High, Critical }
