using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http;
using System.Reflection;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Economy;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Events;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Vpn;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Plugins;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Services;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host;

/// <summary>
/// The composition root, and the only place in the program that constructs anything.
/// </summary>
/// <remarks>
/// This is the same discipline the Node bot arrived at the hard way - index.js builds the
/// context and every other module is a function of it, so nothing imports the root and the
/// wiring is checkable in one place. .NET gives it a name and a container, but the rule is
/// unchanged: if a type reaches for a global, it cannot be tested and it cannot be replaced.
///
/// Startup order matters and is deliberate:
///   1. configuration, then VALIDATE AND EXIT if it is wrong - before anything opens a
///      socket, so a misconfigured bot fails in a second with a list of what to fix rather
///      than a null reference three subsystems deep
///   2. monitoring, so /health is answering while the rest is still coming up
///   3. background services
///   4. the Discord gateway LAST, so the first interaction cannot arrive before the things
///      it depends on exist
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        // .env first, real environment second: an environment variable must win, which is
        // both what dotenv does and what makes a container override work.
        builder.Configuration
            .AddDotEnvFile(Path.Combine(Directory.GetCurrentDirectory(), ".env"))
            .AddEnvironmentVariables();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
            options.UseUtcTimestamp = false;

            /* SCOPES ARE OFF BY DEFAULT. Without this every correlation id, guild, user and
               command name attached by OperationLog would be computed, carried through the
               call stack and never printed - correct, invisible, and indistinguishable from
               broken. */
            options.IncludeScopes = true;
        });
        builder.Logging.SetMinimumLevel(ParseLogLevel(builder.Configuration["LOG_LEVEL"]));

        /* LAYER 1 - BEFORE ANYTHING ELSE IS BUILT. The compiled-in owner identity is checked
           against its fingerprint, and a mismatch stops the process here rather than letting
           a tampered build come up and quietly run without its owner. Deliberately ahead of
           configuration validation: this is not a configuration problem and cannot be fixed
           by editing .env. */
        try
        {
            PavlovBot.Core.Security.OwnerGuard.Verify();
        }
        catch (PavlovBot.Core.Security.OwnerGuardException ex)
        {
            Console.Error.WriteLine($"OWNER GUARD FAILED: {ex.Message}");
            return 78;   // EX_CONFIG
        }

        var options = BotOptions.Bind(builder.Configuration);

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            // Every problem at once. Fixing an environment one crash at a time, with a
            // restart between each, turns a five-minute deploy into an hour.
            await Console.Error.WriteLineAsync("Configuration is not usable:").ConfigureAwait(false);
            foreach (var problem in problems)
                await Console.Error.WriteLineAsync($"  - {problem}").ConfigureAwait(false);
            return 78;   // EX_CONFIG
        }

        var features = FeatureOptions.Bind(builder.Configuration);

        /* THE FACTION SET, resolved before anything is registered because a bad one is a
           configuration error and belongs with the others - reported in full, then exit,
           rather than surfacing as an empty picker three subsystems later.

           UNSET IS THE BUILT-IN SET. The normal bots configure nothing and get exactly what
           they have today; only a themed clone points this at a file. */
        var factions = PavlovBot.Core.Factions.FactionRegistry.Default;
        if (features.FactionsPath is { } factionsPath)
        {
            var loaded = PavlovBot.Host.Factions.FactionsFile.Load(factionsPath);
            if (loaded.Set is null)
            {
                await Console.Error.WriteLineAsync($"FACTIONS_PATH is set but unusable ({factionsPath}):").ConfigureAwait(false);
                foreach (var problem in loaded.Problems)
                    await Console.Error.WriteLineAsync($"  - {problem}").ConfigureAwait(false);
                return 78;   // EX_CONFIG
            }
            factions = loaded.Set;

            /* A CAP THAT NO LONGER DOES ANYTHING IS SAID OUT LOUD. Rank caps were removed, and
               an old file is still a perfectly good file - but a setting that is read, kept and
               quietly ignored is how "why is the cap not working" becomes an afternoon. Written
               to stderr with the other configuration notes rather than the log, because this is
               about the file the operator is holding, not about the run. */
            if (loaded.IgnoredRankCaps > 0)
            {
                await Console.Error.WriteLineAsync(
                    $"{factionsPath} sets a cap on {loaded.IgnoredRankCaps} rank(s). Rank caps were " +
                    "removed - ranks and factions now take as many members as they are given, and " +
                    "these values do nothing. The file loads either way; delete them when convenient.")
                    .ConfigureAwait(false);
            }
        }

        builder.Services.AddSingleton(factions);

        /* PATHS THIS BOT MUST NOT WRITE. Every game-file write goes through this, so a second
           bot sharing one install can be handed the same .env and still be unable to clobber
           the first bot's ban list, whitelist or player balances. */
        builder.Services.AddSingleton(sp => new GameFileGuard(
            features.IgnoredPaths, sp.GetRequiredService<ILogger<GameFileGuard>>()));

        /* A CONTRADICTION, NOT A PREFERENCE. "Manage the rosters here" and "never write here"
           cannot both be honoured, and the shape it takes is the worst kind: the bot starts,
           reports `whitelists: <path>` as though the feature were on, and then refuses every
           single write. /whitelist add answers "the roster could not be written" forever.

           It is an easy configuration to arrive at by accident, because FactionRoles lives
           INSIDE ModSave - so ignoring the ModSave tree to protect the ban list and the
           ledgers takes the rosters with it. Refused here rather than discovered later. */
        if (features.RosterDirectory is { } rosterDirectory &&
            features.IgnoredPaths.Count > 0 &&
            GameFiles.InsideInstall(rosterDirectory, features.IgnoredPaths))
        {
            await Console.Error.WriteLineAsync(
                $"FACTION_ROLES_PATH ({rosterDirectory}) is inside IGNORE_PATHS, so this bot could " +
                "never write a roster - every /whitelist add would fail.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                "  - Ignore the specific files another bot owns (the ban list, the whitelist) rather " +
                "than a directory the rosters live under.").ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                "  - Or leave FACTION_ROLES_PATH unset if this bot is not meant to manage rosters.").ConfigureAwait(false);
            return 78;   // EX_CONFIG
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.Monitoring);
        builder.Services.AddSingleton(features);

        builder.Services.AddSingleton(new MetricsRegistry());
        builder.Services.AddSingleton<RecentErrors>();
        builder.Services.AddSingleton(new HealthRegistry());

        /* SQLite is the source of truth; the JSON files are a current, human-readable
           backup written by the export service. One file to back up, and WAL gives
           durability across a crash that a directory of JSON files never had. */
        builder.Services.AddSingleton<SqliteKeyValueBackend>(sp => new SqliteKeyValueBackend(
            Path.Combine(options.DataDirectory, "bot.db"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SqliteKeyValueBackend>()));
        builder.Services.AddSingleton<IKeyValueBackend>(sp => sp.GetRequiredService<SqliteKeyValueBackend>());
        builder.Services.AddSingleton<IJsonCodec, SystemTextJsonCodec>();
        builder.Services.AddSingleton<SerializedStore>();

        builder.Services.AddSingleton<RconRegistry>();
        builder.Services.AddSingleton(sp => new ServiceRegistry(
            sp.GetRequiredService<MetricsRegistry>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServiceRegistry>()));

        // ---- moderation ----
        builder.Services.AddSingleton(sp => new MasterNames(features.MasterNames, sp.GetRequiredService<SerializedStore>()));
        builder.Services.AddSingleton<IMasterNames>(sp => sp.GetRequiredService<MasterNames>());
        /* IpTrackingService is what knows which account a name belongs to and which flags a
           ban created. BanService needs both to lift a ban completely, and takes them through
           IBanEvidence rather than the whole class. */
        builder.Services.AddSingleton(sp => new BanService(
            sp.GetRequiredService<RconRegistry>(),
            sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<IMasterNames>(),
            sp.GetRequiredService<ILogger<BanService>>(),
            time: null,
            evidence: sp.GetRequiredService<IpTrackingService>(),
            /* LIFTING A BAN HAS TO REWRITE THE GAME'S BAN FILE. The server reads that file
               itself, so a player left listed in it stays banned however many Unban commands
               RCON accepts - and the importer would re-create the record on its next pass.
               Resolved lazily inside the lambda: ModsaveBanlist is registered just below. */
            banFile: sp.GetRequiredService<ModsaveBanlist>()));
        builder.Services.AddSingleton(sp => new ModsaveBanlist(
            features.ModsaveBanlistPath, sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<ILogger<ModsaveBanlist>>(),
            time: null,
            /* Resolved lazily INSIDE the lambda, so this does not depend on IpTrackingService
               being registered first - the import runs long after startup either way. */
            resolveName: id => sp.GetRequiredService<PavlovBot.Host.Logs.IpTrackingService>()
                .Account(id)?.Names.FirstOrDefault(),
            guard: sp.GetRequiredService<GameFileGuard>()));
        /* Pavlov's own whitelist, across every install. Resolved ONCE at startup and logged:
           a write that reaches two installs out of three is the failure worth seeing early,
           and it is invisible if nothing ever says how many were found. */
        var installs = PavlovInstalls.Discover(
            builder.Configuration["PAVLOV_BASES"], builder.Configuration["PAVLOV_BASE_1"]);
        builder.Services.AddSingleton<IpTrackingService>();
        builder.Services.AddSingleton(sp => new LogTailer(sp.GetRequiredService<ILoggerFactory>().CreateLogger<LogTailer>()));

        // ---- vpn screening ----
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(sp => new ResilientJsonClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("vpn"),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientJsonClient>()));
        builder.Services.AddSingleton(features.VpnKeys);
        builder.Services.AddSingleton<IVpnDetector, IpHubDetector>();
        builder.Services.AddSingleton<IVpnDetector, IpapiIsDetector>();
        builder.Services.AddSingleton<IVpnDetector, ProxyCheckDetector>();
        builder.Services.AddSingleton<IVpnDetector, SentinelDetector>();
        builder.Services.AddSingleton<IVpnDetector, IpqsDetector>();
        /* Geolocation is a CHAIN, in this order on purpose. ip-api is keyless and returns
           ISP and ASN, which the evasion scorer needs; Geoapify returns neither, so it is a
           standby for when ip-api is rate-limited or unreachable, not a replacement. With
           no GEOAPIFY_API_KEY the chain is just ip-api and behaves exactly as before. */
        builder.Services.AddSingleton<IGeoLocator>(sp =>
        {
            var providers = new List<IGeoLocator>
            {
                new IpApiGeoLocator(sp.GetRequiredService<ResilientJsonClient>()),
            };

            if (features.GeoapifyKey is { Length: > 0 } geoapifyKey)
                providers.Add(new GeoapifyGeoLocator(sp.GetRequiredService<ResilientJsonClient>(), geoapifyKey));

            return new GeoLocatorChain(providers, sp.GetRequiredService<ILogger<GeoLocatorChain>>());
        });

        /* Registered only when there is a key to render with, so /iplookup resolves a null
           StaticMap and omits the map rather than failing to construct. */
        if (features.GeoapifyKey is { Length: > 0 } mapKey)
        {
            builder.Services.AddSingleton(sp => new StaticMap(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("maps"),
                mapKey,
                sp.GetRequiredService<ILogger<StaticMap>>()));
        }
        builder.Services.AddSingleton(sp => new VpnScreeningService(
            sp.GetServices<IVpnDetector>(), sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<IGeoLocator>(), sp.GetRequiredService<MetricsRegistry>(),
            sp.GetRequiredService<ILogger<VpnScreeningService>>(),
            features.VpnThresholds, features.VpnCacheTtl));

        // ---- discord surfaces ----
        builder.Services.AddSingleton(sp => new Access(
            sp.GetRequiredService<SerializedStore>(), features.Owners, features.SuperOwners, factions));
        /* Singleton, and registered as BOTH: commands inject it to send paged output, and
           the gateway resolves it as a component handler to turn the pages. Two instances
           would mean the handler looking up a session the sender never stored. */
        builder.Services.AddSingleton<Paged>();
        builder.Services.AddSingleton<IComponentHandler>(sp => sp.GetRequiredService<Paged>());

        builder.Services.AddSingleton<MenuPanel>();
        builder.Services.AddSingleton<IComponentHandler>(sp => sp.GetRequiredService<MenuPanel>());

        builder.Services.AddSingleton<ArrestBooking>();
        builder.Services.AddSingleton<IComponentHandler>(sp => sp.GetRequiredService<ArrestBooking>());

        builder.Services.AddSingleton<PavlovBot.Host.Verification.VerificationService>();
        builder.Services.AddSingleton<IComponentHandler>(sp =>
            sp.GetRequiredService<PavlovBot.Host.Verification.VerificationService>());

        builder.Services.AddSingleton<FeedWebhooks>();
        builder.Services.AddSingleton<PavlovBot.Host.Logs.ServerLabels>();
        builder.Services.AddSingleton<FeedBridge>();
        builder.Services.AddSingleton<EvasionResponder>();
        /* Acts on a VPN verdict. Without it the screening ran on every connection, decided
           a ban, and nothing read the decision. */
        builder.Services.AddSingleton<VpnResponder>();
        builder.Services.AddSingleton(sp => new MoneyLog(
            features.LedgerDirectory, sp.GetRequiredService<FeedWebhooks>(), sp.GetRequiredService<ILogger<MoneyLog>>()));
        /* ---- timeline ----
           A REAL TABLE, in the same bot.db, because this is the one dataset the key-value
           document store cannot hold: it is append-heavy and its queries are all "this
           player, last six hours" rather than "give me everything". See SqliteEventStore.

           Registered before AuditLog because AuditLog records onto it. */
        builder.Services.AddSingleton<IEventStore>(sp => features.EventRetentionDays > 0
            ? new SqliteEventStore(
                Path.Combine(options.DataDirectory, "bot.db"),
                sp.GetRequiredService<ILogger<SqliteEventStore>>())
            : new NullEventStore());

        builder.Services.AddSingleton(sp => new EventRecorder(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<ILogger<EventRecorder>>()));

        builder.Services.AddSingleton(sp => new AuditLog(
            sp.GetRequiredService<SerializedStore>(),
            sp.GetService<FeedWebhooks>(),
            sp.GetRequiredService<EventRecorder>()));

        builder.Services.AddSingleton<PlayerEventBridge>();

        // ---- analytics: aggregates over the event table, computed in SQL ----
        builder.Services.AddSingleton<AnalyticsService>();
        builder.Services.AddSingleton<ISlashCommand, ServerStatsCommand>();
        builder.Services.AddSingleton<ISlashCommand, PluginsCommand>();
        builder.Services.AddSingleton<ISlashCommand, FactionStatsCommand>();
        builder.Services.AddSingleton<ISlashCommand, EconomyIntelCommand>();
        builder.Services.AddSingleton<ISlashCommand, StaffStatsCommand>();
        /* The roster is what decides whether a ledger may be written at all - see
           LedgerFileStore. Resolved lazily through the provider because RconRegistry is
           registered further down. */
        builder.Services.AddSingleton<IBalanceStore>(sp => new LedgerFileStore(
            features.LedgerDirectory, sp.GetRequiredService<PavlovBot.Host.Rcon.IOnlineRoster>(),
            sp.GetRequiredService<GameFileGuard>()));
        builder.Services.AddSingleton<Ledger>();
        builder.Services.AddSingleton<AutoPost>();
        builder.Services.AddSingleton(sp => new Boards(
            sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<RconRegistry>(),
            features.LedgerDirectory));

        /* THE CONFIGURED SET, NOT THE DEFAULT. Both of these take it as an optional
           parameter so tests and existing callers keep working, which means forgetting to
           pass it here would silently run a themed clone on the built-in roster. The startup
           summary prints the faction names for exactly that reason. */
        builder.Services.AddSingleton(sp => new RosterService(
            features.RosterDirectory, sp.GetRequiredService<ILogger<RosterService>>(),
            backupDirectory: null, factions: factions, guard: sp.GetRequiredService<GameFileGuard>()));

        builder.Services.AddSingleton<CommandCatalog>();
        builder.Services.AddSingleton<PlayerAutocomplete>();
        builder.Services.AddSingleton<ISlashCommand, ServerInfoCommand>();
        builder.Services.AddSingleton<ISlashCommand, HelpCommand>();
        builder.Services.AddSingleton<ISlashCommand, TempBanCommand>();
        builder.Services.AddSingleton<ISlashCommand, PermBanCommand>();
        builder.Services.AddSingleton<ISlashCommand, UnbanCommand>();
        builder.Services.AddSingleton<ISlashCommand, CheckBanCommand>();
        builder.Services.AddSingleton<ISlashCommand, KickCommand>();
        builder.Services.AddSingleton<ISlashCommand, AnnounceCommand>();
        builder.Services.AddSingleton<ISlashCommand, ErrorsCommand>();
        builder.Services.AddSingleton<ISlashCommand, WhitelistCommand>();
        builder.Services.AddSingleton<PavlovBot.Host.Factions.FactionMembers>();
        builder.Services.AddSingleton<IComponentHandler, WhitelistWipe>();

        builder.Services.AddSingleton<ISlashCommand>(sp => RankChangeCommand.Promotion(
            sp.GetRequiredService<RosterService>(),
            sp.GetRequiredService<PavlovBot.Host.Factions.FactionMembers>(),
            sp.GetRequiredService<Access>(),
            sp.GetRequiredService<ILogger<RankChangeCommand>>()));
        builder.Services.AddSingleton<ISlashCommand>(sp => RankChangeCommand.Demotion(
            sp.GetRequiredService<RosterService>(),
            sp.GetRequiredService<PavlovBot.Host.Factions.FactionMembers>(),
            sp.GetRequiredService<Access>(),
            sp.GetRequiredService<ILogger<RankChangeCommand>>()));
        builder.Services.AddSingleton<ISlashCommand, WarrantCommand>();
        builder.Services.AddSingleton<ISlashCommand, ArrestCommand>();
        builder.Services.AddSingleton<ISlashCommand, BackgroundCheckCommand>();
        builder.Services.AddSingleton<ISlashCommand, BailCommand>();
        builder.Services.AddSingleton<ISlashCommand, SetPinCommand>();
        builder.Services.AddSingleton<ISlashCommand, RemovePinCommand>();
        builder.Services.AddSingleton<ISlashCommand, IpLookupCommand>();
        builder.Services.AddSingleton<ISlashCommand, VpnCheckCommand>();
        builder.Services.AddSingleton<ISlashCommand, HealthCommand>();
        /* Restarting the game server is a PRIVILEGED system action, so the unit names come
           from configuration and the command selects among them by index - nothing typed in
           Discord reaches the command line. */
        builder.Services.AddSingleton(sp => new PavlovBot.Host.Servers.ServiceControl(
            features.PavlovUnits, features.SystemctlSudo,
            sp.GetRequiredService<ILogger<PavlovBot.Host.Servers.ServiceControl>>()));

        /* The SAME instance behind its narrow contracts, not a second one. ServiceControl
           holds the CPU sampling state that /stats reads, and RconRegistry holds the roster
           cache and every open connection - resolving either of these as a fresh object
           would quietly give the consumer an empty one. */
        builder.Services.AddSingleton<PavlovBot.Host.Servers.IUnitControl>(
            sp => sp.GetRequiredService<PavlovBot.Host.Servers.ServiceControl>());
        builder.Services.AddSingleton<PavlovBot.Host.Rcon.IOnlineRoster>(
            sp => sp.GetRequiredService<PavlovBot.Host.Rcon.RconRegistry>());
        builder.Services.AddSingleton<PavlovBot.Host.Servers.PlayerNotice>();
        builder.Services.AddSingleton<PavlovBot.Host.Servers.CrashRecovery>();

        // ---- warnings, payroll and money alerts ----
        builder.Services.AddSingleton<PavlovBot.Host.Moderation.WarningService>();
        builder.Services.AddSingleton<ISlashCommand, WarnCommand>();
        builder.Services.AddSingleton<ISlashCommand, AltsCommand>();

        /* ---- player intelligence ----
           An AGGREGATOR over the services above, owning no data of its own. Registered after
           them because it reads all of them; the optional ones are passed explicitly so a
           deployment with no rosters, ledger or VPN keys still gets a profile. */
        builder.Services.AddSingleton(sp => new PavlovBot.Host.Intelligence.PlayerIntelligenceService(
            sp.GetRequiredService<IpTrackingService>(),
            sp.GetRequiredService<BanService>(),
            sp.GetRequiredService<PavlovBot.Host.Moderation.WarningService>(),
            sp.GetRequiredService<RconRegistry>(),
            sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<ILogger<PavlovBot.Host.Intelligence.PlayerIntelligenceService>>(),
            sp.GetRequiredService<RosterService>(),
            sp.GetRequiredService<PavlovBot.Host.Factions.FactionMembers>(),
            sp.GetService<PavlovBot.Core.Economy.IBalanceStore>(),
            sp.GetRequiredService<PavlovBot.Host.Economy.Payroll>(),
            sp.GetService<PavlovBot.Host.Vpn.VpnScreeningService>()));

        builder.Services.AddSingleton<ISlashCommand, PlayerProfileCommand>();

        builder.Services.AddSingleton(sp => new PavlovBot.Host.Economy.Payroll(
            sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<Ledger>(),
            sp.GetRequiredService<RosterService>(),
            sp.GetRequiredService<PavlovBot.Host.Rcon.IOnlineRoster>(),
            sp.GetRequiredService<ILogger<PavlovBot.Host.Economy.Payroll>>(),
            features.PayrollAmount, features.PayrollInterval, features.PayrollFaction));

        builder.Services.AddSingleton<ISlashCommand, WagesCommand>();

        builder.Services.AddSingleton(sp => new PavlovBot.Host.Economy.MoneyAnomalyDetector(
            sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<ILogger<PavlovBot.Host.Economy.MoneyAnomalyDetector>>(),
            features.MoneyAlertThreshold, features.MoneyAlertWindow));

        builder.Services.AddSingleton<ISlashCommand, RotateMapCommand>();
        builder.Services.AddSingleton<ISlashCommand, ServerSwitchCommand>();
        builder.Services.AddSingleton<ISlashCommand, GiveMenuCommand>();
        builder.Services.AddSingleton<ISlashCommand, UnlinkNameCommand>();
        builder.Services.AddSingleton<ISlashCommand, SetRolesCommand>();
        builder.Services.AddSingleton<ISlashCommand, DonatorCommand>();
        builder.Services.AddSingleton<ISlashCommand, SuspendRankCommand>();
        builder.Services.AddSingleton<ISlashCommand, SubclassCommand>();
        builder.Services.AddSingleton<ISlashCommand, StripMenuCommand>();
        builder.Services.AddSingleton<ISlashCommand, BanListCommand>();
        builder.Services.AddSingleton<ISlashCommand, FlushCommand>();
        builder.Services.AddSingleton<ISlashCommand, StaffActivityCommand>();
        builder.Services.AddSingleton<ISlashCommand, StaffLeaderboardCommand>();
        builder.Services.AddSingleton<ISlashCommand, StatsCommand>();
        builder.Services.AddSingleton<ISlashCommand, ManualCommand>();
        builder.Services.AddSingleton<ISlashCommand, FirewallCommand>();
        /* Standing up a WHOLE NEW server - SteamCMD, a systemd unit, ufw, then a rewrite of the
           bot's own .env and a restart. A far larger privileged surface than restarting a
           configured unit, so it lives behind its own provisioner rather than on ServiceControl,
           and it refuses unless the bot is root. */
        builder.Services.AddSingleton<PavlovBot.Host.Servers.IServerProvisioner>(sp =>
            new PavlovBot.Host.Servers.ServerProvisioner(
                sp.GetRequiredService<ILogger<PavlovBot.Host.Servers.ServerProvisioner>>()));
        builder.Services.AddSingleton<ISlashCommand, ProvisionServerCommand>();
        builder.Services.AddSingleton<ISlashCommand, DeleteServerCommand>();
        builder.Services.AddSingleton<ISlashCommand, GuildInvitesCommand>();
        /* Takes the discovered installs positionally, like the units - server N's Game.ini is
           installs[N-1] - so the file it edits and the unit it restarts are the same server. */
        builder.Services.AddSingleton<ISlashCommand>(sp => new TestModeCommand(
            sp.GetRequiredService<PavlovBot.Host.Servers.ServiceControl>(),
            sp.GetRequiredService<PavlovBot.Host.Servers.PlayerNotice>(),
            sp.GetRequiredService<WhitelistFile>(),
            sp.GetRequiredService<GameFileGuard>(),
            sp.GetRequiredService<Access>(),
            sp.GetRequiredService<AuditLog>(),
            installs,
            sp.GetRequiredService<ILogger<TestModeCommand>>()));
        /* The owner control panel is both: a slash command that posts the menu, and the
           component handler for the menu and its modals. One instance, registered twice. */
        builder.Services.AddSingleton<ConfigPanel>();
        builder.Services.AddSingleton<ISlashCommand>(sp => sp.GetRequiredService<ConfigPanel>());

        /* The public server browser. Its own HttpClient, because the master server list is
           ~90KB and has nothing to do with the VPN detectors' timeouts or rate limits. */
        builder.Services.AddSingleton(sp => new PavlovBot.Host.Servers.MasterServerList(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("pavlov-ms"),
            sp.GetRequiredService<ILogger<PavlovBot.Host.Servers.MasterServerList>>(),
            features.PavlovVersion));
        builder.Services.AddSingleton<ServerBrowser>();
        builder.Services.AddSingleton<IComponentHandler>(sp => sp.GetRequiredService<ServerBrowser>());
        builder.Services.AddSingleton<ISlashCommand>(sp =>
            new ServerLookupCommand(sp.GetRequiredService<ServerBrowser>()));
        builder.Services.AddSingleton<ISlashCommand, FeedsCommand>();

        // Live player counts as voice channel names, replacing the player-list board.
        builder.Services.AddSingleton<IChannelRenamer, GatewayChannelRenamer>();
        builder.Services.AddSingleton(sp => new PlayerCountChannels(
            sp.GetRequiredService<IChannelRenamer>(),
            sp.GetRequiredService<PavlovBot.Host.Rcon.RconRegistry>(),
            sp.GetRequiredService<PavlovBot.Host.Servers.MasterServerList>(),
            sp.GetRequiredService<PavlovBot.Host.Servers.ServiceControl>(),
            sp.GetRequiredService<ILogger<PlayerCountChannels>>(),
            features.DefaultServerCapacity));
        builder.Services.AddSingleton<ISlashCommand, CountsCommand>();
        // ...and the names half of it, on demand rather than posted.
        builder.Services.AddSingleton<ISlashCommand>(sp =>
            new PlayersCommand(sp.GetRequiredService<RconRegistry>(), sp.GetRequiredService<Paged>()));

        builder.Services.AddSingleton<WhitelistFile>();
        builder.Services.AddSingleton<ISlashCommand>(sp => new TesterCommand(
            sp.GetRequiredService<WhitelistFile>(),
            sp.GetRequiredService<Access>(),
            sp.GetRequiredService<AuditLog>(),
            installs,
            sp.GetRequiredService<ILogger<TesterCommand>>()));
        builder.Services.AddSingleton<IComponentHandler>(sp => sp.GetRequiredService<ConfigPanel>());
        builder.Services.AddSingleton(sp => new OwnerActions(
            sp.GetRequiredService<SerializedStore>(),
            sp.GetRequiredService<IpTrackingService>(),
            features.LedgerDirectory));
        builder.Services.AddSingleton<ISlashCommand, InspectCommand>();
        builder.Services.AddSingleton<ISlashCommand, SetRconRolesCommand>();
        builder.Services.AddSingleton<ISlashCommand>(sp => CapsCommand.Give(
            sp.GetRequiredService<Ledger>(), sp.GetRequiredService<AuditLog>(),
            sp.GetRequiredService<Access>(), sp.GetRequiredService<ILogger<CapsCommand>>(),
            sp.GetRequiredService<IBalanceStore>()));
        builder.Services.AddSingleton<ISlashCommand>(sp => CapsCommand.Adjust(
            sp.GetRequiredService<Ledger>(), sp.GetRequiredService<AuditLog>(),
            sp.GetRequiredService<Access>(), sp.GetRequiredService<ILogger<CapsCommand>>(),
            sp.GetRequiredService<IBalanceStore>()));
        builder.Services.AddSingleton<DiscordGateway>();
        builder.Services.AddSingleton<IAutoPostTarget>(sp => new GatewayAutoPostTarget(sp.GetRequiredService<DiscordGateway>(),
            sp.GetRequiredService<ILogger<GatewayAutoPostTarget>>()));

        /* Resolves the gateway ON USE rather than taking it here, so a command depending on this
           does not close the DiscordGateway -> every ISlashCommand -> command -> gateway cycle. */
        builder.Services.AddSingleton<IGuildDirectory, GatewayGuildDirectory>();

        // Hosted services start in registration order and stop in reverse, which is exactly
        // the order this needs: monitoring up first and down last, gateway up last so no
        // interaction can arrive before its dependencies exist.
        builder.Services.AddSingleton(sp => new MonitoringServer(
            sp.GetRequiredService<MonitoringOptions>(),
            sp.GetRequiredService<MetricsRegistry>(),
            sp.GetRequiredService<HealthRegistry>(),
            sp.GetRequiredService<ILogger<MonitoringServer>>(),
            readiness: () => sp.GetRequiredService<DiscordGateway>().IsReady));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitoringServer>());
        builder.Services.AddSingleton<PluginHost>();
        builder.Services.AddHostedService<PluginHostedService>();
        builder.Services.AddHostedService<BackgroundServiceHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordGateway>());

        var host = builder.Build();

        RegisterProcessHealth(host.Services.GetRequiredService<HealthRegistry>(), options);

        var logger = host.Services.GetRequiredService<ILogger<IHost>>();

        /* WHICH BUILD IS ACTUALLY RUNNING. Without this, a deploy that published a new
           binary but left the old process alive - which is what `pm2 start` does to an app
           that is already running - is indistinguishable from a fix that did not work. The
           deploy script reads this line back to confirm the restart took. */
        logger.LogInformation("build {Build}", BuildStamp());

        logger.LogInformation("Starting with {Servers} RCON server(s), data in {DataDirectory}",
            options.Servers.Count, options.DataDirectory);

        /* Say which features are ON. A feature that is off for want of one environment
           variable is otherwise indistinguishable from one that is broken, and the two get
           debugged very differently. */
        foreach (var line in features.Describe()) logger.LogInformation("  {Feature}", line);

        /* THE FACTION SET IS NAMED AT EVERY START. Two bots now run this binary against one
           game server, and the failure that costs a roster is the clone quietly running the
           built-in factions and writing the other bot's files. Printing the names is the
           check that takes one glance; nothing else would say which set is loaded. */
        logger.LogInformation("  factions: {Count} from {Source} - {Names}",
            factions.Names.Count, FactionSource(features.FactionsPath),
            string.Join(", ", factions.Names));
        logger.LogInformation("  whitelist bot: {State}", options.FactionBotEnabled
            ? $"on (application {options.FactionClientId}, owns /whitelist /promotion /demotion /subclass)"
            : "off (FACTION_BOT_TOKEN / FACTION_CLIENT_ID not set - those commands stay on the main bot)");

        /* THE ROSTER FILES THE LOADED FACTIONS EXPECT, created empty if the install does not have
           them yet. Nothing existing is written to, so a roster with members in it is untouched -
           this only means the whole set is present and visible from the first start rather than
           appearing one file at a time as each faction gains its first member. It still never
           creates the DIRECTORY: a missing one means the path is wrong. */
        var rosters = host.Services.GetRequiredService<RosterService>().EnsureRosterFiles();

        /* SAID EVERY START, INCLUDING WHEN IT DID NOTHING. "Zero created" is the answer whether
           the feature is off, every file was already there, or the directory refused every
           write, and an operator staring at an empty roster directory needs to be told which
           of the three happened - silence sent one of those debugging sessions the long way
           round. */
        logger.LogInformation("  faction rosters: {Summary}", rosters.Summary);
        foreach (var failure in rosters.Failed)
            logger.LogWarning("  faction roster NOT created - {Failure}", failure);

        var seeded = 0;
        var backend = host.Services.GetRequiredService<SqliteKeyValueBackend>();
        foreach (var (name, seed) in Datasets.Seeds)
        {
            // Migrate a legacy JSON file first, so an existing install keeps its data;
            // seed only what is still missing after that.
            backend.ImportJsonFile(name, Path.Combine(options.DataDirectory, $"{name}.json"));
            if (backend.SeedIfMissing(name, seed)) seeded++;
        }
        if (seeded > 0) logger.LogInformation("Seeded {Count} empty dataset(s)", seeded);

        /* Said at startup because it is DETECTED, not configured - so the one thing an
           operator cannot look up in .env is how it resolved. A bot that is not root and has
           no sudoers entry will refuse every /serverswitch, and this is where that is
           visible before somebody tries it. */
        var systemd = host.Services.GetRequiredService<PavlovBot.Host.Servers.ServiceControl>();

        /* Set here rather than injected, because the two need each other: ServiceControl
           warns players over RCON before stopping a server, and the registry asks systemd
           whether a silent server was stopped on purpose. See RconRegistry.UseLifecycle. */
        host.Services.GetRequiredService<PavlovBot.Host.Rcon.RconRegistry>().UseLifecycle(systemd);

        /* STAFF LOG CHANNELS, attached here for the same reason as the lifecycle above:
           posting to a channel needs the gateway, the gateway is built from every command,
           and nearly every command needs AuditLog. A constructor dependency closes that loop
           and the container cannot resolve it - so the sink is attached once everything
           exists. Off entirely when neither channel is set. */
        var staffChannels = new PavlovBot.Host.Discord.StaffLogChannels(
            features.ModLogChannel, features.BanLogChannel,
            features.PoliceLogChannel, features.ArrestChannel);

        /* ANY of them, not just the first two. Setting only POLICE_LOG_CHANNEL used to leave
           the sink unattached entirely, so the one channel the operator did configure got
           nothing. */
        if (staffChannels.Any)
        {
            var staffLog = new PavlovBot.Host.Discord.ChannelStaffLog(
                host.Services.GetRequiredService<PavlovBot.Host.Discord.IAutoPostTarget>(),
                staffChannels,
                host.Services.GetRequiredService<ILogger<PavlovBot.Host.Discord.ChannelStaffLog>>());

            host.Services.GetRequiredService<PavlovBot.Host.Moderation.AuditLog>().UseSink(staffLog);

            static string Show(ulong? id) =>
                id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unset";

            logger.LogInformation(
                "Staff log channels: mod {Mod}, ban {Ban}, police {Police}, arrest {Arrest}",
                Show(features.ModLogChannel), Show(features.BanLogChannel),
                Show(features.PoliceLogChannel), Show(features.ArrestChannel));
        }
        logger.LogInformation("systemctl runs {Elevation} | units: {Units}",
            systemd.Elevation, string.Join(", ", systemd.Units));

        /* Said at startup so an operator learns BEFORE running /provisionserver that a non-root
           bot will refuse it - provisioning writes /etc/systemd/system, enables a service and
           runs SteamCMD as steam, none of which fit the narrow sudoers line systemctl uses. */
        logger.LogInformation("/provisionserver is {State}",
            Environment.IsPrivilegedProcess
                ? "available (the bot is root)"
                : "unavailable until the bot runs as root");

        var feeds = host.Services.GetRequiredService<FeedWebhooks>();
        feeds.Register(FeedWebhooks.Join, features.JoinWebhook);
        feeds.Register(FeedWebhooks.Connect, features.ConnectWebhook);
        feeds.Register(FeedWebhooks.Kill, features.KillWebhook);
        feeds.Register(FeedWebhooks.Money, features.MoneyWebhook);
        feeds.Register(FeedWebhooks.Staff, features.StaffWebhook);

        /* Said out loud at startup, at INFORMATION. A feed with no URL is a choice and a
           feed with a dead URL is a fault, and for a long time they looked identical from
           outside - the status was tracked from the first day and nothing ever printed it.
           `/feeds` shows the same thing on demand. */
        logger.LogInformation("Pavlov installs: {Installs}",
            string.Join(", ", installs.Select(Path.GetFileName)));

        /* Every path the bot WRITES INTO A GAME INSTALL, resolved and checked. These were
           only ever visible by their effects, and the effect of a wrong one used to be a
           directory tree created beside the real one that the game never reads. */
        foreach (var (label, path) in new[]
        {
            ("ModSave ban list", features.ModsaveBanlistPath),
            ("economy ledger", features.LedgerDirectory is { } d ? Path.Combine(d, "<player>.txt") : null),
            ("faction rosters", features.RosterDirectory is { } r ? Path.Combine(r, "<roster>.txt") : null),
        }.Concat(installs.Select(i => ("whitelist", (string?)PavlovInstalls.WhitelistPath(i)))))
        {
            if (path is null) continue;

            logger.LogInformation("Game file - {Label}: {Path}{Problem}", label, path,
                GameFiles.Problem(path) is { } problem ? $"  <-- {problem}" : "");
        }

        logger.LogInformation("Feeds: {Status}",
            string.Join(" | ", feeds.Status.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}")));

        if (host.Services.GetRequiredService<VpnScreeningService>().ConfigurationWarning() is { } warning)
            logger.LogWarning("{Warning}", warning);

        /* The ignore list is persisted but the tracker's copy is in memory, so it has to be
           loaded before the first log line is read. Without this the list survives a restart
           in storage while silently doing nothing - the bot resumes recording addresses for
           exactly the accounts somebody asked it not to. */
        host.Services.GetRequiredService<OwnerActions>().RestoreIgnoreList();

        /* RESOLVED, not just registered. The bridge subscribes to the tracker's events in
           its constructor, and a service nobody asks for is never constructed - which is
           exactly how the join, connect and kill feeds came to be silent. */
        host.Services.GetRequiredService<FeedBridge>();

        /* Same reason, and this one matters more: the responder subscribes to the tracker's
           Flagged event in its constructor. Registered but never resolved, ban-evasion
           detection would keep logging catches and taking no action. */
        host.Services.GetRequiredService<EvasionResponder>();

        /* The OTHER way a leaderboard appears twice, and the one a code fix cannot reach.
           Each board keeps its own message, so pointing two of them at one channel puts two
           permanent posts there - which looks exactly like the duplicate-post bug and is not
           it. Cheap to say once at startup; expensive to work out from the channel. */
        if (features.LeaderboardChannel is { } cashChannel && cashChannel == features.ArrestBoardChannel)
            logger.LogWarning("LEADERBOARD_CHANNEL and ARREST_LEADERBOARD_CHANNEL are both {Channel}. " +
                              "Each board keeps its own message, so that channel will hold TWO posts - " +
                              "the cash board and the arrest board. Give them separate channels if that " +
                              "is not what you meant.", cashChannel);

        if (features.MasterNames.Count == 0)
            logger.LogWarning("MASTER_NAMES is not set - only the built-in master account \"{Master}\" is " +
                              "protected from an auto-ban. Any OTHER account of yours could be caught by a " +
                              "false positive.", PavlovBot.Core.Security.OwnerGuard.MasterName);

        /* One owner identity IS compiled in - see OwnerGuard - because a cutover that never
           copied OWNER_IDS into .env leaves nobody at owner tier and every owner-gated
           command refusing everyone. Role-derived tiers still work (they live in bot.db), so
           it is not a full lockout, which is exactly why it would otherwise go unnoticed for
           days. The warning stays because ONE account is not a configuration. */
        if (features.Owners.Count == 0 && features.SuperOwners.Count == 0)
            logger.LogWarning("OWNER_IDS and SUPER_OWNER_IDS are both unset - only the built-in super owner " +
                              "{Owner} holds owner tier. Every other account is limited to whatever its " +
                              "Discord roles grant.", PavlovBot.Core.Security.OwnerGuard.SuperOwnerId);

        /* --selftest builds the entire object graph, reports what it cost, and exits without
           connecting to anything. It is a deploy smoke test - it proves the configuration
           parses and every dependency resolves, which are the two things that fail at 2am -
           and it is the only honest way to measure this process against the Node bot, since
           neither side is connected to Discord when measured this way. */
        if (args.Contains("--selftest", StringComparer.Ordinal))
        {
            SelfTest(host, options, started);
            return 0;
        }

        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void SelfTest(IHost host, BotOptions options, long started)
    {
        // Touching the gateway is the point: constructing DiscordSocketClient is where the
        // memory goes, and a graph that resolves everything EXCEPT the expensive part would
        // be a flattering measurement rather than a useful one.
        var gateway = host.Services.GetRequiredService<DiscordGateway>();
        _ = host.Services.GetRequiredService<RconRegistry>();
        _ = host.Services.GetRequiredService<SerializedStore>();
        _ = host.Services.GetRequiredService<MonitoringServer>();
        /* FROM THE GATEWAY, not from the container. The container's collection is every
           command that EXISTS; the gateway's is every command that will be REGISTERED, and
           COMMANDS_DISABLED is the difference. Reading the wrong one made this gate list
           /arrest on a bot that would never register it. */
        var registered = gateway.CommandNames.Order(StringComparer.Ordinal).ToList();
        var available = host.Services.GetServices<ISlashCommand>().Count();

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        Console.WriteLine($"selftest: graph built in {elapsed.TotalMilliseconds:0}ms");
        Console.WriteLine($"selftest: {options.Servers.Count} RCON server(s), {registered.Count} command(s): {string.Join(", ", registered.Select(c => "/" + c))}");

        if (available > registered.Count)
        {
            /* The names ACTUALLY removed, not the configured list - printing the latter said
               "5 disabled" above six names whenever one of them was a typo, which undercuts
               the warning immediately below it. */
            var removed = options.DisabledCommands
                .Except(gateway.UnknownDisabledCommands, StringComparer.OrdinalIgnoreCase);

            Console.WriteLine(
                $"selftest: {available - registered.Count} command(s) disabled by COMMANDS_DISABLED: " +
                string.Join(", ", removed.Select(c => "/" + c)));
        }

        foreach (var unknown in gateway.UnknownDisabledCommands)
            Console.WriteLine($"selftest: WARNING - COMMANDS_DISABLED names \"{unknown}\", which is not a command here");
        Console.WriteLine($"selftest: resident {process.WorkingSet64 / 1024.0 / 1024:0.0} MB, managed heap {GC.GetTotalMemory(false) / 1024.0 / 1024:0.0} MB");
        Console.WriteLine($"selftest: gateway ready={gateway.IsReady} (not connected - this is construction only)");
    }

    /// <summary>Checks that describe the process itself rather than any one subsystem.</summary>
    private static void RegisterProcessHealth(HealthRegistry health, BotOptions options)
    {
        health.Register("filesystem", _ =>
        {
            /* Actually WRITE something. "The directory exists" is not the question - a
               read-only mount, a full disk and a permissions change all pass that check and
               all lose every write the bot makes. */
            try
            {
                Directory.CreateDirectory(options.DataDirectory);
                var probe = Path.Combine(options.DataDirectory, ".health-probe");
                File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                File.Delete(probe);
                return Task.FromResult(HealthResult.Healthy(options.DataDirectory));
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthResult.Unhealthy($"{options.DataDirectory} is not writable: {ex.Message}"));
            }
        });

        health.Register("timezone", _ =>
        {
            /* Every timestamp the bot prints is Eastern. If the tzdata is missing - the
               usual cause is InvariantGlobalization slipping back on, or a distroless base
               image - every ban expiry the bot displays is silently wrong by up to five
               hours, and nothing else notices. */
            try
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                return Task.FromResult(HealthResult.Healthy(
                    TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, eastern).ToString("yyyy-MM-dd HH:mm zzz",
                        System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (TimeZoneNotFoundException ex)
            {
                return Task.FromResult(HealthResult.Unhealthy($"America/New_York unavailable: {ex.Message}"));
            }
        });
    }

    /// <summary>
    /// The commit this binary was built from, plus when.
    /// </summary>
    /// <remarks>
    /// Falls back to the assembly's write time when no revision was stamped in, so a local
    /// build still says something that changes between builds. An unchanging stamp is the
    /// signal that the process was never replaced.
    /// </remarks>
    /// <summary>
    /// Where the factions came from, RESOLVED, so "the file I edited" and "the file the bot
    /// read" can be compared.
    /// </summary>
    /// <remarks>
    /// THE CONFIGURED STRING WAS NOT ENOUGH. A relative FACTIONS_PATH resolves against the
    /// process's working directory, which under pm2 is not necessarily where somebody was
    /// standing when they edited a file of that name - so a bot reading a DIFFERENT copy
    /// printed exactly the same line as one reading theirs, and an edit that had plainly been
    /// made looked like it had been ignored.
    ///
    /// The last-write time answers the other half of the same question: a file whose edit
    /// predates this process was edited and never deployed. Between the two, "the factions I
    /// removed are still there" stops being a guess.
    ///
    /// The names on this line are also exactly what the slash commands' faction CHOICES are
    /// built from, so a set here that disagrees with what Discord shows is Discord's cache,
    /// not this file.
    /// </remarks>
    internal static string FactionSource(string? path)
    {
        if (path is null) return "built in (FACTIONS_PATH not set)";

        try
        {
            var full = Path.GetFullPath(path);

            // Startup has already refused a path that does not resolve, so this is belt and
            // braces - but a missing file must not turn the summary into an exception.
            return File.Exists(full)
                ? $"{full} (last edited {File.GetLastWriteTimeUtc(full):yyyy-MM-dd HH:mm}Z)"
                : full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static string BuildStamp()
    {
        var informational = System.Reflection.Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        // "1.0.0+<sha>" when the SDK stamped a SourceRevisionId; take the part after '+'.
        if (informational is { Length: > 0 } && informational.Split('+') is { Length: > 1 } parts)
            return parts[^1];

        var location = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var built = location is { Length: > 0 } && File.Exists(location)
            ? File.GetLastWriteTimeUtc(location)
            : DateTime.UtcNow;

        return $"local-{built:yyyyMMddHHmmss}";
    }

    private static LogLevel ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };
}
