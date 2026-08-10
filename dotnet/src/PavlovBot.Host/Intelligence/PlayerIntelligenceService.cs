using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Core.Intelligence;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Discord;
using PavlovBot.Core.Economy;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using PavlovBot.Host.Vpn;
using CoreBanRecord = PavlovBot.Core.Moderation.BanRecord;

namespace PavlovBot.Host.Intelligence;

/// <summary>
/// One player, assembled from every system that already knows something about them.
/// </summary>
/// <remarks>
/// THIS OWNS NO DATA. Every field it returns is read from the service that already owns it,
/// and nothing here is persisted. That is the whole design: the bot already records a player
/// in the IP tracker, the ban list, the warning store, the roster files, the playtime table,
/// the ledger and the audit log, and a seventh copy would be a seventh thing to go stale.
/// What was missing was not another store - it was one place that joins them.
///
/// WHY IT IS A SERVICE AND NOT A COMMAND. The join is the valuable part and it has three
/// consumers already: the /player command, the risk assessment, and anything later that needs
/// to know who somebody is. Putting it in a Discord handler would mean the second consumer
/// re-implements it from the same six sources, which is exactly how the six sources came to
/// disagree in the first place.
///
/// EVERY DEPENDENCY IS OPTIONAL, and that is not defensive padding. This bot runs in
/// deployments with no ledger directory, no roster path and no VPN keys, and a profile that
/// throws because payroll is unconfigured would make the command useless precisely where it
/// is most needed - a fresh install somebody is still setting up.
/// </remarks>
public sealed class PlayerIntelligenceService(
    IpTrackingService tracking,
    BanService bans,
    WarningService warnings,
    RconRegistry rcon,
    SerializedStore store,
    ILogger<PlayerIntelligenceService> logger,
    RosterService? rosters = null,
    FactionMembers? members = null,
    IBalanceStore? balances = null,
    Payroll? payroll = null,
    VpnScreeningService? vpn = null)
{
    /// <summary>How recently an account must have appeared to count as brand new.</summary>
    /// <remarks>
    /// A day. Long enough that a genuinely new player is still flagged as new when staff look
    /// in the morning, short enough that it is not describing half the server after a
    /// promotion. It is a CONTEXT signal and weighted accordingly - being new is not evidence
    /// of anything on its own.
    /// </remarks>
    public static TimeSpan NewAccountWindow { get; } = TimeSpan.FromDays(1);

    /// <summary>Everything known about one player, by in-game name.</summary>
    /// <remarks>
    /// BY NAME because that is what staff have - a name in a killfeed or a report. The
    /// account id is resolved from it where possible and is preferred for everything
    /// afterwards, since it is the only identifier a player cannot change.
    /// </remarks>
    public async Task<PlayerProfile> ProfileAsync(string player, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player);

        var name = player.Trim();
        var account = tracking.AccountByName(name);

        var identity = new ProfileIdentity(
            name,
            account?.Id,
            [.. (account?.Names ?? []).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase))]);

        var activity = Activity(name, account);
        var network = Network(account);
        var moderation = Moderation(name);
        var faction = await FactionOfAsync(name, ct).ConfigureAwait(false);
        var economy = Economy(name);
        var associations = Associations(account, moderation);

        var risk = RiskScorer.Assess(
            Signals(name, account, network, moderation, associations, activity));

        return new PlayerProfile(identity, activity, network, moderation, faction, economy, associations, risk);
    }

    /// <summary>A profile narrowed to what the viewer may see.</summary>
    /// <remarks>
    /// THE ONLY ENTRY POINT THE DISCORD LAYER SHOULD USE. Redacting here rather than at each
    /// embed means the command never holds an address it must not print - and an address
    /// posted to a channel cannot be un-posted by deleting the message.
    /// </remarks>
    public async Task<PlayerProfile> ProfileAsync(string player, ProfileVisibility visibility, CancellationToken ct = default) =>
        ProfileRedaction.For(await ProfileAsync(player, ct).ConfigureAwait(false), visibility);

    private ProfileActivity Activity(string name, AccountRecord? account)
    {
        var playtime = store.Read(Datasets.Playtime, new Dictionary<string, PlaytimeEntry>(StringComparer.OrdinalIgnoreCase))
            .GetValueOrDefault(name);

        var lastSeen = store.Read(Datasets.LastSeen, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase))
            .TryGetValue(name, out var seen) ? seen : playtime?.LastSeen;

        var servers = rcon.Servers
            .Where(s => rcon.Roster(s).Players.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new ProfileActivity(
            account?.FirstSeen,
            lastSeen,
            playtime?.Minutes ?? 0,
            servers.Count > 0,
            servers);
    }

    private ProfileNetwork Network(AccountRecord? account)
    {
        if (account is null) return ProfileNetwork.Empty;

        /* THE MOST RECENT CONFIRMED ADDRESS drives the VPN answer. Screening is per address,
           and an old one they have not used in months describes where they were, not where
           they are. */
        var record = account.ConfirmedIps.Count > 0 ? vpn?.Cached(account.ConfirmedIps[^1]) : null;

        return new ProfileNetwork(
            account.ConfirmedIps,
            account.GuessedIps,
            record?.Vpn,
            record?.Proxy,
            record?.Asn,
            record?.Country);
    }

    private ProfileModeration Moderation(string name)
    {
        var all = bans.LoadBans();
        var active = all.FirstOrDefault(b => BanRules.SamePlayer(b.PlayerId, name) && b.IsActiveAt(DateTimeOffset.UtcNow));

        var log = store.Read<List<ModAction>>(Datasets.ModLog, [])
            .Where(a => a is not null && BanRules.SamePlayer(a.Player, name))
            .OrderByDescending(a => a.At)
            .ToList();

        return new ProfileModeration(
            active?.Reason ?? (active is not null ? "no reason recorded" : null),
            active?.Expires,
            active?.Permanent ?? false,
            warnings.ActiveFor(name).Count,
            warnings.For(name).Count,
            log.Count(a => string.Equals(a.Action, "kick", StringComparison.OrdinalIgnoreCase)),
            [.. log.Take(10).Select(a => $"{a.Action} by {a.Moderator}")]);
    }

    private async Task<ProfileFaction> FactionOfAsync(string name, CancellationToken ct)
    {
        if (rosters is null || !rosters.Enabled) return ProfileFaction.None;

        var membership = await rosters.FindAsync(name, ct).ConfigureAwait(false);
        if (membership is null) return ProfileFaction.None;

        return new ProfileFaction(membership.Faction.Name, membership.Rank, members?.OwnerOf(name));
    }

    private ProfileEconomy Economy(string name)
    {
        /* READ THROUGH IBalanceStore, not through Ledger. Ledger exists to SERIALISE
           mutations; a profile only reads, and taking the mutating type would suggest this
           screen can move money. Null and zero are different facts here - the bot never
           creates a ledger, so "no file" means they have never banked, not that they are
           broke. */
        var balance = balances?.Read(name);

        return new ProfileEconomy(
            balance,
            payroll?.OwedTo(name) ?? 0,
            payroll?.TotalPaidTo(name) ?? 0);
    }

    /// <summary>
    /// Accounts sharing a confirmed address, and whether any of them is banned.
    /// </summary>
    /// <remarks>
    /// CONFIRMED ADDRESSES ONLY, matching AltsOf. A guessed address is a correlation between
    /// a join line and a nearby address line; on a busy server two players connecting a
    /// second apart can be attributed to each other, and an association built on that would
    /// put a stranger's name on somebody's profile as a "known associate".
    /// </remarks>
    private IReadOnlyList<ProfileAssociation> Associations(AccountRecord? account, ProfileModeration moderation)
    {
        if (account is null) return [];

        IReadOnlyList<AccountRecord> alts;
        try
        {
            alts = tracking.AltsOf(account.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A profile that fails entirely because one section could not be built is worse
            // than a profile with an empty section, on a screen somebody is using to decide.
            logger.LogWarning(ex, "Could not read the accounts associated with {Account}", account.Id);
            return [];
        }

        var banned = bans.ActiveBans().Select(b => b.PlayerId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. alts
                .Select(a => new ProfileAssociation(
                    a.Name ?? a.Id,
                    a.Id,
                    "shared a confirmed address",
                    a.Names.Any(banned.Contains)))
                .OrderByDescending(a => a.Banned)
                .Take(25),
        ];
    }

    /// <summary>
    /// Every risk signal this player raises, with the evidence behind each one.
    /// </summary>
    /// <remarks>
    /// GATHERING ONLY. The arithmetic is <see cref="RiskScorer"/>'s, and keeping the two apart
    /// is what makes the score reproducible from the signals in a test with no filesystem.
    ///
    /// Signals that rest on an address are marked Sensitive, so redaction strips the evidence
    /// without changing the score - two moderators at different access levels see the same
    /// number and the same reasons, and only one of them sees the address.
    /// </remarks>
    private IReadOnlyList<RiskSignal> Signals(
        string name,
        AccountRecord? account,
        ProfileNetwork network,
        ProfileModeration moderation,
        IReadOnlyList<ProfileAssociation> associations,
        ProfileActivity activity)
    {
        var signals = new List<RiskSignal>();

        if (moderation.Banned)
        {
            signals.Add(new RiskSignal(
                RiskSignalKind.BannedAccount,
                "This account is banned right now.",
                moderation.ActiveBan));
        }

        var bannedAssociates = associations.Where(a => a.Banned).ToList();
        foreach (var associate in bannedAssociates)
        {
            signals.Add(new RiskSignal(
                RiskSignalKind.SharedAddressWithBanned,
                $"Shares a confirmed address with {associate.Name}, who is banned.",
                associate.AccountId,
                Sensitive: true));
        }

        if (account is not null)
        {
            var flags = tracking.LoadFlags();
            var verdict = FlagMatching.Check(
                flags, account.ConfirmedIps.Count > 0 ? account.ConfirmedIps[^1] : null, name, account.Id);

            if (verdict.Hit)
            {
                signals.Add(new RiskSignal(
                    RiskSignalKind.Flagged,
                    $"Matches a standing ban-evasion flag ({verdict.Match}).",
                    verdict.Detail,
                    Sensitive: verdict.Match == FlagMatch.Ip));
            }

            var evasion = Evasion(name, account, network);
            if (evasion is not null) signals.Add(evasion);
        }

        var priorBans = bans.LoadBans().Count(b => BanRules.SamePlayer(b.PlayerId, name));
        if (priorBans > 0 && !moderation.Banned)
        {
            signals.Add(new RiskSignal(
                RiskSignalKind.PriorBan,
                $"Has {priorBans} ban record(s) that are no longer in force.",
                null));
        }

        if (network.Vpn == true || network.Proxy == true)
        {
            signals.Add(new RiskSignal(
                RiskSignalKind.Anonymising,
                network.Proxy == true ? "Connecting through a proxy." : "Connecting through a VPN.",
                network.Asn));
        }

        if (moderation.Warnings > 0)
        {
            signals.Add(new RiskSignal(
                RiskSignalKind.ActiveWarnings,
                $"{moderation.Warnings} active warning(s) inside the decay window.",
                null));
        }

        if (activity.FirstSeen is { } first && DateTimeOffset.UtcNow - first < NewAccountWindow)
        {
            signals.Add(new RiskSignal(
                RiskSignalKind.BrandNewAccount,
                "First seen in the last day. Context only - most new players are new players.",
                null));
        }

        return signals;
    }

    /// <summary>
    /// The existing evasion scorer's verdict, as a risk signal.
    /// </summary>
    /// <remarks>
    /// REUSED, NOT REIMPLEMENTED. EvasionScorer already correlates a join against the ban list
    /// across EOS id, address, name, VPN provider and ASN, and it is the piece with the most
    /// production history behind it. A second scoring path over the same facts would drift
    /// from it and produce two different answers to one question.
    /// </remarks>
    private RiskSignal? Evasion(string name, AccountRecord account, ProfileNetwork network)
    {
        var join = new JoinContext
        {
            Name = name,
            EosId = account.Id,
            Ip = account.ConfirmedIps.Count > 0 ? account.ConfirmedIps[^1] : null,
            Asn = network.Asn,
            AltNames = account.Names,
        };

        var result = EvasionScorer.Find(join, bans.ActiveBans().Select(ToEvasionBan));

        /* THE SCORER'S OWN THRESHOLD, not one invented here. It is what decides whether a
           match is reported to moderators everywhere else in the bot, and a second opinion
           about the same number would mean /player and the auto-ban path disagreed about
           whether the very same join was a match. */
        if (result.Matches.Count == 0 || result.Score < EvasionScorer.MatchThreshold) return null;

        var best = result.Matches[0];
        var reason = best.Reasons.OrderByDescending(r => r.Weight).FirstOrDefault();

        return new RiskSignal(
            RiskSignalKind.EvasionMatch,
            $"The evasion scorer matched them to banned player {best.PlayerId} " +
            $"(score {result.Score}): {reason?.Detail ?? "no detail recorded"}.",
            best.PlayerId,
            Sensitive: reason?.Kind == SignalKind.Ip);
    }

    /// <summary>The moderation ban record, in the shape the evasion scorer reads.</summary>
    /// <remarks>
    /// TWO BanRecord TYPES EXIST - one in Core.Moderation for the ban list, one in
    /// Core.Evasion for correlation - and this is the seam between them. Merging them would
    /// put network intelligence on every stored ban, which is a storage change reaching every
    /// installation for the benefit of one caller.
    /// </remarks>
    private static PavlovBot.Core.Evasion.BanRecord ToEvasionBan(CoreBanRecord ban) => new()
    {
        PlayerId = ban.PlayerId,
        Reason = ban.Reason ?? "unknown",
        Moderator = ban.Moderator ?? "unknown",
        Permanent = ban.Permanent,
        Expires = ban.Expires,
        Network = ban.Network,
    };
}
