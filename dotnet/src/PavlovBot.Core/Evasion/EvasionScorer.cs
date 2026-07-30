namespace PavlovBot.Core.Evasion;

/// <summary>
/// Decides whether a joining player looks like a returning banned one.
/// </summary>
/// <remarks>
/// Pure: all state is passed in, nothing is read from disk, Discord or the network, and
/// nobody is banned here. The caller decides what to do with the verdict.
///
/// Every signal is scored independently and the strongest are reported, so a moderator
/// sees WHY a match fired rather than a bare "suspicious".
/// </remarks>
public static class EvasionScorer
{
    /// <summary>Score at or above which a match is reported to moderators.</summary>
    public const int MatchThreshold = 50;

    private static string Norm(string? s) => s?.Trim().ToLowerInvariant() ?? string.Empty;

    private static HashSet<string> NormSet(params IEnumerable<string?>[] sources)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
            foreach (var value in source)
            {
                var n = Norm(value);
                if (n.Length > 0) set.Add(n);
            }
        return set;
    }

    public static EvasionResult Find(JoinContext join, IEnumerable<BanRecord> bans, RegistryFlags? flags = null)
    {
        ArgumentNullException.ThrowIfNull(join);
        ArgumentNullException.ThrowIfNull(bans);
        flags ??= RegistryFlags.Empty;

        var joinName = Norm(join.Name);
        var joinEos = Norm(join.EosId);
        var joinIp = join.Ip?.Trim() ?? string.Empty;
        var joinAsn = Norm(join.Asn);
        var joinProvider = Norm(join.Provider);
        var joinAlts = join.AltNames.Select(Norm).Where(a => a.Length > 0).ToArray();

        var matches = new List<EvasionMatch>();

        foreach (var ban in bans)
        {
            if (ban is null || string.IsNullOrWhiteSpace(ban.PlayerId)) continue;   // skip corrupt rows
            var net = ban.Network ?? new BanNetwork();
            var reasons = new List<EvasionReason>();
            var definitive = false;

            // -- EOS account: the same account returning under any name.
            if (joinEos.Length > 0 && NormSet(net.EosIds).Contains(joinEos))
            {
                definitive = true;
                reasons.Add(new EvasionReason(SignalKind.Eos, $"same EOS account ({join.EosId})"));
            }

            // -- IP history: the join IP appears in this ban's recorded addresses.
            if (joinIp.Length > 0 && net.Ips.Contains(joinIp, StringComparer.Ordinal))
                reasons.Add(new EvasionReason(SignalKind.Ip, $"connected from a banned IP ({joinIp})"));

            // -- Username: exact, or one of their known shared-IP aliases.
            var banNames = NormSet([ban.PlayerId], net.Names);
            if (joinName.Length > 0 && banNames.Contains(joinName))
            {
                reasons.Add(new EvasionReason(SignalKind.Username, $"username matches a banned player ({ban.PlayerId})"));
            }
            else
            {
                var alias = Array.Find(joinAlts, banNames.Contains);
                if (alias is not null)
                    reasons.Add(new EvasionReason(SignalKind.Username, $"known alias of a banned player ({alias})"));
            }

            // -- VPN provider: same consumer brand (NordVPN, Mullvad...).
            var banProvider = Norm(net.Provider);
            if (joinProvider.Length > 0 && banProvider.Length > 0 && joinProvider == banProvider)
                reasons.Add(new EvasionReason(SignalKind.Provider, $"same VPN provider ({join.Provider})"));

            /* -- ASN: near-worthless alone, since an ASN can be a whole national ISP, so it
                  only ever ADDS confidence. It scores higher when both sides are also on an
                  anonymising network - a shared VPN ASN is far more suspicious than a shared
                  consumer ISP. */
            var banAsn = Norm(net.Asn);
            if (joinAsn.Length > 0 && banAsn.Length > 0 && joinAsn == banAsn)
            {
                var bothAnon = join.IsAnonymising && net.IsAnonymising;
                reasons.Add(bothAnon
                    ? new EvasionReason(SignalKind.AsnVpn,
                        $"same VPN/hosting network as a banned player ({join.Asn}" +
                        (net.Organization is { Length: > 0 } org ? $" - {org}" : "") + ")")
                    : new EvasionReason(SignalKind.Asn, $"same network/ASN ({join.Asn}) - weak on its own"));
            }

            if (reasons.Count == 0) continue;

            var score = reasons.Sum(r => r.Weight);
            // A lone ASN hit never reports: entire ISPs share one, so it would flag neighbours.
            var onlyWeakAsn = reasons.TrueForAll(r => r.Kind == SignalKind.Asn);
            if (!definitive && (score < MatchThreshold || onlyWeakAsn)) continue;

            matches.Add(new EvasionMatch
            {
                PlayerId = ban.PlayerId,
                Reason = ban.Reason,
                Moderator = ban.Moderator,
                Permanent = ban.Permanent,
                Expires = ban.Expires,
                Score = score,
                Definitive = definitive,
                Reasons = [.. reasons.OrderByDescending(r => r.Weight)],
            });
        }

        /* Standing flags in the registry, independent of any ban RECORD - covers an auto-ban
           that flagged an id/IP/name without writing a per-player ban row. */
        var flagHits = new List<FlagHit>();
        if (joinEos.Length > 0 && flags.EosIds.Contains(joinEos))
            flagHits.Add(new FlagHit(SignalKind.Eos, "EOS account is flagged in the ban registry"));
        if (joinIp.Length > 0 && flags.Ips.Contains(joinIp))
            flagHits.Add(new FlagHit(SignalKind.Ip, $"IP {joinIp} is flagged in the ban registry"));
        if (joinName.Length > 0 && flags.Names.Contains(joinName))
            flagHits.Add(new FlagHit(SignalKind.Username, "username is flagged in the ban registry"));

        matches.Sort((a, b) => b.Score.CompareTo(a.Score));
        return new EvasionResult { Matches = matches, Flags = flagHits };
    }
}
