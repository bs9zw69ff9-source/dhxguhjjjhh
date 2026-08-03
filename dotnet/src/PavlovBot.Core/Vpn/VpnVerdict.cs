namespace PavlovBot.Core.Vpn;

/// <summary>Where a screening landed, as one of six answers and nothing in between.</summary>
public enum VpnStance
{
    /// <summary>No screening was run at all.</summary>
    NotChecked,

    /// <summary>A private or LAN address. There is nothing to look up.</summary>
    Local,

    /// <summary>Checks ran and none flagged it.</summary>
    Clean,

    /// <summary>Enough checks flagged it, and nothing could confirm either way.</summary>
    Likely,

    /// <summary>Flagged, and the confirming check DISAGREED.</summary>
    Disputed,

    /// <summary>Flagged, and the confirming check agreed.</summary>
    Confirmed,
}

/// <param name="Headline">The verdict, alone, in caps. The only thing that must be read.</param>
/// <param name="Plain">One sentence of ordinary English explaining the headline.</param>
/// <param name="Action">What the bot did about it, in the past tense.</param>
public sealed record VpnSummary(VpnStance Stance, string Headline, string Plain, string Action)
{
    /// <summary>True when this warrants a second look from a human.</summary>
    public bool Concerning => Stance is VpnStance.Likely or VpnStance.Disputed or VpnStance.Confirmed;
}

/// <summary>
/// One verdict, worded one way, for every surface that shows a screening result.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS: the same screening was rendered three different ways - the connection
/// card built its own multi-line breakdown, <c>/iplookup</c> printed
/// "VPN + datacenter (flagged (inconclusive))", and the ban feed wrote a third phrasing.
/// All three led with detector arithmetic rather than a verdict, so the reader had to work
/// out what "2/3 flagged, confirmer gave no verdict" was supposed to mean about the person
/// in front of them, and the three could disagree in wording about the same address.
///
/// THE HEADLINE IS THE PRODUCT. Everything else on a card is supporting evidence for
/// somebody who wants it; the headline is what gets read, and it has to answer "is this
/// person on a VPN" without arithmetic. The detector breakdown still exists, underneath,
/// because a moderator challenged on a ban needs to be able to show their working.
///
/// "LIKELY" IS NOT "CONFIRMED" AND NEITHER IS "CLEAN". The three-way distinction is the
/// point of the whole tier system and it survives here, because collapsing it would make
/// the bot sound certain about something it is not - and that certainty ends up in a ban
/// appeal.
/// </remarks>
public static class VpnVerdict
{
    /// <summary>The verdict for a screening, or the not-checked one when there is none.</summary>
    public static VpnSummary Of(VpnRecord? record)
    {
        if (record is null)
            return new(VpnStance.NotChecked, "NOT CHECKED",
                "No VPN screening ran for this address.", "No action.");

        if (record.Local)
            return new(VpnStance.Local, "LOCAL ADDRESS",
                "This is a private or LAN address, so there is nothing to look up.", "No action.");

        var decision = record.Decision;
        var screens = Count(record.ScreenHits, record.ScreenAnswered, "check");

        if (!decision.Flagged)
        {
            return new(VpnStance.Clean, "CLEAN",
                record.ScreenAnswered > 0
                    ? $"All {record.ScreenAnswered} check{(record.ScreenAnswered == 1 ? "" : "s")} agree this is a normal connection."
                    : "Nothing flagged this address.",
                "No action — nothing to act on.");
        }

        var action = decision.Actionable
            ? "AUTO-BANNED."
            : "Not banned — flagged, but below the threshold to act.";

        return decision.Confirmed switch
        {
            true => new(VpnStance.Confirmed, "VPN / PROXY — CONFIRMED",
                $"{screens} flagged this address, and the confirming check agreed.", action),

            /* DISPUTED IS STILL A BAN WHEN IT IS ACTIONABLE. The confirmer used to hold a
               veto, so "disputed" and "not banned" meant the same thing; two screeners
               agreeing now outvotes it, and a card reading "not banned" beside a player who
               had just been banned gets read as a bug in the ban. */
            false => new(VpnStance.Disputed, "VPN / PROXY — DISPUTED",
                $"{screens} flagged this address, but the confirming check cleared it.", action),

            null => new(VpnStance.Likely, "VPN / PROXY — LIKELY",
                $"{screens} flagged this address. " +
                (record.ConfirmAnswered > 0
                    ? "The confirming check gave no verdict, so this is unconfirmed."
                    : "No confirming check is configured, so this is unconfirmed."),
                action),
        };
    }

    /// <summary>
    /// The anonymising signals, named rather than counted: "VPN, datacenter".
    /// </summary>
    /// <remarks>
    /// Only what a detector positively asserted. A null is "did not answer", never "no" -
    /// the distinction this whole subsystem is built on.
    /// </remarks>
    public static IReadOnlyList<string> Signals(VpnRecord? record)
    {
        if (record is null) return [];

        var signals = new List<string>();
        if (record.Vpn == true) signals.Add("VPN");
        if (record.Proxy == true) signals.Add("proxy");
        if (record.Tor == true) signals.Add("Tor");
        if (record.Hosting == true) signals.Add("datacenter");
        return signals;
    }

    /// <summary>"2 of 3 checks", worded so it never reads as a fraction to be evaluated.</summary>
    private static string Count(int hits, int answered, string noun) =>
        answered > 0
            ? $"{hits} of {answered} {noun}{(answered == 1 ? "" : "s")}"
            : $"{hits} {noun}{(hits == 1 ? "" : "s")}";
}
