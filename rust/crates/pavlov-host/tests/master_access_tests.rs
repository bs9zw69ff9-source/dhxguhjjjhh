//! Port of `MasterAccessTests.cs`: master names in `mods.txt`, the whitelists and the rosters,
//! the menu over RCON on join (asserted on the wire), and everything taken back from a name
//! removed from `MASTER_NAMES`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use chrono::Utc;
use common::file_store;
use pavlov_core::data::SerializedStore;
use pavlov_core::factions::FactionRegistry;
use pavlov_core::servers::installs::{log_path, whitelist_path};
use pavlov_core::time::SystemClock;
use pavlov_host::factions::RosterService;
use pavlov_host::logs::PlayerJoined;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::master_access::{grant_commands, mods_path};
use pavlov_host::moderation::{MasterAccess, MasterAccessParts, StoredMasterNames};
use pavlov_host::registry::RconRegistry;
use pavlov_host::server_labels::ServerLabels;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_storage::datasets::MASTER_GRANTS;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::whitelist::WhitelistFile;

const MASTER: &str = "fki6";

struct Fixture {
    root: tempfile::TempDir,
    server: FakeRconServer,
    rcon: Arc<RconRegistry>,
    labels: Arc<ServerLabels>,
    install: String,
    log: String,
    roster_dir: PathBuf,
    rosters: Arc<RosterService>,
    access: Arc<MasterAccess>,
}

impl Fixture {
    async fn new() -> Self {
        let root = tempfile::tempdir().unwrap();
        let install = root.path().join("pavlovserver");
        std::fs::create_dir_all(install.join("Pavlov/Saved/Config")).unwrap();
        let install = install.to_str().unwrap().to_owned();
        let log = log_path(&install);
        let labels = Arc::new(ServerLabels::default());
        labels.assign(vec![log.clone()]);

        let server = FakeRconServer::start().await.unwrap();
        let mut options = server.options();
        options.read_cache_duration = Duration::ZERO;
        options.max_attempts = 1;
        let rcon = Arc::new(RconRegistry::with_system_clock(
            &[options],
            Duration::from_secs(60),
            Arc::new(MetricsRegistry::default()),
        ));

        let roster_dir = PathBuf::from(&install).join("Pavlov/Saved/Config/ModSave/FactionRoles");
        std::fs::create_dir_all(&roster_dir).unwrap();
        let rosters = Arc::new(
            RosterService::new(Some(roster_dir.clone()), root.path().join("roster_bak"))
                .with_factions(FactionRegistry::police().clone()),
        );

        let access = MasterAccess::new(parts(
            &Pieces {
                root: root.path(),
                rcon: &rcon,
                labels: &labels,
                install: &install,
                rosters: &rosters,
            },
            file_store(&root.path().join("data")),
            &[MASTER],
            false,
        ));
        Self {
            root,
            server,
            rcon,
            labels,
            install,
            log,
            roster_dir,
            rosters,
            access,
        }
    }

    fn pieces(&self) -> Pieces<'_> {
        Pieces {
            root: self.root.path(),
            rcon: &self.rcon,
            labels: &self.labels,
            install: &self.install,
            rosters: &self.rosters,
        }
    }

    fn files(&self) -> Arc<WhitelistFile> {
        Arc::new(WhitelistFile::new(
            Arc::new(GameFileGuard::none()),
            self.root.path().join("whitelist_bak"),
        ))
    }

    /// `record`: wire the store in, so grants are recorded and removals revoked.
    fn build(
        &self,
        store: Arc<SerializedStore>,
        masters: &[&str],
        record: bool,
    ) -> Arc<MasterAccess> {
        MasterAccess::new(MasterAccessParts {
            masters: Arc::new(
                StoredMasterNames::new(
                    masters.iter().copied(),
                    Arc::clone(&store),
                    Arc::new(SystemClock),
                )
                .unwrap(),
            ),
            rcon: Arc::clone(&self.rcon),
            servers: Arc::clone(&self.labels),
            installs: vec![self.install.clone()],
            files: self.files(),
            clock: Arc::new(SystemClock),
            grant_delay: Duration::ZERO,
            rosters: Some(Arc::clone(&self.rosters)),
            store: record.then_some(store),
        })
    }

    fn grant_store(&self) -> Arc<SerializedStore> {
        file_store(&self.root.path().join("grants"))
    }

    fn mods(&self) -> String {
        mods_path(&self.install)
    }

    fn whitelist(&self) -> String {
        whitelist_path(&self.install)
    }

    fn join(&self, name: &str, file: Option<&str>) -> PlayerJoined {
        PlayerJoined {
            file: file.unwrap_or(&self.log).to_owned(),
            account_id: name.to_owned(),
            name: Some(name.to_owned()),
            ip: None,
            confident: false,
            at: Utc::now(),
        }
    }

    /// Wait for the background grant to reach the fake server, then give anything more a
    /// moment to prove it does not arrive.
    async fn commands_after_grant(&self, expected: usize) -> Vec<String> {
        let deadline = Instant::now() + Duration::from_secs(10);
        while self.server.commands().len() < expected && Instant::now() < deadline {
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        tokio::time::sleep(Duration::from_millis(200)).await;
        self.server.commands()
    }

    fn roster_path(&self, file: &str) -> PathBuf {
        self.roster_dir.join(file)
    }

    fn roster_files(&self) -> Vec<String> {
        RosterService::roster_files_of(self.rosters.factions())
    }

    /// Start a fresh service over `store` with `masters`, then stop it.
    async fn run(&self, store: &Arc<SerializedStore>, masters: &[&str]) {
        let access = self.build(Arc::clone(store), masters, true);
        access.start(None).await;
        access.stop(Duration::from_secs(5)).await;
    }
}

/// What every service in these tests is built from.
struct Pieces<'a> {
    root: &'a std::path::Path,
    rcon: &'a Arc<RconRegistry>,
    labels: &'a Arc<ServerLabels>,
    install: &'a str,
    rosters: &'a Arc<RosterService>,
}

/// `record`: wire the store in, so grants are recorded and removals revoked.
fn parts(
    p: &Pieces<'_>,
    store: Arc<SerializedStore>,
    masters: &[&str],
    record: bool,
) -> MasterAccessParts {
    MasterAccessParts {
        masters: Arc::new(
            StoredMasterNames::new(
                masters.iter().copied(),
                Arc::clone(&store),
                Arc::new(SystemClock),
            )
            .unwrap(),
        ),
        rcon: Arc::clone(p.rcon),
        servers: Arc::clone(p.labels),
        installs: vec![p.install.to_owned()],
        files: Arc::new(WhitelistFile::new(
            Arc::new(GameFileGuard::none()),
            p.root.join("whitelist_bak"),
        )),
        clock: Arc::new(SystemClock),
        grant_delay: Duration::ZERO,
        rosters: Some(Arc::clone(p.rosters)),
        store: record.then_some(store),
    }
}

fn lines(path: impl AsRef<std::path::Path>) -> Vec<String> {
    std::fs::read_to_string(path)
        .unwrap()
        .lines()
        .map(str::to_owned)
        .collect()
}

fn has(path: impl AsRef<std::path::Path>, name: &str) -> bool {
    lines(path).iter().any(|l| l == name)
}

#[test]
fn the_grant_is_the_full_menu_with_no_bitcode() {
    assert_eq!(grant_commands(MASTER), [format!("GiveMenu {MASTER}")]);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_master_joining_gets_the_full_menu_on_the_wire() {
    let f = Fixture::new().await;
    f.access.on_joined(&f.join(MASTER, None));

    assert_eq!(
        f.commands_after_grant(1).await,
        [format!("GiveMenu {MASTER}")]
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn master_names_match_ignoring_case() {
    let f = Fixture::new().await;
    f.access.on_joined(&f.join("FKI6", None));

    assert_eq!(f.commands_after_grant(1).await.len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn anybody_else_joining_gets_nothing() {
    let f = Fixture::new().await;
    f.access.on_joined(&f.join("RandomPlayer", None));

    assert!(f.commands_after_grant(0).await.is_empty());
    assert!(!PathBuf::from(f.mods()).exists());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_reconnect_flurry_grants_once() {
    let f = Fixture::new().await;
    for _ in 0..3 {
        f.access.on_joined(&f.join(MASTER, None));
    }

    f.commands_after_grant(1).await;
    tokio::time::sleep(Duration::from_millis(300)).await;
    assert_eq!(f.server.commands().len(), 1);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_join_on_a_log_that_is_not_a_server_sends_nothing() {
    let f = Fixture::new().await;
    f.access
        .on_joined(&f.join(MASTER, Some("/somewhere/else/Pavlov.log")));

    assert!(f.commands_after_grant(0).await.is_empty());
}

#[tokio::test(flavor = "multi_thread")]
async fn missing_masters_are_appended_and_everything_else_is_kept() {
    let f = Fixture::new().await;
    std::fs::write(f.mods(), "# staff\nSomeMod\n").unwrap();

    f.access.ensure_files().await;

    let lines = lines(f.mods());
    assert_eq!(lines[0], "# staff");
    assert_eq!(lines[1], "SomeMod");
    assert!(lines.iter().any(|l| l == MASTER));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_present_master_is_not_written_twice() {
    let f = Fixture::new().await;
    std::fs::write(f.mods(), "FKI6\n").unwrap();
    let before = std::fs::read_to_string(f.mods()).unwrap();

    f.access.ensure_files().await;
    f.access.ensure_files().await;

    assert_eq!(std::fs::read_to_string(f.mods()).unwrap(), before);
}

#[tokio::test(flavor = "multi_thread")]
async fn a_join_checks_that_servers_mods_file() {
    let f = Fixture::new().await;
    f.access.grant(1, MASTER).await;

    assert!(has(f.mods(), MASTER));
}

#[tokio::test(flavor = "multi_thread")]
async fn masters_are_on_every_whitelist() {
    let f = Fixture::new().await;
    std::fs::write(f.roster_path("policepatrolman.txt"), "PlayerOne\n").unwrap();
    std::fs::write(f.whitelist(), "# friends\nPlayerOne\n").unwrap();

    f.access.ensure_files().await;

    let files = f.roster_files();
    assert!(!files.is_empty());
    for file in &files {
        assert!(has(f.roster_path(file), MASTER), "{file}");
    }
    assert!(has(f.roster_path("policepatrolman.txt"), "PlayerOne"));
    let whitelist = lines(f.whitelist());
    assert_eq!(whitelist[..2], ["# friends", "PlayerOne"]);
    assert!(whitelist.iter().any(|l| l == MASTER));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_roster_already_holding_the_masters_is_not_rewritten() {
    let f = Fixture::new().await;
    let path = f.roster_path("policepatrolman.txt");
    std::fs::write(&path, "fki6\n").unwrap();
    f.access.ensure_files().await;
    let written = std::fs::metadata(&path).unwrap().modified().unwrap();
    let before = std::fs::read_to_string(&path).unwrap();

    f.access.ensure_files().await;

    assert_eq!(std::fs::read_to_string(&path).unwrap(), before);
    assert_eq!(
        std::fs::metadata(&path).unwrap().modified().unwrap(),
        written
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_join_puts_a_wiped_master_back_on_the_rosters() {
    let f = Fixture::new().await;
    let path = f.roster_path("policepatrolman.txt");
    std::fs::write(&path, "PlayerOne\n").unwrap();

    f.access.grant(1, MASTER).await;

    assert!(has(&path, MASTER));
    assert!(has(f.whitelist(), MASTER));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_missing_config_directory_is_not_created() {
    let f = Fixture::new().await;
    let bogus = f.root.path().join("not-an-install");
    let access = MasterAccess::new(MasterAccessParts {
        installs: vec![bogus.to_str().unwrap().to_owned()],
        rosters: None,
        ..parts(
            &f.pieces(),
            file_store(&f.root.path().join("data2")),
            &[MASTER],
            false,
        )
    });

    access.ensure_files().await;

    assert!(!bogus.exists());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_removed_master_loses_everything_it_was_given() {
    // THE BUG: dropping a name from .env only stopped the bot granting it again.
    let f = Fixture::new().await;
    std::fs::write(f.roster_path("policepatrolman.txt"), "PlayerOne\n").unwrap();
    let store = f.grant_store();

    f.run(&store, &[MASTER, "OldAlt", "KeptMaster"]).await;
    assert!(has(f.mods(), "OldAlt"));

    f.run(&store, &[MASTER, "KeptMaster"]).await; // OldAlt taken out, restart

    assert!(!has(f.mods(), "OldAlt"));
    assert!(!has(f.whitelist(), "OldAlt"));
    for file in f.roster_files() {
        assert!(!has(f.roster_path(&file), "OldAlt"), "{file}");
        assert!(has(f.roster_path(&file), "KeptMaster"), "{file}");
    }
    assert!(has(f.roster_path("policepatrolman.txt"), "PlayerOne"));
    let recorded: Vec<String> = store.read(MASTER_GRANTS, Vec::new()).unwrap();
    assert!(recorded.iter().any(|n| n == "KeptMaster"));
    assert!(!recorded.iter().any(|n| n == "OldAlt"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_removed_masters_live_menu_is_taken_on_the_wire() {
    let f = Fixture::new().await;
    let store = f.grant_store();
    store
        .write(MASTER_GRANTS, &vec!["OldAlt".to_owned()])
        .await
        .unwrap();

    let after = f.build(Arc::clone(&store), &[], true);
    after.start(None).await;
    let commands = f.commands_after_grant(3).await; // in the background: startup is not held up

    assert!(commands.iter().any(|c| c == "RemoveMenu OldAlt"));
    assert!(commands.iter().any(|c| c == "RemoveAccessManager OldAlt"));
    after.stop(Duration::from_secs(5)).await;
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unrecorded_master_left_on_every_roster_is_purged() {
    // Being on every roster is the record: nobody but a master is ever put there.
    let f = Fixture::new().await;
    std::fs::write(f.mods(), "OldMaster\n").unwrap();
    std::fs::write(f.whitelist(), "OldMaster\nPlayerOne\n").unwrap();
    for file in f.roster_files() {
        std::fs::write(f.roster_path(&file), "OldMaster\n").unwrap();
    }
    std::fs::write(
        f.roster_path("policepatrolman.txt"),
        "OldMaster\nPlayerOne\n",
    )
    .unwrap();

    f.run(&f.grant_store(), &[MASTER]).await;

    assert!(!has(f.mods(), "OldMaster"));
    let whitelist: Vec<String> = lines(f.whitelist())
        .into_iter()
        .filter(|l| !l.is_empty())
        .collect();
    assert_eq!(whitelist, ["PlayerOne", MASTER]);
    for file in f.roster_files() {
        assert!(!has(f.roster_path(&file), "OldMaster"), "{file}");
    }
    assert!(has(f.roster_path("policepatrolman.txt"), "PlayerOne"));
}

#[tokio::test(flavor = "multi_thread")]
async fn the_joined_up_name_from_a_space_is_purged_and_both_real_names_granted() {
    // MASTER_NAMES=fki6 Holosight1 used to be ONE name, written into every file.
    let f = Fixture::new().await;
    let joined = "fki6 Holosight1";
    std::fs::write(f.mods(), format!("{joined}\n")).unwrap();
    std::fs::write(f.whitelist(), format!("{joined}\n")).unwrap();
    for file in f.roster_files() {
        std::fs::write(f.roster_path(&file), format!("{joined}\n")).unwrap();
    }

    f.run(&f.grant_store(), &["fki6", "Holosight1"]).await;

    let mut paths = vec![PathBuf::from(f.mods()), PathBuf::from(f.whitelist())];
    paths.extend(f.roster_files().iter().map(|file| f.roster_path(file)));
    for path in paths {
        assert!(!has(&path, joined), "{}", path.display());
        assert!(has(&path, "fki6"), "{}", path.display());
        assert!(has(&path, "Holosight1"), "{}", path.display());
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_member_of_one_faction_is_never_taken_for_a_leftover() {
    let f = Fixture::new().await;
    std::fs::write(f.roster_path("policepatrolman.txt"), "PlayerOne\n").unwrap();

    f.run(&f.grant_store(), &[MASTER]).await;

    assert!(has(f.roster_path("policepatrolman.txt"), "PlayerOne"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_still_listed_master_is_left_alone() {
    let f = Fixture::new().await;
    let store = f.grant_store();
    f.run(&store, &[MASTER]).await;
    f.run(&store, &[MASTER]).await;

    assert!(has(f.mods(), MASTER));
    assert!(!f.server.commands().iter().any(|c| c.starts_with("Remove")));
}
