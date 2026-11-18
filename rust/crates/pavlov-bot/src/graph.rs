//! The object graph (C# `Program.Main`'s container registrations and the wiring after
//! `builder.Build()`), built by hand in the same order.
//!
//! BUILT WITHOUT DISCORD. Everything that talks to Discord comes in as a [`Surfaces`] value:
//! the real client's REST adapters at run time, inert ones for `--selftest`. Building the
//! serenity client contacts Discord, and the selftest must not; a graph that needed the
//! client to exist could not be checked before a deploy.
//!
//! NOTHING HERE STARTS WORK. Constructors may spawn a queue's writer task (the timeline, the
//! feeds), which idles until fed; the background services, the monitoring endpoint and the
//! gateway are started by `run`.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_config::{BotOptions, Config, FeatureOptions};
use pavlov_core::data::{KeyValueBackend, SerializedStore};
use pavlov_core::economy::BalanceStore;
use pavlov_core::embed::Embed;
use pavlov_core::factions::FactionSet;
use pavlov_core::time::{Clock, SystemClock};
use pavlov_discord::access::Access;
use pavlov_discord::autocomplete::PlayerAutocomplete;
use pavlov_discord::boards::Boards;
use pavlov_discord::catalog::{CommandCatalog, CommandSummary};
use pavlov_discord::command::CommandSpec;
use pavlov_discord::command_log::CommandLog;
use pavlov_discord::commands;
use pavlov_discord::component::ActionRow;
use pavlov_discord::component_router::ComponentRouter;
use pavlov_discord::dispatcher::InteractionDispatcher;
use pavlov_discord::guild_directory::{GuildDirectory, GuildInvite};
use pavlov_discord::home_guild::{HomeGuildMembers, MemberSource};
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::monitor_alerts::{AlertPoster, ChannelMonitorAlerts};
use pavlov_discord::paged::{ComponentHandler, Paged};
use pavlov_discord::player_count::{ChannelRenamer, PlayerCountChannels, RenameOutcome, Targets};
use pavlov_discord::post::{ChannelPoster, PostEdit};
use pavlov_discord::registration::{self, RegistrationSettings};
use pavlov_discord::runner::{
    AccessPrimer, COMMAND_BUDGET, CommandRunner, GuildInstalled, RunnerParts,
};
use pavlov_discord::slash::{DisabledCommandList, SlashCommand, apply};
use pavlov_discord::staff_log::{ChannelStaffLog, StaffLogChannels};
use pavlov_discord::verification::{
    RoleGrantError, RoleGranter, VerificationChannels, VerificationService,
};
use pavlov_host::analytics::AnalyticsService;
use pavlov_host::confirmations::RconConfirmations;
use pavlov_host::crash_recovery::CrashRecovery;
use pavlov_host::economy::{Payroll, PayrollParts, SharedLedger};
use pavlov_host::factions::whitelist_backup::WhitelistBackup;
use pavlov_host::factions::{FactionMembers, RosterService};
use pavlov_host::feeds::{self as feed_labels, FeedWebhooks};
use pavlov_host::firewall::{Firewall, UfwFirewall};
use pavlov_host::health::HealthRegistry;
use pavlov_host::http::{HttpClient, ReqwestClient};
use pavlov_host::intelligence::{IntelligenceSources, PlayerIntelligenceService};
use pavlov_host::kill_stats::KillStats;
use pavlov_host::layout_gate::ServerLayoutGate;
use pavlov_host::logs::feed_bridge::{FeedBridge, FeedBridgeParts};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::logs::player_events;
use pavlov_host::logs::stats_log_service::{self, StatsLogService};
use pavlov_host::logs::tailer::{self, LogTailer};
use pavlov_host::master_list::MasterServerList;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::owner_actions::{LiftBan, OwnerActionParts, OwnerActions};
use pavlov_host::moderation::{
    AuditLog, BanFileExport, BanFirewall, BanService, CommandBlacklist, EvasionResponder,
    MasterAccess, MasterAccessParts, SecurityAlerts, ServerBanFile, StoredMasterNames,
    VpnResponder, WarningService,
};
use pavlov_host::monitoring::{
    MonitorBoard, MonitorHistory, MonitorParts, RconServerProbe, ServerMonitor, log_activity,
};
use pavlov_host::notice::{GRACE, PlayerNotice};
use pavlov_host::process::{CommandRunner as ProcessRunner, SystemRunner};
use pavlov_host::provisioner::ServerProvisioner;
use pavlov_host::recent_errors::RecentErrors;
use pavlov_host::registry::RconRegistry;
use pavlov_host::server_labels::ServerLabels;
use pavlov_host::service_control::ServiceControl;
use pavlov_host::services::ServiceRegistry;
use pavlov_host::timeline::{EventRecorder, Timeline};
use pavlov_host::vpn::detectors;
use pavlov_host::vpn::geo::{GeoLocator, GeoLocatorChain, GeoapifyGeoLocator, IpApiGeoLocator};
use pavlov_host::vpn::json_client::ResilientJsonClient;
use pavlov_host::vpn::screening::VpnScreeningService;
use pavlov_host::vpn::static_map::StaticMap;
use pavlov_rcon::{OnlineRoster, ServerLifecycle};
use pavlov_storage::datasets::SEEDS;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::ledger_store::LedgerFileStore;
use pavlov_storage::mod_save_sync::ModSaveSync;
use pavlov_storage::whitelist::WhitelistFile;
use pavlov_storage::{EventStore, NullEventStore, SqliteEventStore, SqliteKeyValueBackend};

use crate::startup::Settled;

/// The user agent for every outbound HTTP call. Generic on purpose: it names the software,
/// never the operator or the server.
pub const USER_AGENT: &str = "pavlov-bot";
/// How much of a log the tailer reads on first sight of it (C# `LogTailer` default).
pub const MAX_BACKFILL: u64 = 50 * 1024 * 1024;
/// How long a master's join grant waits for the game to finish the join (C#
/// `MasterAccess.DefaultGrantDelay`).
pub const MASTER_GRANT_DELAY: Duration = Duration::from_secs(8);

/// Everything that talks to Discord.
#[derive(Clone)]
pub struct Surfaces {
    pub poster: Arc<dyn ChannelPoster>,
    pub alerts: Arc<dyn AlertPoster>,
    pub renamer: Arc<dyn ChannelRenamer>,
    pub directory: Arc<dyn GuildDirectory>,
    pub members: Arc<dyn MemberSource>,
    pub roles: Arc<dyn RoleGranter>,
    pub guild_installed: GuildInstalled,
}

impl std::fmt::Debug for Surfaces {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Surfaces")
    }
}

/// Inert surfaces for `--selftest`: every call answers "not connected" and nothing leaves.
#[derive(Debug)]
struct Offline;

fn offline() -> DiscordError {
    DiscordError("not connected (selftest)".to_owned())
}

impl ChannelPoster for Offline {
    fn edit(&self, _: u64, _: u64, _: Embed, _: Vec<ActionRow>) -> BoxFuture<'_, PostEdit> {
        Box::pin(async { PostEdit::Failed })
    }
    fn send(
        &self,
        _: u64,
        _: Embed,
        _: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        Box::pin(async { Err(offline()) })
    }
}

impl AlertPoster for Offline {
    fn post_alert(
        &self,
        _: u64,
        _: Option<u64>,
        _: Embed,
    ) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Err(offline()) })
    }
}

impl ChannelRenamer for Offline {
    fn name_of(&self, _: u64) -> BoxFuture<'_, Option<String>> {
        Box::pin(async { None })
    }
    fn rename<'a>(&'a self, _: u64, _: &'a str) -> BoxFuture<'a, RenameOutcome> {
        Box::pin(async { RenameOutcome::Failed })
    }
}

impl GuildDirectory for Offline {
    fn invite_to_every_guild(&self, _: Duration, _: u32) -> BoxFuture<'_, Vec<GuildInvite>> {
        Box::pin(async { Vec::new() })
    }
    fn send_direct_message(&self, _: u64, _: Embed) -> BoxFuture<'_, Result<bool, DiscordError>> {
        Box::pin(async { Err(offline()) })
    }
}

impl MemberSource for Offline {
    fn guild_ids(&self) -> Vec<u64> {
        Vec::new()
    }
    fn fetch_member(
        &self,
        _: u64,
        _: u64,
    ) -> BoxFuture<'_, Result<Option<pavlov_discord::caller::MemberView>, DiscordError>> {
        Box::pin(async { Err(offline()) })
    }
}

impl RoleGranter for Offline {
    fn grant(&self, _: u64, _: u64, _: u64) -> BoxFuture<'_, Result<String, RoleGrantError>> {
        Box::pin(async {
            Err(RoleGrantError::Failed(
                "not connected (selftest)".to_owned(),
            ))
        })
    }
}

impl Surfaces {
    /// The selftest's surfaces.
    #[must_use]
    pub fn offline() -> Self {
        let inert = Arc::new(Offline);
        Self {
            poster: inert.clone(),
            alerts: inert.clone(),
            renamer: inert.clone(),
            directory: inert.clone(),
            members: inert.clone(),
            roles: inert,
            guild_installed: Arc::new(|_| false),
        }
    }
}

/// Why the graph could not be built.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GraphError(pub String);

impl std::fmt::Display for GraphError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for GraphError {}

/// The parts the background services and the run read. Public fields: this is the
/// composition root's own record, not an API.
#[allow(missing_debug_implementations)]
pub struct Graph {
    pub options: BotOptions,
    pub features: Arc<FeatureOptions>,
    pub factions: FactionSet,
    pub clock: Arc<dyn Clock>,
    pub metrics: Arc<MetricsRegistry>,
    pub health: Arc<HealthRegistry>,
    pub errors: Arc<RecentErrors>,
    pub backend: Arc<SqliteKeyValueBackend>,
    pub store: Arc<SerializedStore>,
    pub seeded: usize,
    pub rcon: Arc<RconRegistry>,
    pub services: Arc<ServiceRegistry>,
    pub masters: Arc<StoredMasterNames>,
    pub tracking: Arc<IpTrackingService>,
    pub bans: Arc<BanService>,
    pub ban_file: Arc<ServerBanFile>,
    pub ban_firewall: Arc<BanFirewall>,
    pub installs: Vec<String>,
    pub mod_save_sync: Arc<ModSaveSync>,
    pub tailer: Arc<LogTailer>,
    pub log_paths: Vec<String>,
    pub labels: Arc<ServerLabels>,
    pub stats_log: Arc<StatsLogService>,
    pub vpn: Arc<VpnScreeningService>,
    pub access: Arc<Access>,
    pub blacklist: Arc<CommandBlacklist>,
    pub alerts: Arc<SecurityAlerts>,
    pub kill_stats: Arc<KillStats>,
    pub feeds: Arc<FeedWebhooks>,
    pub events: Arc<dyn EventStore>,
    pub audit: Arc<AuditLog>,
    pub rosters: Arc<RosterService>,
    pub members: Arc<FactionMembers>,
    pub boards: Arc<Boards>,
    pub autopost: Arc<pavlov_discord::autopost::AutoPost>,
    pub service_control: Arc<ServiceControl>,
    pub crash_recovery: Arc<CrashRecovery>,
    pub payroll: Arc<Payroll>,
    pub counts: Arc<PlayerCountChannels>,
    pub monitor: Arc<ServerMonitor>,
    pub monitor_board: Arc<MonitorBoard>,
    pub whitelist_backup: Arc<WhitelistBackup>,
    pub master_access: Arc<MasterAccess>,
    pub owner_actions: Arc<OwnerActions>,
    pub verification: Arc<VerificationService>,
    pub home: Arc<HomeGuildMembers>,
    pub catalog: Arc<CommandCatalog>,
    pub dispatcher: Arc<InteractionDispatcher>,
    pub runner: Arc<CommandRunner>,
    pub router: Arc<ComponentRouter>,
    pub autocomplete: Arc<PlayerAutocomplete>,
    /// What the main application registers, and what the whitelist bot does.
    pub main_specs: Vec<CommandSpec>,
    pub faction_specs: Vec<CommandSpec>,
    pub registration: RegistrationSettings,
    pub command_names: Vec<String>,
    pub disabled_unknown: Vec<String>,
    pub disabled_count: usize,
}

fn fail(what: &str, error: impl std::fmt::Display) -> GraphError {
    GraphError(format!("{what}: {error}"))
}

/// Open the store, seeding any dataset the database has never held, and say how many.
fn open_store(
    data: &Path,
) -> Result<(Arc<SqliteKeyValueBackend>, Arc<SerializedStore>, usize), GraphError> {
    std::fs::create_dir_all(data)
        .map_err(|e| fail(&format!("cannot create {}", data.display()), e))?;
    let backend = Arc::new(
        SqliteKeyValueBackend::open(data.join("bot.db"))
            .map_err(|e| fail("cannot open bot.db", e))?,
    );
    let mut seeded = 0;
    for (key, json) in SEEDS {
        if backend
            .seed_if_missing(key, json)
            .map_err(|e| fail(&format!("cannot seed {key}"), e))?
        {
            seeded += 1;
        }
    }
    let store = Arc::new(
        SerializedStore::new(Arc::clone(&backend) as Arc<dyn KeyValueBackend>)
            .with_unreadable_hook(|key| {
                tracing::error!(
                    dataset = key,
                    "dataset holds data that cannot be parsed. Reads return an empty default and \
                     EVERY CHANGE TO IT IS REFUSED until it is fixed or restored - the \
                     alternative was writing that empty default over the real data. The JSON \
                     export in the data directory has the last good copy"
                );
            }),
    );
    Ok((backend, store, seeded))
}

/// Build the graph.
///
/// # Errors
/// When the store cannot be opened, or the owner guard refuses an identity.
#[allow(clippy::too_many_lines)]
pub fn build(
    settled: &Settled,
    config: Arc<Config>,
    cwd: &Path,
    surfaces: Surfaces,
) -> Result<Graph, GraphError> {
    let options = settled.options.clone();
    let features = Arc::new(settled.features.clone());
    let factions = settled.factions.clone();
    let clock: Arc<dyn Clock> = Arc::new(SystemClock);
    let metrics = Arc::new(MetricsRegistry::default());
    let errors = Arc::new(RecentErrors::new());
    let health = Arc::new(HealthRegistry::default());

    let (backend, store, seeded) = open_store(&options.data_directory)?;
    let rcon = Arc::new(RconRegistry::new(
        &options.servers,
        options.rcon_health_interval,
        Arc::clone(&metrics),
        Arc::clone(&clock),
    ));
    let online: Arc<dyn OnlineRoster> = rcon.clone();
    let services = Arc::new(ServiceRegistry::new(Arc::clone(&metrics)));
    let masters = Arc::new(
        StoredMasterNames::new(
            features.master_names.iter().map(String::as_str),
            Arc::clone(&store),
            Arc::clone(&clock),
        )
        .map_err(|e| fail("owner guard", e))?,
    );
    let guard = Arc::new(GameFileGuard::new(&features.ignored_paths));
    let installs = features.install_roots.clone();

    let tracking = Arc::new(
        IpTrackingService::new(Arc::clone(&store), Arc::clone(&metrics), Arc::clone(&clock))
            .with_masters(masters.clone()),
    );
    let resolve_tracking = Arc::clone(&tracking);
    let ban_file = Arc::new(ServerBanFile::new(
        features.ban_file_paths.clone(),
        Arc::clone(&store),
        Arc::clone(&clock),
        Some(Arc::new(move |id: &str| {
            resolve_tracking
                .account(id)
                .and_then(|a| a.names.first().cloned())
        })),
        Arc::clone(&guard),
    ));
    let bans = Arc::new(
        BanService::new(
            Arc::clone(&rcon),
            Arc::clone(&store),
            masters.clone(),
            Arc::clone(&clock),
        )
        .with_evidence(tracking.clone())
        .with_ban_file(ban_file.clone() as Arc<dyn BanFileExport>),
    );
    let mod_save_sync = Arc::new(ModSaveSync::new(
        installs.clone(),
        features.mod_save_sync,
        &features.mod_save_sync_skip_extra,
        features.roster_directory.as_deref(),
    ));
    let tailer = Arc::new(LogTailer::new(MAX_BACKFILL));
    let log_paths = tailer::discover(features.log_paths.as_deref(), &installs);
    let labels = Arc::new(ServerLabels::default());
    labels.assign(log_paths.clone());

    let http: Arc<dyn HttpClient> = Arc::new(
        ReqwestClient::new(USER_AGENT).map_err(|e| fail("cannot build the HTTP client", e))?,
    );
    let json = Arc::new(ResilientJsonClient::new(
        Arc::clone(&http),
        Arc::clone(&clock),
    ));
    let mut geo: Vec<(String, Arc<dyn GeoLocator>)> = vec![(
        "ip-api".to_owned(),
        Arc::new(IpApiGeoLocator::new(Arc::clone(&json))),
    )];
    if let Some(key) = features.geoapify_key.clone() {
        geo.push((
            "geoapify".to_owned(),
            Arc::new(GeoapifyGeoLocator::new(Arc::clone(&json), key)),
        ));
    }
    let static_map = features
        .geoapify_key
        .clone()
        .map(|key| Arc::new(StaticMap::new(Arc::clone(&http), key)));
    let vpn = Arc::new(
        VpnScreeningService::new(
            detectors::all(&json, &features.vpn_keys),
            Arc::clone(&store),
            Arc::new(GeoLocatorChain::new(geo)),
            Arc::clone(&metrics),
            Arc::clone(&clock),
        )
        .with_thresholds(features.vpn_thresholds)
        .with_cache_ttl(features.vpn_cache_ttl),
    );

    let access = Arc::new(
        Access::new(
            Arc::clone(&store),
            features.owners.iter().copied(),
            features.super_owners.iter().copied(),
        )
        .map_err(|e| fail("owner guard", e))?
        .with_factions(factions.clone()),
    );
    let owner_check = Arc::clone(&access);
    let blacklist = Arc::new(CommandBlacklist::new(
        Arc::clone(&store),
        Arc::new(move |id| owner_check.is_owner_id(id)),
        &features.barred_user_ids,
    ));
    let paged = Arc::new(Paged::new());
    let alerts = Arc::new(SecurityAlerts::new());
    let kill_stats = Arc::new(KillStats::new(Arc::clone(&store)));
    let feeds = Arc::new(FeedWebhooks::new(
        Arc::clone(&http),
        Arc::clone(&metrics),
        Arc::clone(&clock),
    ));
    for (label, url) in [
        (feed_labels::JOIN, &features.join_webhook),
        (feed_labels::CONNECT, &features.connect_webhook),
        (feed_labels::KILL, &features.kill_webhook),
        (feed_labels::RCON, &features.rcon_webhook),
        (feed_labels::STAFF, &features.staff_webhook),
    ] {
        feeds.register(label, url.as_ref().map(|u| u.expose()));
    }
    let confirmations = Arc::new(RconConfirmations::new(Arc::clone(&clock)));
    let stats_log = Arc::new(StatsLogService::new(
        stats_log_service::discover(features.stats_log_paths.as_deref(), &log_paths),
        Arc::clone(&tailer),
        Arc::clone(&metrics),
        None,
    ));

    let events: Arc<dyn EventStore> = if features.event_retention_days > 0 {
        Arc::new(
            SqliteEventStore::open(options.data_directory.join("bot.db"))
                .map_err(|e| fail("cannot open the event timeline", e))?,
        )
    } else {
        Arc::new(NullEventStore)
    };
    let timeline: Arc<dyn Timeline> = EventRecorder::start(Arc::clone(&events));
    let audit = Arc::new(
        AuditLog::new(Arc::clone(&store), Arc::clone(&clock))
            .with_feeds(Arc::clone(&feeds))
            .with_timeline(Arc::clone(&timeline)),
    );
    let vpn_responder = Arc::new(VpnResponder::new(
        Arc::clone(&bans),
        masters.clone(),
        Arc::clone(&store),
        Arc::clone(&audit),
        Arc::clone(&feeds),
        Arc::clone(&metrics),
        Arc::clone(&clock),
        features.vpn_auto_ban,
        Some(Arc::clone(&alerts)),
    ));
    player_events::attach(&tracking, Arc::clone(&timeline));
    let analytics = Arc::new(AnalyticsService::new(Arc::clone(&events)));

    let ledger_files = Arc::new(LedgerFileStore::new(
        features.ledger_directory.as_deref().map(PathBuf::from),
        Arc::clone(&guard),
    ));
    let balances: Arc<dyn BalanceStore> = ledger_files.clone();
    let ledger = Arc::new(SharedLedger::new(balances.clone()));
    let autopost = Arc::new(pavlov_discord::autopost::AutoPost::new(
        Arc::clone(&surfaces.poster),
        Arc::clone(&store),
    ));
    let members = Arc::new(FactionMembers::new(Arc::clone(&store)));
    let holds = Arc::clone(&members);
    let rosters = Arc::new(
        RosterService::new(
            features.roster_directory.as_deref().map(PathBuf::from),
            cwd.join("faction_file_bak"),
        )
        .with_factions(factions.clone())
        .with_guard(Arc::clone(&guard))
        .with_holds_all_ranks(Arc::new(move |name| holds.holds_all_ranks(name))),
    );
    let boards = Arc::new(Boards::new(
        Arc::clone(&store),
        Arc::clone(&rcon),
        features.ledger_directory.as_deref().map(PathBuf::from),
        Some(Arc::clone(&rosters)),
        Arc::clone(&clock),
    ));
    let catalog = Arc::new(CommandCatalog::new());
    let autocomplete = Arc::new(PlayerAutocomplete::new(
        Arc::clone(&rcon),
        Arc::clone(&tracking),
        Arc::clone(&store),
        Arc::clone(&rosters),
    ));

    let runner: Arc<dyn ProcessRunner> = Arc::new(SystemRunner);
    let service_control = Arc::new(ServiceControl::new(
        features.pavlov_units.clone(),
        features.systemctl_sudo,
        Arc::clone(&runner),
    ));
    rcon.use_lifecycle(service_control.clone() as Arc<dyn ServerLifecycle>);
    let notice = Arc::new(PlayerNotice::new(Arc::clone(&rcon)));
    let crash_recovery = Arc::new(CrashRecovery::new(
        service_control.clone(),
        Arc::clone(&clock),
    ));
    let warnings = Arc::new(WarningService::new(
        Arc::clone(&store),
        Arc::clone(&audit),
        Arc::clone(&clock),
    ));
    let payroll = Arc::new(Payroll::new(PayrollParts {
        store: Arc::clone(&store),
        ledger: Arc::clone(&ledger),
        rosters: Arc::clone(&rosters),
        online: Arc::clone(&online),
        clock: Arc::clone(&clock),
        amount: features.payroll_amount,
        period: features.payroll_interval,
        faction: Some(features.payroll_faction.clone()).filter(|f| !f.is_empty()),
    }));
    let intelligence = Arc::new(PlayerIntelligenceService::new(IntelligenceSources {
        tracking: Arc::clone(&tracking),
        bans: Arc::clone(&bans),
        warnings: Arc::clone(&warnings),
        rcon: Arc::clone(&rcon),
        store: Arc::clone(&store),
        clock: Arc::clone(&clock),
        rosters: Some(Arc::clone(&rosters)),
        members: Some(Arc::clone(&members)),
        balances: Some(balances.clone()),
        payroll: Some(Arc::clone(&payroll)),
        vpn: Some(Arc::clone(&vpn)),
        kill_stats: Some(Arc::clone(&kill_stats)),
    }));
    let provisioner = Arc::new(ServerProvisioner::new(Arc::clone(&runner)));
    let layout = Arc::new(ServerLayoutGate::new());
    let master_list = Arc::new(MasterServerList::new(
        Arc::clone(&http),
        features.pavlov_version.as_deref(),
        Arc::clone(&clock),
    ));
    let browser = Arc::new(commands::server_lookup::ServerBrowser::new(
        Arc::clone(&master_list),
        commands::server_lookup::OwnServers {
            hosts: options.servers.iter().map(|s| s.host.clone()).collect(),
            names: options.servers.iter().map(|s| s.name.clone()).collect(),
        },
        Arc::clone(&clock),
    ));
    let counts = Arc::new(PlayerCountChannels::new(
        Arc::clone(&surfaces.renamer),
        Arc::clone(&rcon),
        Arc::clone(&master_list),
        service_control.clone(),
        i32::try_from(features.default_server_capacity).unwrap_or(i32::MAX),
    ));
    let whitelist = Arc::new(WhitelistFile::new(
        Arc::clone(&guard),
        cwd.join("whitelist_bak"),
    ));
    let firewall: Arc<dyn Firewall> = Arc::new(UfwFirewall::new(Arc::clone(&runner)));
    let whitelist_backup = Arc::new(WhitelistBackup::new(
        Arc::clone(&store),
        Arc::clone(&rosters),
        Arc::clone(&clock),
    ));
    let ban_firewall = Arc::new(BanFirewall::new(
        Arc::clone(&bans),
        Arc::clone(&tracking),
        masters.clone(),
        Arc::clone(&firewall),
        Arc::clone(&store),
        features.firewall_bans,
        &features.firewall_never_block,
        Arc::clone(&clock),
    ));
    let lift_bans = Arc::clone(&bans);
    let lift: LiftBan = Arc::new(move |player: String| {
        let bans = Arc::clone(&lift_bans);
        Box::pin(async move {
            let result = bans.lift(&player, None).await;
            if result.landed() {
                Ok(())
            } else {
                Err(format!("the ban on {player} did not lift everywhere"))
            }
        })
    });
    let owner_actions = Arc::new(OwnerActions::new(
        Arc::clone(&store),
        Arc::clone(&tracking),
        OwnerActionParts {
            ledger_directory: features.ledger_directory.as_deref().map(PathBuf::from),
            masters: Some(masters.clone()),
            lift_ban: Some(lift),
            firewall: features
                .firewall_blacklisted_ips
                .then(|| Arc::clone(&firewall)),
            whitelists: Some(Arc::clone(&whitelist_backup)),
            blacklist: Some(Arc::clone(&blacklist)),
        },
    ));

    let history = Arc::new(MonitorHistory::new(Arc::clone(&store), Arc::clone(&clock)));
    let probe = Arc::new(RconServerProbe::new(
        Arc::clone(&rcon),
        features.monitor_settings.clone(),
        Arc::clone(&clock),
        Some(service_control.clone() as Arc<dyn ServerLifecycle>),
        Some(log_activity(Arc::clone(&labels), log_paths.clone())),
    ));
    let monitor_alerts = Arc::new(ChannelMonitorAlerts::new(
        Arc::clone(&surfaces.alerts),
        features.monitor_alert_channel,
        features.monitor_alert_role,
    ));
    let monitor = Arc::new(ServerMonitor::new(MonitorParts {
        probe,
        alerts: monitor_alerts.clone(),
        history,
        settings: features.monitor_settings.clone(),
        targets: rcon.clone(),
        clock: Arc::clone(&clock),
        post_individual_alerts: !features.monitor_board_enabled,
    }));
    let monitor_board = Arc::new(MonitorBoard::new(
        Arc::clone(&monitor),
        service_control.clone(),
    ));
    let master_access = MasterAccess::new(MasterAccessParts {
        masters: masters.clone(),
        servers: Arc::clone(&labels),
        installs: installs.clone(),
        files: Arc::clone(&whitelist),
        clock: Arc::clone(&clock),
        grant_delay: MASTER_GRANT_DELAY,
        rosters: Some(Arc::clone(&rosters)),
        store: Some(Arc::clone(&store)),
    });
    let verification = Arc::new(VerificationService {
        store: Arc::clone(&store),
        tracking: Arc::clone(&tracking),
        feeds: Arc::clone(&feeds),
        audit: Arc::clone(&audit),
        poster: Arc::clone(&surfaces.poster),
        roles: Arc::clone(&surfaces.roles),
        channels: VerificationChannels {
            panel: features.verify_channel,
            staff: features.verify_staff_channel,
            role: features.verified_role,
        },
        clock: Arc::clone(&clock),
    });

    // ---- the wiring after the build (C# after `builder.Build()`) ----
    let staff_log = Arc::new(ChannelStaffLog::new(
        Arc::clone(&surfaces.poster),
        StaffLogChannels {
            moderation: features.mod_log_channel,
            ban: features.ban_log_channel,
            police: features.police_log_channel,
            arrest: features.arrest_channel,
        },
    ));
    if staff_log.enabled() {
        audit.use_sink(staff_log);
    }
    let direct = Arc::new(
        pavlov_discord::direct_message_alerts::DirectMessageAlerts::new(
            Arc::clone(&surfaces.directory),
            features.security_alert_recipients(),
            Arc::clone(&metrics),
        ),
    );
    if direct.enabled() {
        alerts.use_sink(direct);
    }
    let home = Arc::new(HomeGuildMembers::new(
        Arc::clone(&surfaces.members),
        options.home_guild_id,
        options.guild_id,
    ));
    let lookup_home = Arc::clone(&home);
    let id_home = Arc::clone(&home);
    access.use_home_guild(
        Arc::new(move |user| lookup_home.member(user)),
        Some(Arc::new(move || id_home.home())),
    );
    owner_actions.restore_ignore_list();
    let bridge = FeedBridge::attach(
        Arc::clone(&tracking),
        Arc::clone(&feeds),
        Arc::clone(&labels),
        FeedBridgeParts {
            vpn: Some(Arc::clone(&vpn)),
            masters: Some(masters.clone()),
            vpn_bans: Some(vpn_responder),
            alerts: Some(Arc::clone(&alerts)),
            kill_stats: Some(Arc::clone(&kill_stats)),
        },
    );
    let noted = Arc::clone(&confirmations);
    tracking.rcon.subscribe(move |logged| {
        noted.note(&logged.action.full(), Some(&logged.file));
        Box::pin(async {})
    });
    let log_labels = Arc::clone(&labels);
    rcon.use_log_confirmation(
        Arc::clone(&confirmations),
        Arc::new(move |server| log_labels.log_for(server)),
    );
    if stats_log.enabled() {
        let kills = Arc::clone(&bridge);
        stats_log.killed.subscribe(move |kill| {
            kills.on_stats_kill(&kill);
            Box::pin(async {})
        });
        tracking.use_stats_log_kills();
    }
    // Subscribed for its side effect: a detection with no responder logs catches and acts on none.
    let _evasion = EvasionResponder::attach(
        Arc::clone(&tracking),
        Arc::clone(&bans),
        masters.clone(),
        Arc::clone(&store),
        Arc::clone(&audit),
        Arc::clone(&feeds),
        Arc::clone(&metrics),
        Arc::clone(&clock),
        Some(Arc::clone(&alerts)),
    );
    let granting = Arc::clone(&master_access);
    tracking.joined.subscribe(move |join| {
        granting.on_joined(&join);
        Box::pin(async {})
    });

    // ---- the commands, in the C# registration order ----
    // The menu tiers live in ServerRoles beside the rank files, so they go through the same
    // roster service: one write path, one per-file lock, one guard.
    let menus = Arc::new(commands::menu::MenuAccess {
        rosters: Arc::clone(&rosters),
        store: Arc::clone(&store),
        clock: Arc::clone(&clock),
    });
    let booking = Arc::new(commands::arrest::ArrestBooking::new(Arc::new(
        commands::arrest::ArrestRecorder {
            store: Arc::clone(&store),
            audit: Arc::clone(&audit),
            clock: Arc::clone(&clock),
        },
    )));
    let config_panel = Arc::new(commands::config_panel::ConfigPanel {
        actions: Arc::clone(&owner_actions),
        access: Arc::clone(&access),
        audit: Arc::clone(&audit),
    });
    let wipe = Arc::new(commands::whitelist_command::WhitelistWipe {
        rosters: Arc::clone(&rosters),
        members: Arc::clone(&members),
        access: Arc::clone(&access),
        audit: Arc::clone(&audit),
    });
    let ban_parts = Arc::new(commands::bans::BanParts {
        bans: Arc::clone(&bans),
        tracking: Arc::clone(&tracking),
        store: Arc::clone(&store),
        access: Arc::clone(&access),
        audit: Arc::clone(&audit),
        masters: masters.clone(),
        ban_file: Arc::clone(&ban_file),
        clock: Arc::clone(&clock),
    });
    let env_path = cwd.join(".env");
    let rcon_ports: Vec<i32> = options.servers.iter().map(|s| s.port).collect();
    let saves = features
        .ledger_directory
        .is_some()
        .then(|| Arc::clone(&ledger_files));
    let targets = Targets {
        server_channels: features.player_count_channels.clone(),
        total_channel: features.shack_total_channel,
    };

    use commands::*;
    let all: Vec<Arc<dyn SlashCommand>> = vec![
        Arc::new(whitelist_command::WhitelistCommand {
            rosters: Arc::clone(&rosters),
            members: Arc::clone(&members),
            access: Arc::clone(&access),
            boards: Arc::clone(&boards),
            audit: Arc::clone(&audit),
            paged: Some(Arc::clone(&paged)),
        }),
        Arc::new(rank_change::RankChangeCommand::promotion(
            Arc::clone(&rosters),
            Arc::clone(&members),
            Arc::clone(&access),
            Arc::clone(&audit),
        )),
        Arc::new(rank_change::RankChangeCommand::demotion(
            Arc::clone(&rosters),
            Arc::clone(&members),
            Arc::clone(&access),
            Arc::clone(&audit),
        )),
        Arc::new(set_roles::SetRolesCommand::new(
            Arc::clone(&store),
            Arc::clone(&access),
            &factions,
            &options.disabled_commands,
        )),
        Arc::new(bans::TempBanCommand {
            parts: Arc::clone(&ban_parts),
        }),
        Arc::new(bans::PermBanCommand {
            parts: Arc::clone(&ban_parts),
        }),
        Arc::new(bans::UnbanCommand {
            parts: Arc::clone(&ban_parts),
        }),
        Arc::new(bans::CheckBanCommand {
            parts: Arc::clone(&ban_parts),
        }),
        Arc::new(warn::WarnCommand {
            warnings: Arc::clone(&warnings),
            bans: Arc::clone(&bans),
            tracking: Arc::clone(&tracking),
            store: Arc::clone(&store),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
            masters: masters.clone(),
            clock: Arc::clone(&clock),
        }),
        Arc::new(utility::KickCommand {
            rcon: Arc::clone(&rcon),
            bans: Arc::clone(&bans),
            audit: Arc::clone(&audit),
            access: Arc::clone(&access),
        }),
        Arc::new(utility::AnnounceCommand {
            rcon: Arc::clone(&rcon),
            access: Arc::clone(&access),
        }),
        Arc::new(utility::HealthCommand {
            health: Arc::clone(&health),
            services: Arc::clone(&services),
            access: Arc::clone(&access),
        }),
        Arc::new(utility::HelpCommand {
            catalog: Arc::clone(&catalog),
            access: Arc::clone(&access),
        }),
        Arc::new(players::PlayersCommand {
            rcon: Arc::clone(&rcon),
            paged: Arc::clone(&paged),
            clock: Arc::clone(&clock),
        }),
        Arc::new(server_info::ServerInfoCommand {
            rcon: Arc::clone(&rcon),
        }),
        Arc::new(server_lookup::ServerLookupCommand {
            browser: Arc::clone(&browser),
        }),
        Arc::new(stats::StatsCommand {
            intelligence: Arc::clone(&intelligence),
            members: Arc::clone(&members),
            access: Arc::clone(&access),
        }),
        Arc::new(feeds::FeedsCommand {
            feeds: Arc::clone(&feeds),
            access: Arc::clone(&access),
            clock: Arc::clone(&clock),
        }),
        Arc::new(faction_stats::FactionStatsCommand {
            rosters: Arc::clone(&rosters),
            boards: Arc::clone(&boards),
            store: Arc::clone(&store),
            access: Arc::clone(&access),
            clock: Arc::clone(&clock),
        }),
        Arc::new(analytics::StaffStatsCommand {
            analytics,
            access: Arc::clone(&access),
            clock: Arc::clone(&clock),
        }),
        Arc::new(staff::StaffActivityCommand {
            audit: Arc::clone(&audit),
            access: Arc::clone(&access),
            clock: Arc::clone(&clock),
        }),
        Arc::new(staff::StaffLeaderboardCommand {
            boards: Arc::clone(&boards),
            access: Arc::clone(&access),
        }),
        Arc::new(staff::BanListCommand {
            bans: Arc::clone(&bans),
        }),
        Arc::new(staff::FlushCommand {
            rcon: Arc::clone(&rcon),
            bans: Arc::clone(&bans),
            masters: masters.clone(),
            audit: Arc::clone(&audit),
            access: Arc::clone(&access),
        }),
        Arc::new(menu::StripMenuCommand {
            menus: Arc::clone(&menus),
            access: Arc::clone(&access),
        }),
        Arc::new(police::WarrantCommand {
            store: Arc::clone(&store),
            access: Arc::clone(&access),
            paged: Arc::clone(&paged),
            clock: Arc::clone(&clock),
        }),
        Arc::new(police::BackgroundCheckCommand {
            store: Arc::clone(&store),
        }),
        Arc::new(police::BailCommand {
            store: Arc::clone(&store),
            access: Arc::clone(&access),
        }),
        Arc::new(arrest::ArrestCommand {
            store: Arc::clone(&store),
            access: Arc::clone(&access),
            booking: Arc::clone(&booking),
            rcon: Arc::clone(&rcon),
        }),
        Arc::new(license::LicenseCommand {
            saves: saves.clone(),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
        }),
        Arc::new(economy::MoneyCommand::give(
            Arc::clone(&ledger),
            Arc::clone(&audit),
            Arc::clone(&access),
            saves.clone(),
        )),
        Arc::new(economy::MoneyCommand::adjust(
            Arc::clone(&ledger),
            Arc::clone(&audit),
            Arc::clone(&access),
            saves,
        )),
        Arc::new(economy::WagesCommand {
            payroll: Arc::clone(&payroll),
        }),
        Arc::new(admin::SetPinCommand {
            rcon: Arc::clone(&rcon),
            access: Arc::clone(&access),
        }),
        Arc::new(admin::RemovePinCommand {
            rcon: Arc::clone(&rcon),
            access: Arc::clone(&access),
        }),
        Arc::new(admin::IpLookupCommand {
            vpn: Arc::clone(&vpn),
            access: Arc::clone(&access),
            map: static_map,
        }),
        Arc::new(admin::VpnCheckCommand {
            vpn: Arc::clone(&vpn),
            access: Arc::clone(&access),
        }),
        Arc::new(alts::AltsCommand {
            tracking: Arc::clone(&tracking),
            access: Arc::clone(&access),
        }),
        Arc::new(errors::ErrorsCommand {
            errors: Arc::clone(&errors),
            access: Arc::clone(&access),
        }),
        Arc::new(test_alert::TestAlertCommand {
            alerts: Arc::clone(&alerts),
            recipients: features.security_alert_recipients(),
            access: Arc::clone(&access),
            clock: Arc::clone(&clock),
        }),
        Arc::new(counts::CountsCommand {
            counts: Arc::clone(&counts),
            targets: targets.clone(),
            access: Arc::clone(&access),
        }),
        Arc::new(rotate_map::RotateMapCommand {
            rcon: Arc::clone(&rcon),
            unit_count: features.pavlov_units.len(),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
        }),
        Arc::new(guild_invites::GuildInvitesCommand {
            directory: Arc::clone(&surfaces.directory),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
        }),
        Arc::new(provision_server::ProvisionServerCommand {
            provisioner: provisioner.clone(),
            layout: Arc::clone(&layout),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
            config: Arc::clone(&config),
            rcon_ports,
            default_capacity: i32::try_from(features.default_server_capacity).unwrap_or(i32::MAX),
            poster: Arc::clone(&surfaces.poster),
            env_path: env_path.clone(),
        }),
        Arc::new(delete_server::DeleteServerCommand {
            provisioner,
            layout,
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
            config,
            poster: Arc::clone(&surfaces.poster),
            env_path,
        }),
        Arc::new(server_switch::ServerSwitchCommand {
            services: service_control.clone(),
            notice: Arc::clone(&notice),
            rcon: Arc::clone(&rcon),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
            grace: GRACE,
        }),
        Arc::new(tester::TestModeCommand {
            services: service_control.clone(),
            notice,
            whitelist: Arc::clone(&whitelist),
            guard: Arc::clone(&guard),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
            installs: installs.clone(),
            grace: GRACE,
        }),
        Arc::new(tester::TesterCommand {
            whitelist: Arc::clone(&whitelist),
            access: Arc::clone(&access),
            audit: Arc::clone(&audit),
            installs: installs.clone(),
        }),
        Arc::new(monitor::MonitorCommand {
            monitor: Arc::clone(&monitor),
            service: service_control.clone(),
            alerts: monitor_alerts,
            access: Arc::clone(&access),
        }),
        Arc::new(owner::BotStatsCommand {
            metrics: Arc::clone(&metrics),
            services: service_control.clone(),
            access: Arc::clone(&access),
            kills: Some(Arc::clone(&kill_stats)),
        }),
        Arc::new(owner::ManualCommand {
            rcon: Arc::clone(&rcon),
            audit: Arc::clone(&audit),
            access: Arc::clone(&access),
        }),
        Arc::new(owner::FirewallCommand {
            firewall,
            audit: Arc::clone(&audit),
            access: Arc::clone(&access),
        }),
        Arc::new(owner::InspectCommand {
            tracking: Arc::clone(&tracking),
            audit: Arc::clone(&audit),
            bans: Arc::clone(&bans),
            access: Arc::clone(&access),
        }),
        Arc::new(config::DonatorCommand {
            store: Arc::clone(&store),
            rosters: Arc::clone(&rosters),
            access: Arc::clone(&access),
            paged: Arc::clone(&paged),
            clock: Arc::clone(&clock),
        }),
        Arc::new(config::SuspendRankCommand {
            store: Arc::clone(&store),
            rosters: Arc::clone(&rosters),
            access: Arc::clone(&access),
            clock: Arc::clone(&clock),
        }),
        Arc::new(menu::GiveMenuCommand {
            menus,
            access: Arc::clone(&access),
        }),
        Arc::new(menu::UnlinkNameCommand {
            store: Arc::clone(&store),
            access: Arc::clone(&access),
        }),
        config_panel.clone(),
    ];
    // Sub-class commands only for a ladder that has sub-classes: the built-in GWPD has none
    // since the move to ServerRoles, and a command that can only ever say "none" is noise.
    let mut all = all;
    if factions.all().any(|f| !f.subclasses.is_empty()) {
        all.push(Arc::new(subclass::SubclassCommand {
            rosters: Arc::clone(&rosters),
            members: Arc::clone(&members),
            access: Arc::clone(&access),
        }));
        all.push(Arc::new(rank_change::SubclassesCommand {
            rosters: Arc::clone(&rosters),
            paged: Arc::clone(&paged),
        }));
    }
    let available = all.len();
    let disabled = DisabledCommandList::parse(&options.disabled_commands);
    let (kept, disabled_unknown) = apply(&all, &disabled);
    let disabled_count = available - kept.len();
    let mut command_names: Vec<String> = kept.iter().map(|c| c.name().to_owned()).collect();
    command_names.sort();
    catalog.populate(kept.iter().map(|c| {
        let spec = c.build();
        CommandSummary {
            name: spec.name,
            description: spec.description,
        }
    }));

    let built: Vec<CommandSpec> = kept.iter().map(|c| c.build()).collect();
    let faction_bot = options.faction_bot_enabled();
    let mut main_specs =
        registration::validated(registration::main_set(built.clone(), faction_bot), "main");
    if options.user_app {
        main_specs = registration::as_user_app(main_specs);
    }
    let faction_specs = registration::validated(registration::faction_set(built), "whitelist");
    let registration = RegistrationSettings {
        user_app: options.user_app,
        guild_id: options.guild_id,
        faction_bot_enabled: faction_bot,
    };

    let dispatcher = Arc::new(InteractionDispatcher::new());
    let command_log = features
        .command_log_channel
        .map(|channel| Arc::new(CommandLog::new(Arc::clone(&surfaces.poster), channel)));
    let runner = Arc::new(CommandRunner::new(RunnerParts {
        commands: kept,
        blacklist: Some(Arc::clone(&blacklist)),
        command_log,
        dispatcher: Arc::clone(&dispatcher),
        metrics: Arc::clone(&metrics),
        errors: Arc::clone(&errors),
        primer: Some(home.clone() as Arc<dyn AccessPrimer>),
        guild_installed: Arc::clone(&surfaces.guild_installed),
        budget: COMMAND_BUDGET,
    }));
    let handlers: Vec<Arc<dyn ComponentHandler>> = vec![
        paged,
        booking,
        verification.clone(),
        wipe,
        browser,
        config_panel,
    ];
    let router = Arc::new(ComponentRouter::new(
        handlers,
        Some(Arc::clone(&blacklist)),
        Some(home.clone() as Arc<dyn AccessPrimer>),
        Arc::clone(&metrics),
    ));

    Ok(Graph {
        options,
        features,
        factions,
        clock,
        metrics,
        health,
        errors,
        backend,
        store,
        seeded,
        rcon,
        services,
        masters,
        tracking,
        bans,
        ban_file,
        ban_firewall,
        installs,
        mod_save_sync,
        tailer,
        log_paths,
        labels,
        stats_log,
        vpn,
        access,
        blacklist,
        alerts,
        kill_stats,
        feeds,
        events,
        audit,
        rosters,
        members,
        boards,
        autopost,
        service_control,
        crash_recovery,
        payroll,
        counts,
        monitor,
        monitor_board,
        whitelist_backup,
        master_access,
        owner_actions,
        verification,
        home,
        catalog,
        dispatcher,
        runner,
        router,
        autocomplete,
        main_specs,
        faction_specs,
        registration,
        command_names,
        disabled_unknown,
        disabled_count,
    })
}
