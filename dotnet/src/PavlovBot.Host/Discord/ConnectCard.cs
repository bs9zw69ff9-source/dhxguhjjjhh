using System.Globalization;
using Discord;
using PavlovBot.Core.Evasion;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Core.Vpn;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The connection card: everything the bot knows about a player, at the moment they join.
/// </summary>
/// <remarks>
/// AN EMBED, unlike the other feeds, and the exception is deliberate. The join, kill and
/// money feeds are high-volume append-only logs people scroll and search, so they are plain
/// text. This is ONE CARD PER CONNECTION in a private admin channel, read closely by
/// somebody deciding whether to act - the port flattened it to a single line, which threw
/// away the address history, the alts and the per-detector breakdown that made it worth
/// reading.
///
/// FIELDS THAT ARE NOT KNOWN SAY "unknown" RATHER THAN BEING OMITTED. A card whose shape
/// changes per player is one you have to read carefully to notice something is missing;
/// a constant shape means an empty field is visible at a glance.
/// </remarks>
public static class ConnectCard
{
    public static Embed Build(
        string name,
        string accountId,
        string? ip,
        bool confidentIp,
        string server,
        AccountRecord? account,
        IReadOnlyList<AccountRecord> alts,
        VpnRecord? vpn,
        bool flagged,
        bool master,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(alts);

        var embed = new EmbedBuilder()
            .WithColor(flagged ? Theme.BanRed : vpn?.Decision.Flagged == true ? Theme.Amber : Theme.Green)
            .WithTitle($"Player information: {Sanitize.Message(name)}")
            .WithDescription($"{Sanitize.Message(name)} just connected on **{Sanitize.Message(server)}**.");

        /* Address and account id in inline code so they are one tap to copy - these are the
           two things somebody reading this card is most likely to want in their clipboard. */
        embed.AddField("IP address", ip is { Length: > 0 }
            // A guessed address is MARKED, never presented as fact. It is a correlation
            // with a nearby log line, and acting on it as certain bans the wrong person.
            ? $"`{ip}`{(confidentIp ? "" : "  ⚠️ *unconfirmed - correlated, not paired*")}"
            : "unknown");

        embed.AddField("Account ID", accountId is { Length: > 0 } ? $"`{accountId}`" : "unknown");

        embed.AddField("First seen", Stamp(account?.FirstSeen), inline: true);
        embed.AddField("Last seen", Stamp(account?.LastSeen), inline: true);
        embed.AddField("Known addresses",
            (account?.ConfirmedIps.Count ?? 0).ToString(CultureInfo.InvariantCulture) + " confirmed", inline: true);

        embed.AddField("Possible alts", alts.Count == 0
            ? "None"
            : Truncate(string.Join(", ", alts.Select(a => Sanitize.Message(a.Name ?? a.Id))), 1024));

        embed.AddField("Protected from auto-ban", master ? "Yes — master account" : "No", inline: true);
        embed.AddField("Server", Sanitize.Message(server), inline: true);
        embed.AddField("Flag match", flagged
            ? "**Flagged** — matches a standing blacklist entry"
            : "No matches", inline: true);

        embed.AddField("Network", string.Join("\n",
            $"ASN: {Code(vpn?.Asn)}",
            $"Organization: {Plain(vpn?.Organization ?? vpn?.Isp)}",
            $"Country: {Plain(vpn?.Country)}"));

        /* Explicit yes/no per category, with the risky answer in bold. "unknown" is its own
           answer and is NOT collapsed into "No" - a detector that could not tell you is not
           a detector telling you it is fine. */
        embed.AddField("Detection", string.Join("\n", new[]
        {
            $"VPN: {YesNo(vpn?.Vpn)}",
            $"Proxy: {YesNo(vpn?.Proxy)}",
            $"Tor: {YesNo(vpn?.Tor)}",
            $"Hosting/datacenter: {YesNo(vpn?.Hosting)}",
            $"Residential: {YesNo(vpn?.Residential, riskyWhen: false)}",
            vpn?.ThreatScore is { } score ? $"Threat score: **{score:0}**/100" : null,
        }.Where(line => line is not null)));

        embed.AddField("VPN / proxy", Truncate(Verdict(vpn), 1024));

        embed.AddField("Location", Truncate(Location(vpn, ip), 1024));

        embed.AddField("Recent addresses", Truncate(Addresses(account), 1024));

        return embed.Brand($"Connection log — {EasternTime.Stamp(at)} Eastern").Build();
    }

    /// <summary>
    /// The consensus, spelled out rather than reduced to a word.
    /// </summary>
    /// <remarks>
    /// "FLAGGED (inconclusive)" on its own tells a moderator nothing about what to do. What
    /// makes it actionable is WHICH detectors said what, and whether a confirmer was even
    /// consulted - a screen hit that nothing could confirm is a very different thing from
    /// one that was confirmed.
    /// </remarks>
    private static string Verdict(VpnRecord? vpn)
    {
        if (vpn is null) return "Not checked";
        if (vpn.Local) return "Skipped — private/LAN address, nothing to check";

        var decision = vpn.Decision;
        var tier1 = vpn.Detectors.Where(d => d.Tier == 1).ToList();
        var tier2 = vpn.Detectors.Where(d => d.Tier == 2).ToList();

        if (vpn.Detectors.Count == 0)
            return $"**{decision.Label}** — {decision.Reason}\n*No per-detector breakdown (cached before detectors were recorded).*";

        static string Pip(DetectorReading d) => $"{(d.Flagged ? "🔴" : "🟢")} {d.Name}";

        var lines = new List<string>();

        if (!decision.Flagged)
        {
            lines.Add(vpn.ScreenAnswered > 0
                ? $"**Clean** — all {vpn.ScreenAnswered} regular check(s) agree"
                : "**Clean**");
            if (tier1.Count > 0) lines.Add(string.Join("  ", tier1.Select(Pip)));
            if (tier2.Count == 0 && vpn.ScreenAnswered > 0)
                lines.Add("*Not escalated — no regular check flagged it.*");
        }
        else if (decision.Confirmed == true && tier2.Count > 0)
        {
            lines.Add($"🚫 **VPN/proxy CONFIRMED** — {vpn.ScreenHits}/{vpn.ScreenAnswered} flagged, " +
                      $"confirmed {vpn.ConfirmHits}/{vpn.ConfirmAnswered}");
            if (tier1.Count > 0) lines.Add(string.Join("  ", tier1.Select(Pip)));
            lines.Add($"→ final: {string.Join("  ", tier2.Select(Pip))}");
        }
        else if (decision.Confirmed == false)
        {
            lines.Add($"⚠️ **Disputed** — {vpn.ScreenHits}/{vpn.ScreenAnswered} flagged, but the confirmer cleared it (not banned)");
            if (tier1.Count > 0) lines.Add(string.Join("  ", tier1.Select(Pip)));
            lines.Add($"→ final: {string.Join("  ", tier2.Select(Pip))}");
        }
        else
        {
            lines.Add($"⚠️ **Flagged by {vpn.ScreenHits}/{vpn.ScreenAnswered} regular check(s)** — " +
                      $"{(tier2.Count > 0 ? "the confirmer gave no verdict" : "no confirmer configured")}, so unconfirmed");
            if (tier1.Count > 0) lines.Add(string.Join("  ", tier1.Select(Pip)));
        }

        var meta = new[]
        {
            vpn.Provider is { Length: > 0 } p ? $"provider: **{Sanitize.Message(p)}**" : null,
            vpn.Isp is { Length: > 0 } isp ? Sanitize.Message(isp) : null,
        }.Where(m => m is not null).ToList();

        if (meta.Count > 0) lines.Add(string.Join("  —  ", meta));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The addresses on record, confirmed first.
    /// </summary>
    /// <remarks>
    /// No per-connection timestamps here, unlike the Node bot: this bot stores the SET of
    /// addresses an account has used, not a log of connections. Listing them undated is
    /// honest; inventing a time for each would not be.
    /// </remarks>
    private static string Addresses(AccountRecord? account)
    {
        if (account is null) return "```\nno records\n```";

        var lines = account.ConfirmedIps.TakeLast(8).Reverse().Select(ip => $"{ip}   confirmed")
            .Concat(account.GuessedIps.TakeLast(4).Reverse().Select(ip => $"{ip}   unconfirmed"))
            .ToList();

        return lines.Count == 0 ? "```\nno records\n```" : "```\n" + string.Join("\n", lines) + "\n```";
    }

    /// <summary>City, region, country and timezone - whatever of it is known.</summary>
    private static string Location(VpnRecord? vpn, string? ip)
    {
        if (vpn is null) return ip is { Length: > 0 } ? "unknown" : "no address";

        var place = new[] { vpn.City, vpn.Region, vpn.Country }
            .Where(part => !string.IsNullOrEmpty(part))
            .Select(part => Sanitize.Message(part!))
            .ToList();

        return place.Count > 0 ? string.Join(", ", place) : "unknown";
    }

    private static string Stamp(DateTimeOffset? at) => at is { } when ? EasternTime.Stamp(when) : "unknown";

    private static string Code(string? value) => value is { Length: > 0 } ? $"`{Sanitize.Message(value)}`" : "unknown";

    private static string Plain(string? value) => value is { Length: > 0 } ? Sanitize.Message(value) : "unknown";

    /// <param name="riskyWhen">Which answer is the worrying one, so it is the one in bold.</param>
    private static string YesNo(bool? value, bool riskyWhen = true)
    {
        if (value is not { } known) return "unknown";

        // The worrying answer is the bold one, so a card can be skimmed for what matters.
        return known == riskyWhen
            ? known ? "**YES**" : "**No**"
            : known ? "Yes" : "No";
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
