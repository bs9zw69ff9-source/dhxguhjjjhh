using System.Globalization;
using System.IO.Compression;
using System.Text;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Network;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/antiddos</c> - the firewall, its logs, CrowdSec and the bans, from Discord.
/// </summary>
/// <remarks>
/// READING IS ADMIN, CHANGING IS OWNER. Every view is ephemeral, so nothing from the logs is shown
/// to anybody but the person who asked; every change is audited twice, in the bot's audit log and
/// the anti-DDoS event log, with who did it.
///
/// NOTHING TYPED HERE REACHES A COMMAND LINE AS TEXT. Addresses are parsed to their canonical form
/// (<see cref="IpNetworks"/>), thresholds are names from a fixed list with fixed ranges, modes and
/// kinds are choices, and every firewall change goes through the service, which checks it with
/// <c>nft -c</c> before applying it atomically.
///
/// OUTPUT IS BOUNDED. A view shows at most a page; a search attaches at most
/// <see cref="MaxAttachedRecords"/> records; a bigger investigation is an export, written to the
/// server and attached only when it fits Discord's limit.
/// </remarks>
public sealed class AntiDdosCommand(Access access, AuditLog audit, AntiDdosService service, ILogger<AntiDdosCommand> logger) : ISlashCommand
{
    internal const int PageSize = 15;
    internal const int MaxAttachedRecords = 5000;
    internal const int MaxExportRecords = 2_000_000;
    internal static readonly TimeSpan MaxHistory = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MaxExport = TimeSpan.FromDays(31);

    /// <summary>Discord's attachment limit for a server without boosts, with room to spare.</summary>
    internal const long MaxAttachmentBytes = 8L * 1024 * 1024;

    private const int ExportsKept = 10;

    public string Name => "antiddos";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        SlashCommandOptionBuilder Sub(string name, string description) =>
            new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.SubCommand);

        SlashCommandOptionBuilder Range(SlashCommandOptionBuilder b) => b
            .AddOption("minutes", ApplicationCommandOptionType.Integer, "How far back from now (default 60)", isRequired: false, minValue: 1, maxValue: 44_640)
            .AddOption("from", ApplicationCommandOptionType.String, "Start, UTC: 2026-10-29 17:30 (instead of minutes)", isRequired: false)
            .AddOption("to", ApplicationCommandOptionType.String, "End, UTC (default now)", isRequired: false);

        var kinds = new SlashCommandOptionBuilder().WithName("kind").WithDescription("Which records (default all)")
            .WithType(ApplicationCommandOptionType.String)
            .AddChoice("packets (logged by the firewall)", "packet").AddChoice("sources (per-minute totals)", "source")
            .AddChoice("traffic (per-minute totals)", "traffic").AddChoice("events", "event");
        var protocols = new SlashCommandOptionBuilder().WithName("protocol").WithDescription("Protocol")
            .WithType(ApplicationCommandOptionType.String).AddChoice("udp", "udp").AddChoice("tcp", "tcp").AddChoice("icmp", "icmp");
        var actions = new SlashCommandOptionBuilder().WithName("action").WithDescription("Firewall action or event action")
            .WithType(ApplicationCommandOptionType.String).AddChoice("dropped", "dropped").AddChoice("passed", "passed")
            .AddChoice("banned", "banned").AddChoice("unbanned", "unbanned");

        var thresholdNames = new SlashCommandOptionBuilder().WithName("name").WithDescription("Which threshold")
            .WithType(ApplicationCommandOptionType.String).WithRequired(true);
        foreach (var t in Thresholds.All) thresholdNames.AddChoice(t.Name, t.Name);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Admin - Anti-DDoS firewall, packet logs, CrowdSec and bans")
            .AddOption(Sub("status", "Admin - Firewall, logging, CrowdSec and storage at a glance"))
            .AddOption(Range(Sub("traffic", "Admin - Packet and byte rates over time, and the busiest sources")))
            .AddOption(Range(Sub("history", "Admin - Search the packet, source and event logs"))
                .AddOption("ip", ApplicationCommandOptionType.String, "Source address or network, e.g. 203.0.113.5 or 203.0.113.0/24", isRequired: false)
                .AddOption("port", ApplicationCommandOptionType.Integer, "Destination port", isRequired: false, minValue: 1, maxValue: 65535)
                .AddOption(protocols).AddOption(actions).AddOption(kinds)
                .AddOption("category", ApplicationCommandOptionType.String, "Drop reason or event category, e.g. blocked, suspect, ban", isRequired: false)
                .AddOption("page", ApplicationCommandOptionType.Integer, "Page of results (default 1)", isRequired: false, minValue: 1, maxValue: 1000))
            .AddOption(Range(Sub("top", "Admin - The highest-volume sources in a window"))
                .AddOption("port", ApplicationCommandOptionType.Integer, "Only sources sending to this port", isRequired: false, minValue: 1, maxValue: 65535))
            .AddOption(Sub("detections", "Admin - Recent detections and what was done about them")
                .AddOption("page", ApplicationCommandOptionType.Integer, "Page (default 1)", isRequired: false, minValue: 1, maxValue: 100))
            .AddOption(Sub("bans", "Admin - Everything blocked now: the firewall's own blocks and CrowdSec's bans")
                .AddOption("page", ApplicationCommandOptionType.Integer, "Page (default 1)", isRequired: false, minValue: 1, maxValue: 1000))
            .AddOption(Sub("crowdsec", "Admin - CrowdSec's decisions, recent alerts and health"))
            .AddOption(Sub("storage", "Admin - Log sizes, retention, oldest records and disk space"))
            .AddOption(Range(Sub("export", "Owner - Export a range of records for offline analysis"))
                .AddOption("ip", ApplicationCommandOptionType.String, "Only this source address or network", isRequired: false)
                .AddOption(kinds))
            .AddOption(Sub("block", "Owner - Ban an address or network (needs CrowdSec for permanent bans and ranges)")
                .AddOption("ip", ApplicationCommandOptionType.String, "Address or network (no wider than a /24 or /48)", isRequired: true)
                .AddOption("duration", ApplicationCommandOptionType.String, "30m, 4h, 7d, or permanent", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Why - kept with the ban", isRequired: true))
            .AddOption(Sub("unban", "Owner - Lift every ban on an address or network")
                .AddOption("ip", ApplicationCommandOptionType.String, "Address or network", isRequired: true))
            .AddOption(Sub("logging", "Owner - Raise logging for a while, or return it to normal")
                .AddOption(new SlashCommandOptionBuilder().WithName("mode").WithDescription("Logging mode")
                    .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                    .AddChoice("normal", "normal").AddChoice("elevated", "elevated").AddChoice("incident", "incident"))
                .AddOption("minutes", ApplicationCommandOptionType.Integer, "How long before it returns to normal (default 30; again to extend)",
                    isRequired: false, minValue: 5, maxValue: 1440))
            .AddOption(new SlashCommandOptionBuilder().WithName("allow").WithDescription("Owner - The trusted list")
                .WithType(ApplicationCommandOptionType.SubCommandGroup)
                .AddOption(Sub("add", "Owner - Trust an address or network (no wider than a /24 or /48)")
                    .AddOption("ip", ApplicationCommandOptionType.String, "Address or network", isRequired: true)
                    .AddOption("comment", ApplicationCommandOptionType.String, "Whose it is", isRequired: true))
                .AddOption(Sub("remove", "Owner - Stop trusting an address or network")
                    .AddOption("ip", ApplicationCommandOptionType.String, "Address or network", isRequired: true))
                .AddOption(Sub("list", "Admin - Everything trusted")))
            .AddOption(new SlashCommandOptionBuilder().WithName("threshold").WithDescription("Owner - Detection and filtering thresholds")
                .WithType(ApplicationCommandOptionType.SubCommandGroup)
                .AddOption(Sub("set", "Owner - Change a threshold (kept across restarts)")
                    .AddOption(thresholdNames)
                    .AddOption("value", ApplicationCommandOptionType.Integer, "New value", isRequired: true))
                .AddOption(Sub("list", "Admin - Every threshold, its value and its allowed range")))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var top = command.Data.Options.FirstOrDefault();
        var (name, options) = top?.Type == ApplicationCommandOptionType.SubCommandGroup
            ? ($"{top.Name} {top.Options.FirstOrDefault()?.Name}", top.Options.FirstOrDefault()?.Options ?? [])
            : (top?.Name ?? "", top?.Options ?? []);
        var args = options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal);

        var required = RequiredFor(name);
        if (!access.Allows(required, command))
        {
            await command.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = Theme.Denied("Not allowed", access.Refusal(required, command)).Build();
                m.AllowedMentions = AllowedMentions.None;
            }).ConfigureAwait(false);
            return;
        }

        var reply = await RunAsync(name, args, command.User.Username, ct).ConfigureAwait(false);
        using var stream = reply.File is { } bytes ? new MemoryStream(bytes) : null;
        await command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = reply.Embed.Build();
            if (stream is not null) m.Attachments = new[] { new FileAttachment(stream, reply.FileName) };
            m.AllowedMentions = AllowedMentions.None;
        }).ConfigureAwait(false);
    }

    /// <summary>Whether a subcommand changes something, and so is the owner's.</summary>
    internal static RequiredAccess RequiredFor(string name) =>
        name is "export" or "block" or "unban" or "logging" or "allow add" or "allow remove" or "threshold set"
            ? RequiredAccess.Owner
            : RequiredAccess.Admin;

    /// <summary>
    /// Run one action and return what to show. The caller has checked access. Shared by the slash
    /// command and the <c>/configure</c> panel, so the two can never do different things.
    /// </summary>
    /// <param name="args">Option values as Discord gives them: strings, and integers as <see cref="long"/>.</param>
    internal async Task<AntiDdosReply> RunAsync(string name, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        var command = new ReplySink(actor);
        try
        {
            switch (name)
            {
                case "status": await Reply(command, await StatusAsync(ct).ConfigureAwait(false)).ConfigureAwait(false); break;
                case "traffic": await TrafficAsync(command, args, ct).ConfigureAwait(false); break;
                case "history": await HistoryAsync(command, args, ct).ConfigureAwait(false); break;
                case "top": await TopAsync(command, args, ct).ConfigureAwait(false); break;
                case "detections": await Reply(command, Detections(Int(args, "page") ?? 1)).ConfigureAwait(false); break;
                case "bans": await Reply(command, await BansAsync(Int(args, "page") ?? 1, ct).ConfigureAwait(false)).ConfigureAwait(false); break;
                case "crowdsec": await Reply(command, await CrowdSecAsync(ct).ConfigureAwait(false)).ConfigureAwait(false); break;
                case "storage": await Reply(command, await StorageAsync(ct).ConfigureAwait(false)).ConfigureAwait(false); break;
                case "export": await ExportAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "block": await BlockAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "unban": await UnbanAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "logging": await LoggingAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "allow add": await TrustAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "allow remove": await UntrustAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "allow list": await Reply(command, TrustedList()).ConfigureAwait(false); break;
                case "threshold set": await ThresholdAsync(command, args, actor, ct).ConfigureAwait(false); break;
                case "threshold list": await Reply(command, ThresholdList()).ConfigureAwait(false); break;
                default: await Reply(command, Theme.Failure("Unknown subcommand")).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The details go to the bot's log; the reply never carries a stack trace or a path it should not.
            logger.LogError(ex, "/antiddos {Subcommand} failed", name);
            command.Set(Theme.Failure("That failed", "The error is in the bot's log. Nothing was half-applied: firewall changes are atomic."));
        }
        return command.Result ?? Theme.Failure("Nothing to show");
    }

    // ---- views ----

    private async Task<EmbedBuilder> StatusAsync(CancellationToken ct)
    {
        var fw = service.Firewall;
        var requested = service.Requested;
        var stats = await fw.StatsAsync(ct).ConfigureAwait(false);
        var w = service.LastWindow;

        var healthy = fw.Managed && fw.LastProblem is null && service.CrowdSecProblem is null && (service.Storage?.Level ?? StorageLevel.Ok) <= StorageLevel.Warning;
        var embed = (healthy ? Theme.Success("Anti-DDoS status") : Theme.Warning("Anti-DDoS status"))
            .AddField("Firewall", !fw.Managed ? "not managed by this bot"
                : fw.LastProblem is { } p ? $"⚠️ last change refused: {Sanitize.Code(Clip(p, 300))}"
                : $"`inet {FloodShieldRules.Table}` on `{Sanitize.Code(service.Interface)}` · " +
                  (fw.Settings.Enforce ? fw.Engaged ? "**attack limits ON**" : "filtering, attack limits standing by" : "counting and logging only (ATTACK_MITIGATION off)"), false)
            .AddField("Logging", $"{fw.Mode.ToString().ToLowerInvariant()}" +
                (requested.Until is { } until ? $" until {AntiDdosEmbeds.Discord(until)} (by {Sanitize.Code(requested.By)})" : "") +
                $" · at most {LogRates.For(fw.Mode).WorstCasePerSecond:0} packet records/s", false)
            .AddField("Last minute", w is null ? "no reading yet"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{w.Pps:0} pkt/s avg, {w.PeakPps:N0} peak, {w.Mbps:0.0} Mbit/s · {w.Sources} sources{(w.AccountingSaturated ? " (per-source table full - likely spoofed)" : "")}"), false)
            .AddField("Dropped (since load)", stats is null ? "unknown"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{stats.Dropped:N0} packets · {stats.BlockedSources} address(es) blocked by the firewall now"), true)
            .AddField("CrowdSec", !service.CrowdSec.Installed ? "not installed - see ANTIDDOS.md"
                : service.CrowdSecProblem is { } c ? $"⚠️ {Sanitize.Code(Clip(c, 200))}"
                : string.Create(CultureInfo.InvariantCulture, $"{service.Decisions.Count} active decision(s){(service.HealthProblem is { } h ? $" · ⚠️ {Sanitize.Code(Clip(h, 200))}" : " · healthy")}"), true)
            .AddField("Storage", service.Storage is { } s ? $"{s.Level.ToString().ToLowerInvariant()} · {Sanitize.Code(s.Reason)}" : "not checked yet", false)
            .AddField("Capture ring", service.Capture is not { } cap ? "off"
                : cap.Running ? $"recording · {AttackReport.Bytes(cap.RingBytes())} of {AttackReport.Bytes(cap.RingBudgetBytes)}"
                : $"not recording: {Sanitize.Code(cap.Problem ?? "starting")}", false);
        if (service.CurrentIncident is { } incident) embed.AddField("Incident", $"`{incident}` in progress", false);
        return embed;
    }

    private async Task TrafficAsync(ReplySink command, IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (Window(args, MaxExport) is not { } range) { await BadWindow(command).ConfigureAwait(false); return; }

        var records = new List<LogRecord>();
        await foreach (var r in service.Log.ReadAsync([RecordKind.Traffic], range.From, range.To, ct).ConfigureAwait(false)) records.Add(r);
        var filter = new QueryFilter(range.From, range.To);
        var points = LogQuery.Rates(records, filter);
        if (points.Count == 0)
        {
            await Reply(command, Theme.Notice("No traffic records in that window", RangeText(range))).ConfigureAwait(false);
            return;
        }

        var table = new StringBuilder("time (UTC)       pkt/s    peak   Mbit/s  udp/min  dropped\n");
        foreach (var group in Buckets(points, 20))
        {
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{group.First().Time.UtcDateTime:MM-dd HH:mm}  {group.Average(p => p.Pps),7:0} {group.Max(p => p.PeakPps),7} {group.Average(p => p.Mbps),7:0.0} {group.Average(p => p.UdpPackets),8:0} {group.Sum(p => p.Dropped),8}"));
        }

        var topRecords = new List<LogRecord>();
        await foreach (var r in service.Log.ReadAsync([RecordKind.Source], range.From, range.To, ct).ConfigureAwait(false)) topRecords.Add(r);
        var topSources = LogQuery.TopSources(topRecords, filter, 10);

        var embed = Theme.Notice($"Traffic · {RangeText(range)}")
            .WithDescription($"```\n{table}```")
            .AddField("Busiest sources", topSources.Count == 0 ? "none recorded" : Sources(topSources), false)
            .WithFooter("pkt/s and Mbit/s from the interface's own counters; udp and dropped from the firewall's - these cover what reached the server, never what the provider filtered");
        await Reply(command, embed).ConfigureAwait(false);
    }

    private async Task TopAsync(ReplySink command, IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (Window(args, MaxExport) is not { } range) { await BadWindow(command).ConfigureAwait(false); return; }
        var filter = new QueryFilter(range.From, range.To, Port: Int(args, "port"));

        var records = new List<LogRecord>();
        await foreach (var r in service.Log.ReadAsync([RecordKind.Source], range.From, range.To, ct).ConfigureAwait(false)) records.Add(r);
        var top = LogQuery.TopSources(records, filter, 25);

        await Reply(command, Theme.Notice($"Highest-volume sources · {RangeText(range)}")
            .WithDescription(top.Count == 0 ? "No source records in that window." : Sources(top))
            .WithFooter("From the per-minute source records: the 100 busiest sources each minute. `/antiddos history ip:` for one address")).ConfigureAwait(false);
    }

    private async Task HistoryAsync(ReplySink command, IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (Window(args, MaxHistory) is not { } range) { await BadWindow(command, MaxHistory).ConfigureAwait(false); return; }
        if (Network(args, "ip", out var network) is { } bad) { await Reply(command, bad).ConfigureAwait(false); return; }

        var filter = new QueryFilter(range.From, range.To, network, Int(args, "port"), Text(args, "protocol"), Text(args, "action"),
            Text(args, "category") is { } c ? CrowdSecClient.Clean(c) : null);
        var kinds = Kinds(Text(args, "kind"));
        var page = Int(args, "page") ?? 1;

        var found = new List<LogRecord>();
        var truncated = false;
        await foreach (var r in service.Log.ReadAsync(kinds, range.From, range.To, ct).ConfigureAwait(false))
        {
            if (!filter.Matches(r)) continue;
            if (found.Count >= MaxAttachedRecords) { truncated = true; break; }
            found.Add(r);
        }

        if (found.Count == 0)
        {
            await Reply(command, Theme.Notice("Nothing matched", $"{RangeText(range)}{Describe(filter)}\n" +
                "Packet records exist only for what the firewall logged (drops, suspicious sources, samples); sources and traffic are per minute.")).ConfigureAwait(false);
            return;
        }

        var pages = (found.Count + PageSize - 1) / PageSize;
        page = Math.Clamp(page, 1, pages);
        var lines = found.Skip((page - 1) * PageSize).Take(PageSize).Select(Row);
        var embed = Theme.Notice($"History · {found.Count}{(truncated ? "+" : "")} record(s) · page {page}/{pages}")
            .WithDescription($"{RangeText(range)}{Describe(filter)}\n```\n{string.Join('\n', lines)}\n```")
            .WithFooter((truncated ? $"Cut at {MaxAttachedRecords} - narrow it, or /antiddos export. " : "") + "Every match is attached as JSON Lines.");

        var body = Encoding.UTF8.GetBytes(string.Join('\n', found.Select(r => r.Line)) + "\n");
        command.Set(new AntiDdosReply(embed, $"antiddos-history-{AntiDdosLog.FileStamp(range.From)}.jsonl", body));
        await service.RecordAsync("query", "history", network?.Text, $"{RangeText(range)}{Describe(filter)}", command.Actor, ct).ConfigureAwait(false);
    }

    private EmbedBuilder Detections(int page)
    {
        var all = service.RecentDetections();
        if (all.Count == 0) return Theme.Notice("No detections since the bot started", "Older ones are in `/antiddos history kind:events category:detection`.");

        var pages = (all.Count + PageSize - 1) / PageSize;
        page = Math.Clamp(page, 1, pages);
        var b = new StringBuilder();
        foreach (var d in all.Skip((page - 1) * PageSize).Take(PageSize))
        {
            var outcome = d.Plan.Ban is { } ban ? d.BanProblem is null ? $"banned {AntiDdosEmbeds.Span(ban)}" : $"ban failed: {Clip(d.BanProblem, 80)}"
                : d.Plan.Held is { } held ? $"not banned: {Clip(held, 80)}" : "watching";
            b.AppendLine(CultureInfo.InvariantCulture,
                $"{AntiDdosEmbeds.Discord(d.At)} `{d.Plan.Source}` · {string.Join(", ", d.Plan.Detections.Select(x => x.Category))} · {Sanitize.Code(outcome)}");
        }
        return Theme.Notice($"Detections · page {page}/{pages}").WithDescription(b.ToString())
            .WithFooter($"An automatic ban needs {service.Options.EscalateAfter} detection minutes within {service.Options.CorrelationMinutes} min");
    }

    private async Task<EmbedBuilder> BansAsync(int page, CancellationToken ct)
    {
        var rows = new List<string>();
        if (await service.Firewall.SnapshotAsync(ct).ConfigureAwait(false) is { } snapshot)
        {
            rows.AddRange(snapshot.Blocked.OrderBy(b => b.Address, StringComparer.Ordinal).Select(b =>
                string.Create(CultureInfo.InvariantCulture, $"`{b.Address}` · firewall block · {(b.ExpiresSeconds is { } s ? $"{s / 60}m left" : "until it expires")}")));
        }
        foreach (var d in service.Decisions.OrderBy(d => d.Value, StringComparer.Ordinal))
        {
            rows.Add($"`{d.Value}` · CrowdSec {d.Type} · {Sanitize.Code(Clip(d.Scenario, 60))}{(d.Until is { } u ? $" · until {AntiDdosEmbeds.Discord(u)}" : "")}");
        }

        if (rows.Count == 0) return Theme.Success("Nothing is blocked", "No firewall blocks and no active CrowdSec decisions (community blocklists not counted).");
        var pages = (rows.Count + PageSize - 1) / PageSize;
        page = Math.Clamp(page, 1, pages);
        return Theme.Notice($"Blocked now · {rows.Count} · page {page}/{pages}")
            .WithDescription(string.Join('\n', rows.Skip((page - 1) * PageSize).Take(PageSize)))
            .WithFooter("Firewall blocks are the attack-time per-address limit and fallback bans; `/antiddos unban` lifts both kinds");
    }

    private async Task<EmbedBuilder> CrowdSecAsync(CancellationToken ct)
    {
        var cs = service.CrowdSec;
        if (!cs.Installed) return Theme.Warning("CrowdSec is not installed", "Install it with `scripts/antiddos/install.sh` - see ANTIDDOS.md.");

        var (decisions, problem) = await cs.DecisionsAsync(ct).ConfigureAwait(false);
        if (problem is not null) return Theme.Failure("CrowdSec is not answering", Sanitize.Code(Clip(problem, 900)));

        var alerts = await cs.AlertsAsync(TimeSpan.FromHours(24), null, ct).ConfigureAwait(false) ?? [];
        var bouncers = await cs.BouncersAsync(ct).ConfigureAwait(false) ?? [];
        var sources = await cs.AcquisitionAsync(ct).ConfigureAwait(false) ?? [];

        var scenarios = alerts.GroupBy(a => a.Scenario).OrderByDescending(g => g.Count()).Take(8)
            .Select(g => $"{Sanitize.Code(g.Key)}: {g.Count()}");
        return Theme.Notice("CrowdSec")
            .AddField("Decisions", string.Create(CultureInfo.InvariantCulture,
                $"{decisions!.Count} active · {decisions.Count(d => d.Origin == "crowdsec")} from scenarios, {decisions.Count(d => d.FromBot)} from this bot, {decisions.Count(d => d.Origin == "cscli" && !d.FromBot)} by hand"), false)
            .AddField("Alerts, last 24h", alerts.Count == 0 ? "none" : string.Join('\n', scenarios), false)
            .AddField("Bouncers", bouncers.Count == 0 ? "⚠️ none registered - decisions are not enforced"
                : string.Join('\n', bouncers.Select(b => $"{Sanitize.Code(b.Name)} · {(b.Revoked ? "revoked" : b.LastPull is { } t ? $"last pull {AntiDdosEmbeds.Discord(t)}" : "never pulled")}")), false)
            .AddField("Log sources", sources.Count == 0 ? "none reported"
                : string.Join('\n', sources.Select(s => string.Create(CultureInfo.InvariantCulture,
                    $"{(s.Failing ? "⚠️ " : "")}{Sanitize.Code(s.Name)}: {s.Reads:N0} read, {s.Parsed:N0} parsed"))), false)
            .WithFooter("CrowdSec reads logs (SSH, ufw). It does not see UDP floods - the firewall's own limits handle those.");
    }

    private async Task<EmbedBuilder> StorageAsync(CancellationToken ct)
    {
        var b = new StringBuilder("kind       files      size  oldest record (UTC)\n");
        long total = 0;
        foreach (var kind in Enum.GetValues<RecordKind>())
        {
            var (oldest, bytes, files) = await service.Log.SummaryAsync(kind, ct).ConfigureAwait(false);
            total += bytes;
            b.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{kind.ToString().ToLowerInvariant(),-9} {files,6} {AttackReport.Bytes(bytes),9}  {(oldest is { } o ? o.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-")}"));
        }

        var disk = DiskSpace.Read(service.Log.Paths.Root);
        var verdict = service.Storage;
        var rotation = File.Exists(LogrotateState) ? File.GetLastWriteTimeUtc(LogrotateState) : (DateTime?)null;
        return Theme.Notice("Anti-DDoS storage")
            .WithDescription($"`{Sanitize.Code(service.Log.Paths.Root)}`\n```\n{b}```")
            .AddField("Logs", $"{AttackReport.Bytes(total)} of {AttackReport.Bytes(service.Options.Storage.MaxLogBytes)}", true)
            .AddField("Capture ring", service.Capture is { } cap ? $"{AttackReport.Bytes(cap.RingBytes())} of {AttackReport.Bytes(cap.RingBudgetBytes)}" : "off", true)
            .AddField("Disk", disk is { } d ? $"{AttackReport.Bytes(d.Free)} free of {AttackReport.Bytes(d.Total)}" : "unknown", true)
            .AddField("Guard", verdict is null ? "not checked yet" : $"{verdict.Level.ToString().ToLowerInvariant()} - {Sanitize.Code(verdict.Reason)}", false)
            .AddField("Rotation", rotation is { } r
                ? $"logrotate last ran {AntiDdosEmbeds.Discord(new DateTimeOffset(r, TimeSpan.Zero))} (every 10 minutes by anti-ddos-logrotate.timer)"
                : "⚠️ no logrotate state - install the timer with scripts/antiddos/install.sh", false);
    }

    /// <summary>The state file the anti-DDoS logrotate timer keeps (see scripts/antiddos).</summary>
    internal const string LogrotateState = "/var/lib/logrotate/anti-ddos.status";

    private EmbedBuilder TrustedList()
    {
        var trusted = service.TrustedEntries();
        if (trusted.Count == 0) return Theme.Notice("Nothing is trusted", "`/antiddos allow add` adds an address or network (no wider than a /24 or /48).");
        return Theme.Notice($"Trusted · {trusted.Count}")
            .WithDescription(string.Join('\n', trusted.Take(50).Select(t =>
                $"`{t.Network}` · {Sanitize.Code(t.Comment)} · by {Sanitize.Code(t.AddedBy)} {AntiDdosEmbeds.Discord(t.AddedAt)}")))
            .WithFooter("Trusted addresses skip the bot's limits and are on CrowdSec's pavlovbot allowlist. ufw still applies to them.");
    }

    private EmbedBuilder ThresholdList()
    {
        var o = service.Options;
        var l = service.Limits;
        var values = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["per_ip_pps"] = l.PerSourcePps, ["unknown_pps"] = l.UnknownPps, ["block_minutes"] = l.BlockMinutes,
            ["suspect_udp_pps"] = o.SuspectUdpPps, ["suspect_syn_pps"] = o.SuspectSynPerSecond, ["ssh_new_per_minute"] = o.SshNewPerMinute,
            ["icmp_pps"] = o.IcmpEchoPps, ["syn_per_minute"] = o.SynPerMinute, ["port_spread"] = o.PortSpread,
            ["escalate_after"] = o.EscalateAfter, ["ban_minutes"] = o.BanMinutes, ["autoban"] = o.AutoBan ? 1 : 0,
        };
        var b = new StringBuilder();
        foreach (var t in Thresholds.All)
            b.AppendLine(CultureInfo.InvariantCulture, $"**{t.Name}** = {values[t.Name]:N0} ({t.Min:N0}-{t.Max:N0}) - {t.Description}");
        return Theme.Notice("Thresholds").WithDescription(b.ToString()).WithFooter("`/antiddos threshold set` changes one; it is kept across restarts");
    }

    // ---- changes ----

    private async Task ExportAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        if (Window(args, MaxExport) is not { } range) { await BadWindow(command, MaxExport).ConfigureAwait(false); return; }
        if (Network(args, "ip", out var network) is { } bad) { await Reply(command, bad).ConfigureAwait(false); return; }
        var filter = new QueryFilter(range.From, range.To, network);

        Directory.CreateDirectory(service.Log.Paths.Exports);
        var path = Path.Combine(service.Log.Paths.Exports, $"export-{AntiDdosLog.FileStamp(DateTimeOffset.UtcNow)}.jsonl.gz");
        var count = 0;
        var truncated = false;
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        await using (var gz = new GZipStream(file, CompressionLevel.SmallestSize))
        await using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
        {
            await foreach (var r in service.Log.ReadAsync(Kinds(Text(args, "kind")), range.From, range.To, ct).ConfigureAwait(false))
            {
                if (!filter.Matches(r)) continue;
                if (count >= MaxExportRecords) { truncated = true; break; }
                await writer.WriteAsync(r.Line.AsMemory(), ct).ConfigureAwait(false);
                await writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
                count++;
            }
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        PruneExports();

        var size = new FileInfo(path).Length;
        await audit.RecordAsync("antiddos-export", actor, network?.Text ?? "all", $"{count} records, {RangeText(range)}", ct).ConfigureAwait(false);
        await service.RecordAsync("export", "exported", network?.Text, $"{count} records, {RangeText(range)}, {path}", actor, ct).ConfigureAwait(false);

        var embed = Theme.Success($"Exported {count:N0} record(s){(truncated ? " (cut at the limit)" : "")}",
            $"{RangeText(range)}{Describe(filter)}\nOn the server: `{Sanitize.Code(path)}` ({AttackReport.Bytes(size)}, gzip JSON Lines)\n" +
            "Read it with `zcat FILE | jq .`; copy it off with `scp root@SERVER:FILE .`");
        if (size > MaxAttachmentBytes)
        {
            await Reply(command, embed.WithFooter("Too big for a Discord attachment - it is on the server")).ConfigureAwait(false);
            return;
        }

        command.Set(new AntiDdosReply(embed, Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)));
    }

    private void PruneExports()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(service.Log.Paths.Exports, "export-*.jsonl.gz")
                         .OrderByDescending(f => f, StringComparer.Ordinal).Skip(ExportsKept))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prune old exports");
        }
    }

    private async Task BlockAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        var bad = Network(args, "ip", out var network);
        if (bad is not null || network is null)
        {
            await Reply(command, bad ?? Theme.Failure("Give an address")).ConfigureAwait(false);
            return;
        }
        if (!network.NoWiderThan(IpNetworks.WidestBlocked4, IpNetworks.WidestBlocked6))
        {
            await Reply(command, Theme.Failure("That network is too wide",
                $"The widest block is a /{IpNetworks.WidestBlocked4} (IPv4) or /{IpNetworks.WidestBlocked6} (IPv6): a whole provider's range would take its innocent customers with it.")).ConfigureAwait(false);
            return;
        }
        if (Duration(Text(args, "duration"), out var duration) is { } badDuration)
        {
            await Reply(command, Theme.Failure("Not a duration", badDuration)).ConfigureAwait(false);
            return;
        }

        var reason = CrowdSecClient.Clean(Text(args, "reason") ?? "");
        var problem = await service.BanAsync(network, duration, reason, actor).ConfigureAwait(false);
        await audit.RecordAsync("antiddos-block", actor, network.Text, problem is null ? $"{AntiDdosEmbeds.Span(duration ?? TimeSpan.FromDays(3650))}: {reason}" : $"refused: {problem}", ct)
            .ConfigureAwait(false);
        await Reply(command, problem is null
            ? Theme.Success($"Banned {network.Text} {AntiDdosEmbeds.Span(duration ?? TimeSpan.FromDays(3650))}", Sanitize.Code(reason))
            : Theme.Failure("Not banned", Sanitize.Code(problem))).ConfigureAwait(false);
    }

    private async Task UnbanAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        var bad = Network(args, "ip", out var network);
        if (bad is not null || network is null)
        {
            await Reply(command, bad ?? Theme.Failure("Give an address")).ConfigureAwait(false);
            return;
        }

        var problem = await service.UnbanAsync(network, actor).ConfigureAwait(false);
        await audit.RecordAsync("antiddos-unban", actor, network.Text, problem, ct).ConfigureAwait(false);
        await Reply(command, problem is null
            ? Theme.Success($"Unbanned {network.Text}", "Every CrowdSec decision and firewall block on it is lifted. A community blocklist can list it again at its next update.")
            : Theme.Warning($"Partly unbanned {network.Text}", Sanitize.Code(problem))).ConfigureAwait(false);
    }

    private async Task LoggingAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        var mode = Text(args, "mode") switch { "elevated" => LogMode.Elevated, "incident" => LogMode.Incident, _ => (LogMode?)null };
        var minutes = Int(args, "minutes") ?? 30;
        var problem = await service.SetModeAsync(mode, TimeSpan.FromMinutes(minutes), actor).ConfigureAwait(false);
        await audit.RecordAsync("antiddos-logging", actor, mode?.ToString().ToLowerInvariant() ?? "normal",
            problem ?? (mode is null ? "now" : $"{minutes} min"), ct).ConfigureAwait(false);

        var rates = LogRates.For(service.Firewall.Mode);
        await Reply(command, problem is not null
            ? Theme.Failure("Logging not changed", Sanitize.Code(problem))
            : mode is null
                ? Theme.Success("Logging back to normal")
                : Theme.Success($"Logging {mode.Value.ToString().ToLowerInvariant()} for {minutes} min",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Loaded: {service.Firewall.Mode.ToString().ToLowerInvariant()} - at most {rates.DropsPerSecond} dropped and {rates.SuspectPerSecond} suspicious packet records/s, {rates.SamplesPerMinute} samples/min. It returns to normal by itself; run this again to extend it. Filtering is not affected by the mode."))).ConfigureAwait(false);
    }

    private async Task TrustAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        var bad = Network(args, "ip", out var network);
        if (bad is not null || network is null)
        {
            await Reply(command, bad ?? Theme.Failure("Give an address")).ConfigureAwait(false);
            return;
        }
        if (!network.NoWiderThan(IpNetworks.WidestTrusted4, IpNetworks.WidestTrusted6))
        {
            await Reply(command, Theme.Failure("That network is too wide to trust",
                $"At most a /{IpNetworks.WidestTrusted4} (IPv4) or /{IpNetworks.WidestTrusted6} (IPv6): anything wider is a hole in the firewall, not an allow list.")).ConfigureAwait(false);
            return;
        }

        var comment = CrowdSecClient.Clean(Text(args, "comment") ?? "");
        var problem = await service.TrustAsync(network, comment, actor).ConfigureAwait(false);
        await audit.RecordAsync("antiddos-allow", actor, network.Text, problem ?? comment, ct).ConfigureAwait(false);
        await Reply(command, problem is null
            ? Theme.Success($"Trusted {network.Text}", "It skips the bot's limits and is on CrowdSec's allowlist, which also lifted any ban on it. ufw still applies.")
            : Theme.Warning($"Trusting {network.Text}", Sanitize.Code(problem))).ConfigureAwait(false);
    }

    private async Task UntrustAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        var bad = Network(args, "ip", out var network);
        if (bad is not null || network is null)
        {
            await Reply(command, bad ?? Theme.Failure("Give an address")).ConfigureAwait(false);
            return;
        }

        var problem = await service.UntrustAsync(network, actor).ConfigureAwait(false);
        await audit.RecordAsync("antiddos-disallow", actor, network.Text, problem, ct).ConfigureAwait(false);
        await Reply(command, problem is null ? Theme.Success($"No longer trusted: {network.Text}") : Theme.Failure("Not changed", Sanitize.Code(problem)))
            .ConfigureAwait(false);
    }

    private async Task ThresholdAsync(ReplySink command, IReadOnlyDictionary<string, object> args, string actor, CancellationToken ct)
    {
        if (Thresholds.Find(Text(args, "name")) is not { } threshold || Int(args, "value") is not { } value)
        {
            await Reply(command, Theme.Failure("Unknown threshold", "`/antiddos threshold list` shows them all.")).ConfigureAwait(false);
            return;
        }

        var problem = await service.SetThresholdAsync(threshold, value, actor).ConfigureAwait(false);
        await audit.RecordAsync("antiddos-threshold", actor, threshold.Name, problem ?? value.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await Reply(command, problem is null
            ? Theme.Success($"{threshold.Name} = {value:N0}", $"{threshold.Description}. Checked by nft and applied; kept across restarts.")
            : Theme.Failure($"{threshold.Name} not changed", Sanitize.Code(problem))).ConfigureAwait(false);
    }

    // ---- parsing ----

    /// <summary>
    /// The time range a command asked for: <c>from</c>/<c>to</c> in UTC, or <c>minutes</c> back from
    /// now. Null when it is not a range or is longer than <paramref name="longest"/>.
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset To)? ParseWindow(string? from, string? to, long? minutes, DateTimeOffset now, TimeSpan longest)
    {
        DateTimeOffset? Parse(string? text) =>
            text is null ? null
            : DateTimeOffset.TryParse(text.Trim().Replace('T', ' ').TrimEnd('Z', 'z'), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTimeOffset.MinValue;

        var end = Parse(to) ?? now;
        var start = Parse(from) ?? end - TimeSpan.FromMinutes(minutes ?? 60);
        if (start == DateTimeOffset.MinValue || end == DateTimeOffset.MinValue || start >= end || end - start > longest) return null;
        return (start, end > now ? now : end);
    }

    /// <summary><c>30m</c>, <c>4h</c>, <c>7d</c> or <c>permanent</c> (null). The problem, or null when it parsed.</summary>
    internal static string? Duration(string? text, out TimeSpan? duration)
    {
        duration = null;
        var t = text?.Trim().ToLowerInvariant();
        if (t is "permanent" or "perm" or "forever") return null;
        if (t is null || t.Length < 2 || !int.TryParse(t[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            return "Use a number with m, h or d - `30m`, `4h`, `7d` - or `permanent`.";

        duration = t[^1] switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
        if (duration is null) return "Use m, h or d.";
        if (duration < TimeSpan.FromMinutes(5) || duration > TimeSpan.FromDays(365)) return "Between 5 minutes and 365 days, or `permanent`.";
        return null;
    }

    internal static IReadOnlyList<RecordKind> Kinds(string? kind) => kind switch
    {
        "packet" => [RecordKind.Packet],
        "source" => [RecordKind.Source],
        "traffic" => [RecordKind.Traffic],
        "event" => [RecordKind.Event],
        _ => [RecordKind.Event, RecordKind.Packet, RecordKind.Source],
    };

    /// <summary>One record as one line of a page.</summary>
    internal static string Row(LogRecord r)
    {
        var time = r.Time.UtcDateTime.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return r.Kind switch
        {
            RecordKind.Packet => string.Create(CultureInfo.InvariantCulture,
                $"{time} pkt {r.Action,-7} {r.Category,-18} {r.Source}:{r.SourcePort} > :{r.DestinationPort} {r.Protocol} len {r.Length}{(string.IsNullOrEmpty(r.Flags) ? "" : $" [{r.Flags}]")}"),
            RecordKind.Source => string.Create(CultureInfo.InvariantCulture,
                $"{time} src {r.Source} {r.Packets:N0} pkts {AttackReport.Bytes(r.Bytes)} ports {string.Join(",", r.Ports)}"),
            RecordKind.Traffic => string.Create(CultureInfo.InvariantCulture, $"{time} traffic {r.Packets:N0} pkts {AttackReport.Bytes(r.Bytes)}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{time} event {r.Category} {r.Action} {r.Source}"),
        };
    }

    private static string Sources(IReadOnlyList<SourceTotal> top) =>
        string.Join('\n', top.Select(t => string.Create(CultureInfo.InvariantCulture,
            $"`{t.Source}`{(t.Player ? " (player)" : "")} · {t.Packets:N0} pkts · {AttackReport.Bytes(t.Bytes)} · peak {t.PeakPps:0}/s{(t.Dropped > 0 ? $" · {t.Dropped:N0} dropped" : "")}")));

    private static IEnumerable<IReadOnlyList<LogQuery.RatePoint>> Buckets(IReadOnlyList<LogQuery.RatePoint> points, int buckets)
    {
        var size = Math.Max(1, (int)Math.Ceiling(points.Count / (double)buckets));
        for (var i = 0; i < points.Count; i += size) yield return points.Skip(i).Take(size).ToList();
    }

    private static (DateTimeOffset From, DateTimeOffset To)? Window(IReadOnlyDictionary<string, object> args, TimeSpan longest) =>
        ParseWindow(Text(args, "from"), Text(args, "to"), args.TryGetValue("minutes", out var m) && m is long l ? l : null, DateTimeOffset.UtcNow, longest);

    private static Task BadWindow(ReplySink command, TimeSpan? longest = null) =>
        Reply(command, Theme.Failure("Not a time range",
            $"Use `minutes`, or `from` (and `to`) in UTC like `2026-10-29 17:30`. At most {(longest ?? MaxExport).TotalDays:0} days, and `from` before `to`."));

    private static EmbedBuilder? Network(IReadOnlyDictionary<string, object> args, string name, out IpNetworks.Network? network)
    {
        network = null;
        if (Text(args, name) is not { } text) return null;
        network = IpNetworks.Canonical(text);
        return network is null
            ? Theme.Failure("Not an address", "Give an IPv4 or IPv6 address (`203.0.113.5`) or a network with its host bits zero (`203.0.113.0/24`).")
            : null;
    }

    private static string RangeText((DateTimeOffset From, DateTimeOffset To) r) =>
        $"{r.From.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} -> {r.To.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";

    private static string Describe(QueryFilter f)
    {
        var parts = new List<string>();
        if (f.Network is { } n) parts.Add($"ip {n.Text}");
        if (f.Port is { } p) parts.Add(string.Create(CultureInfo.InvariantCulture, $"port {p}"));
        if (f.Protocol is { } pr) parts.Add(pr);
        if (f.Action is { } a) parts.Add(a);
        if (f.Category is { } c) parts.Add($"category {Sanitize.Code(c)}");
        return parts.Count == 0 ? "" : " · " + string.Join(", ", parts);
    }

    private static string? Text(IReadOnlyDictionary<string, object> args, string name) =>
        args.TryGetValue(name, out var v) && v is string s && s.Trim().Length > 0 ? s.Trim() : null;

    private static int? Int(IReadOnlyDictionary<string, object> args, string name) =>
        args.TryGetValue(name, out var v) && v is long l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    private static string Clip(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    private static Task Reply(ReplySink sink, EmbedBuilder embed)
    {
        sink.Set(embed);
        return Task.CompletedTask;
    }
}

/// <summary>What an anti-DDoS action shows: an embed, and sometimes a file to attach.</summary>
public sealed record AntiDdosReply(EmbedBuilder Embed, string? FileName = null, byte[]? File = null)
{
    public static implicit operator AntiDdosReply(EmbedBuilder embed) => new(embed);
}

/// <summary>Where an action puts its reply, so it runs the same from a slash command or a panel.</summary>
internal sealed class ReplySink(string actor)
{
    public string Actor { get; } = actor;

    public AntiDdosReply? Result { get; private set; }

    public void Set(AntiDdosReply reply) => Result = reply;
}
