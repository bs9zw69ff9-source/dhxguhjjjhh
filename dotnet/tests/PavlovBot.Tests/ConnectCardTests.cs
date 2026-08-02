using PavlovBot.Core.Evasion;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The connection card, which the port had flattened into a single line.
/// </summary>
public class ConnectCardTests
{
    private static VpnRecord Screened(
        bool flagged = false, bool? confirmed = null, bool? vpn = null, bool? residential = null,
        bool? actionable = null,
        params DetectorReading[] detectors) =>
        new()
        {
            Ip = "203.0.113.9",
            // Actionable is now INDEPENDENT of the confirmation - two screeners agreeing
            // bans over a confirmer's objection - so the card's tests have to set it.
            Decision = new VpnDecision(flagged, confirmed, actionable ?? flagged, "test"),
            Vpn = vpn,
            Residential = residential,
            Asn = "AS577",
            Isp = "Bell Canada",
            Country = "Canada",
            City = "Toronto",
            Detectors = detectors,
            ScreenHits = detectors.Count(d => d.Tier == 1 && d.Flagged),
            ScreenAnswered = detectors.Count(d => d.Tier == 1),
            ConfirmHits = detectors.Count(d => d.Tier == 2 && d.Flagged),
            ConfirmAnswered = detectors.Count(d => d.Tier == 2),
        };

    private static DetectorReading Reading(string name, int tier, bool flagged) =>
        new() { Name = name, Tier = tier, Flagged = flagged };

    private static string Render(
        VpnRecord? vpn = null, AccountRecord? account = null, IReadOnlyList<AccountRecord>? alts = null,
        bool flagged = false, bool master = false, bool confidentIp = true)
    {
        var embed = ConnectCard.Build("Pkdestroy", "76561198000000001", "203.0.113.9", confidentIp,
            "server1", account, alts ?? [], vpn, flagged, master, DateTimeOffset.UtcNow);

        return embed.Title + "\n" + embed.Description + "\n" +
               string.Join("\n", embed.Fields.Select(f => $"{f.Name}: {f.Value}"));
    }

    [Fact]
    public void TheCardCarriesEverythingTheOneLinerDropped()
    {
        /* The regression this fixes: the port reduced a card with address history, alts,
           ASN and a per-detector breakdown to "JOIN name | ip | city | vpn=FLAGGED", which
           tells a moderator nothing they can act on. */
        var account = new AccountRecord("76561198000000001",
            ConfirmedIps: ["203.0.113.9", "198.51.100.4"], GuessedIps: [], Names: ["Pkdestroy"],
            FirstSeen: DateTimeOffset.UtcNow.AddDays(-30), LastSeen: DateTimeOffset.UtcNow);

        var text = Render(
            vpn: Screened(vpn: true, residential: false),
            account: account,
            alts: [new AccountRecord("2", [], [], ["OtherGuy"])]);

        foreach (var expected in new[]
        {
            "IP address", "Account ID", "First seen", "Last seen", "Possible alts",
            "Network", "Detection", "VPN / proxy", "Location", "Recent addresses",
        })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.Contains("AS577", text, StringComparison.Ordinal);
        Assert.Contains("OtherGuy", text, StringComparison.Ordinal);
        Assert.Contains("198.51.100.4", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictNamesWhichDetectorsSaidWhat()
    {
        /* "FLAGGED (inconclusive)" alone is unactionable. Which detectors flagged it, and
           whether a confirmer was even consulted, is the difference between "ban them" and
           "leave it". */
        var text = Render(Screened(flagged: true, confirmed: null,
            detectors: [Reading("iphub", 1, true), Reading("vpnapi", 1, false)]));

        Assert.Contains("iphub", text, StringComparison.Ordinal);
        Assert.Contains("vpnapi", text, StringComparison.Ordinal);
        Assert.Contains("🔴", text, StringComparison.Ordinal);
        Assert.Contains("🟢", text, StringComparison.Ordinal);
        Assert.Contains("no confirmer configured", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfirmedVerdictSaysSoAndShowsTheConfirmer()
    {
        var text = Render(Screened(flagged: true, confirmed: true,
            detectors: [Reading("iphub", 1, true), Reading("ipqs", 2, true)]));

        Assert.Contains("CONFIRMED", text, StringComparison.Ordinal);
        Assert.Contains("final:", text, StringComparison.Ordinal);
        Assert.Contains("ipqs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisputedVerdictThatDidNotBanSaysSo()
    {
        // The one somebody would otherwise act on by mistake.
        var text = Render(Screened(flagged: true, confirmed: false, actionable: false,
            detectors: [Reading("iphub", 1, true), Reading("ipqs", 2, false)]));

        Assert.Contains("Disputed", text, StringComparison.Ordinal);
        Assert.Contains("not banned", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisputedVerdictThatBannedAnywaySaysThatInstead()
    {
        /* The confirmer used to hold a veto, so "disputed" and "not banned" were the same
           thing. Two screeners agreeing now bans over its objection - and a card still
           reading "not banned" beside a player who had just been banned would be read as a
           bug in the ban rather than a stale caption. */
        var text = Render(Screened(flagged: true, confirmed: false, actionable: true,
            detectors: [Reading("iphub", 1, true), Reading("vpnapi", 1, true), Reading("ipqs", 2, false)]));

        Assert.Contains("Disputed", text, StringComparison.Ordinal);
        Assert.Contains("banned anyway on consensus", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(not banned)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownIsItsOwnAnswer_NotNo()
    {
        /* A detector that could not tell you is not a detector telling you it is fine.
           Collapsing null into "No" is how somebody reads a card as clean when nothing
           actually checked. */
        var text = Render(Screened(vpn: null));

        Assert.Contains("VPN: unknown", text, StringComparison.Ordinal);
        Assert.DoesNotContain("VPN: No", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfirmedAddressIsMarkedAsSuch()
    {
        /* A guessed address is a correlation with a nearby log line. Presenting it as fact
           is how the wrong person gets banned off somebody else's connection. */
        var text = Render(confidentIp: false);

        Assert.Contains("unconfirmed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMasterAccountSaysItIsProtected()
    {
        Assert.Contains("master account", Render(master: true), StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagMatchIsCalledOut()
    {
        Assert.Contains("Flagged", Render(flagged: true), StringComparison.Ordinal);
    }

    [Fact]
    public void NoScreeningAtAllSaysNotChecked_RatherThanClean()
    {
        // Silence is not innocence, and the card must not imply it is.
        var text = Render(vpn: null);

        Assert.Contains("Not checked", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Clean", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFieldStaysInsideDiscordsLimit()
    {
        /* An over-long field makes Discord reject the WHOLE message, so a player with two
           hundred alts would silently produce no card at all. */
        var manyAlts = Enumerable.Range(0, 200)
            .Select(i => new AccountRecord($"{i}", [], [], [$"AltAccountWithALongName{i}"]))
            .ToList();

        var account = new AccountRecord("1",
            Enumerable.Range(0, 200).Select(i => $"203.0.113.{i % 256}").ToList(), [], ["Pkdestroy"]);

        var embed = ConnectCard.Build("Pkdestroy", "76561198000000001", "203.0.113.9", true,
            "server1", account, manyAlts, Screened(), flagged: false, master: false, DateTimeOffset.UtcNow);

        foreach (var field in embed.Fields)
            Assert.True(field.Value.ToString()!.Length <= 1024, $"{field.Name} is {field.Value.ToString()!.Length}");

        Assert.True(embed.Length <= 6000, $"embed is {embed.Length} characters");
    }
}
