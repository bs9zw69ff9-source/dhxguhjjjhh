//! `/provisionserver` (C# `ProvisionServerCommand.cs`): install a new Pavlov server, configure
//! it and wire it into this bot. Owner only, one layout change at a time.
//!
//! - THE DISK DECIDES WHICH SLOT, not the count of `.env` entries: a configured slot with no
//!   install behind it is the slot to build, or a first server on a fresh box becomes a second.
//! - THE CONVENTION DECIDES THE UNIT AND THE DIRECTORY from the slot alone (server 1 is
//!   `pavlovserver`, server 2 `pavlovserver1`), so neither can be typed in and disagree.
//! - The checklist is posted to the channel and edited as each step lands; the work runs in
//!   the background and the layout gate is released only when it ends.

use std::collections::HashSet;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_config::{Config, Secret};
use pavlov_core::embed::Embed;
use pavlov_core::provisioning::commands::{parse_maps, unit_suffix};
use pavlov_core::provisioning::ports::{game_port, query_port_for, rcon_port};
use pavlov_core::provisioning::text::clean_server_name;
use pavlov_core::provisioning::validation::{MAX_SERVERS, MAX_SHACK_PLAYERS, check};
use pavlov_core::provisioning::{ServerLayout, ServerProvisionSpec, plan, target_slot};
use pavlov_core::servers::installs::DEFAULT_BASE;
use pavlov_core::servers::units::without_legacy_docker_prefix;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::layout_gate::ServerLayoutGate;
use pavlov_host::moderation::AuditLog;
use pavlov_host::provisioner::{ProvisionRequest, ProvisionStatus, ProvisionStep, Provisioner};

use crate::access::{Access, RequiredAccess};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::post::{ChannelPoster, PostEdit};
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the command is wired to.
pub struct ProvisionServerCommand {
    pub provisioner: Arc<dyn Provisioner>,
    pub layout: Arc<ServerLayoutGate>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    /// `.env` as loaded: the RCON slots, units and bases already configured.
    pub config: Arc<Config>,
    /// The RCON ports of the servers already configured.
    pub rcon_ports: Vec<i32>,
    pub default_capacity: i32,
    pub poster: Arc<dyn ChannelPoster>,
    pub env_path: PathBuf,
}

impl std::fmt::Debug for ProvisionServerCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ProvisionServerCommand")
            .finish_non_exhaustive()
    }
}

/// A comma-separated `.env` list, trimmed, empty entries dropped.
pub(crate) fn split_list(raw: Option<&str>) -> Vec<String> {
    raw.unwrap_or("")
        .split(',')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
        .collect()
}

/// `values` without the entry at 1-based `slot`, when there is one.
fn without<T: Clone>(values: &[T], slot: i32) -> Vec<T> {
    let skip = usize::try_from(slot).ok().and_then(|s| s.checked_sub(1));
    values
        .iter()
        .enumerate()
        .filter(|(i, _)| Some(*i) != skip)
        .map(|(_, v)| v.clone())
        .collect()
}

/// A strong RCON password from the operating system's random source: 20 characters of
/// `[A-Za-z0-9]`, by rejection sampling so no character is likelier than another. `None` only
/// when the random source cannot be read, which refuses the provision rather than weakening it.
fn generate_password() -> Option<String> {
    const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    let mut source = std::fs::File::open("/dev/urandom").ok()?;
    let mut out = String::with_capacity(20);
    let mut byte = [0u8; 1];
    while out.len() < 20 {
        source.read_exact(&mut byte).ok()?;
        // 248 is the largest multiple of 62 below 256.
        if byte[0] < 248 {
            out.push(char::from(ALPHABET[usize::from(byte[0] % 62)]));
        }
    }
    Some(out)
}

/// A checklist step's mark.
pub(crate) fn glyph(status: ProvisionStatus) -> &'static str {
    match status {
        ProvisionStatus::Ok => theme::OK,
        ProvisionStatus::Failed => theme::BAD,
        ProvisionStatus::Running => "⏳",
        ProvisionStatus::Skipped => "➖",
        ProvisionStatus::Pending => "⬜",
    }
}

/// The RCON slots `.env` configures (`RCON_HOST_n` set), in order.
pub(crate) fn used_rcon_indices(config: &Config) -> Vec<i32> {
    (1..=MAX_SERVERS)
        .filter(|i| {
            config
                .get(&format!("RCON_HOST_{i}"))
                .is_some_and(|h| !h.trim().is_empty())
        })
        .collect()
}

/// The live checklist.
#[must_use]
pub fn checklist(spec: &ServerProvisionSpec, steps: &[ProvisionStep]) -> Embed {
    let body: Vec<String> = steps
        .iter()
        .map(|s| {
            let line = format!("{} {}", glyph(s.status), s.name);
            if s.detail.is_empty() {
                line
            } else {
                format!("{line} — {}", Sanitize::code(Some(&s.detail)))
            }
        })
        .collect();
    let title = format!("Provisioning server {} ({})", spec.slot, spec.unit_name);
    let failed = steps.iter().any(|s| s.status == ProvisionStatus::Failed);
    let running = steps.iter().any(|s| {
        matches!(
            s.status,
            ProvisionStatus::Pending | ProvisionStatus::Running
        )
    });
    let body = body.join("\n");
    if failed && !running {
        theme::failure(&title, Some(&body))
    } else if running {
        theme::notice(&title, Some(&body))
    } else {
        theme::success(&title, Some(&body))
    }
}

fn started(
    spec: &ServerProvisionSpec,
    generated: bool,
    copy_from: Option<&str>,
    refilling: bool,
) -> Embed {
    let source = match copy_from {
        None => "SteamCMD downloads several GB, so this".to_owned(),
        Some(from) => format!(
            "Copying `{}` rather than downloading, so this",
            Sanitize::code(Some(from))
        ),
    };
    let mut embed = theme::notice(
        &format!("Provisioning server {} started", spec.slot),
        Some(&format!(
            "Installing **{}** as `{}`. {source} runs in the background - watch the checklist I just posted in \
             this channel. The bot restarts itself at the end.",
            Sanitize::code(Some(&spec.server_name)),
            spec.unit_name
        )),
    );
    if refilling {
        embed = embed.field(
            format!("{} Filling in an existing slot", theme::INFO),
            format!(
                "Server {} is already configured but has no install on disk, so this builds THAT rather than adding \
                 another. Its RCON settings are replaced.",
                spec.slot
            ),
            false,
        );
    }
    embed = embed
        .field(
            "Ports",
            format!(
                "game `{}/udp` • query `{}/udp` • rcon `{}/tcp`",
                spec.game_port, spec.query_port, spec.rcon_port
            ),
            false,
        )
        .field(
            "Install dir",
            format!("`{}`", Sanitize::code(Some(&spec.install_dir))),
            false,
        );
    if generated {
        embed = embed.field(
            "RCON password (generated - save it)",
            format!("||`{}`||", spec.rcon_password.expose()),
            false,
        );
    }
    if spec
        .server_key
        .as_ref()
        .is_some_and(|k| !k.expose().is_empty())
    {
        embed = embed.field(
            "Server key",
            "written to Game.ini as `ServerKey=` (value not shown)",
            false,
        );
    }
    embed
}

impl ProvisionServerCommand {
    fn install_parent(&self) -> String {
        let first = self
            .config
            .get("PAVLOV_BASE_1")
            .map(str::trim)
            .filter(|f| !f.is_empty());
        let root = first.map_or(DEFAULT_BASE.to_owned(), |f| {
            f.trim_end_matches('/').to_owned()
        });
        Path::new(&root)
            .parent()
            .map(|p| p.display().to_string())
            .filter(|p| !p.is_empty())
            .unwrap_or_else(|| "/home/steam".to_owned())
    }

    fn used_rcon_indices(&self) -> Vec<i32> {
        used_rcon_indices(&self.config)
    }

    fn installed_slots(&self, configured: i32) -> HashSet<i32> {
        let parent = self.install_parent();
        (1..=configured)
            .filter(|slot| {
                Path::new(&parent)
                    .join(format!("pavlovserver{}", unit_suffix(*slot)))
                    .join("PavlovServer.sh")
                    .is_file()
            })
            .collect()
    }

    /// Another slot's RCON host, else this machine's own address, else loopback.
    fn rcon_host_for(&self, building: i32) -> String {
        (1..=MAX_SERVERS)
            .filter(|i| *i != building)
            .find_map(|i| {
                self.config
                    .get(&format!("RCON_HOST_{i}"))
                    .map(str::trim)
                    .filter(|h| !h.is_empty())
                    .map(str::to_owned)
            })
            .or_else(local_address)
            .unwrap_or_else(|| "127.0.0.1".to_owned())
    }

    async fn start(&self, context: &CommandContext<'_>) -> Result<bool, CommandError> {
        let invocation = context.invocation;
        let used = self.used_rcon_indices();
        let configured = i32::try_from(used.len()).unwrap_or(i32::MAX);
        let units: Vec<String> = split_list(self.config.get("PAVLOV_UNITS"))
            .iter()
            .map(|u| without_legacy_docker_prefix(u).to_owned())
            .collect();
        let bases = split_list(self.config.get("PAVLOV_BASES"));
        let slot = target_slot(configured, &self.installed_slots(configured));
        let refilling = slot <= configured;
        let unit = format!("pavlovserver{}", unit_suffix(slot));
        let install_dir = format!("{}/{unit}", self.install_parent());
        let avoid = if refilling { slot } else { 0 };
        let rcon_ports: HashSet<i32> = without(&self.rcon_ports, avoid).into_iter().collect();

        let supplied = invocation.string("rconpassword").filter(|p| !p.is_empty());
        let generated = supplied.is_none();
        let Some(password) = supplied.map(str::to_owned).or_else(generate_password) else {
            context
                .reply(Reply::embed(
                    theme::failure(
                        "Cannot provision",
                        Some("A strong RCON password could not be generated."),
                    )
                    .brand(None),
                ))
                .await?;
            return Ok(false);
        };
        let narrow = |v: i64| i32::try_from(v).unwrap_or(-1);
        let game = invocation
            .integer("gameport")
            .map_or_else(|| game_port(slot), narrow);
        let spec = ServerProvisionSpec {
            slot,
            unit_name: unit.clone(),
            install_dir: install_dir.clone(),
            server_name: clean_server_name(invocation.string("name")),
            game_port: game,
            query_port: invocation
                .integer("queryport")
                .map_or_else(|| query_port_for(game), narrow),
            rcon_port: invocation
                .integer("rconport")
                .map_or_else(|| rcon_port(slot), narrow),
            rcon_password: Secret::new(password),
            max_players: invocation
                .integer("maxplayers")
                .map_or(self.default_capacity, narrow),
            maps: parse_maps(invocation.string("maps")),
            game_password: invocation.string("gamepassword").map(Secret::new),
            server_key: invocation.string("serverkey").map(Secret::new),
        };
        let layout = ServerLayout {
            rcon_indices: used.clone(),
            units: units.clone(),
            bases: bases.clone(),
        };
        let mut problems = Vec::new();
        let planned = match plan(&layout, slot, &unit, &install_dir, MAX_SERVERS) {
            Ok(planned) => Some(planned),
            Err(found) => {
                problems.extend(found);
                None
            }
        };
        problems.extend(check(
            &spec,
            &rcon_ports,
            &without(&units, avoid),
            &without(&bases, avoid),
        ));
        let Some(planned) = planned.filter(|_| problems.is_empty()) else {
            let lines: Vec<String> = problems
                .iter()
                .map(|p| format!("{} {p}", theme::DOT))
                .collect();
            context
                .reply(Reply::embed(
                    theme::failure(
                        &format!("Cannot provision server {slot}"),
                        Some(&lines.join("\n")),
                    )
                    .brand(None),
                ))
                .await?;
            return Ok(false);
        };
        let spec = ServerProvisionSpec {
            slot: planned.slot,
            ..spec
        };
        let detail = format!(
            "slot {}, ports {}/{}/{}, dir {}",
            spec.slot, spec.game_port, spec.query_port, spec.rcon_port, spec.install_dir
        );
        self.audit
            .record(
                "provisionserver",
                &invocation.username,
                &spec.unit_name,
                Some(&detail),
            )
            .await?;
        tracing::warn!(user = %invocation.username, slot = spec.slot, unit = %spec.unit_name, dir = %spec.install_dir, "PROVISIONSERVER");
        let copy_from = (invocation.boolean("copy") == Some(true))
            .then(|| format!("{}/pavlovserver{}", self.install_parent(), unit_suffix(1)));
        context
            .reply(Reply::embed(
                started(&spec, generated, copy_from.as_deref(), refilling).brand(None),
            ))
            .await?;

        let mut request = ProvisionRequest::new(
            spec.clone(),
            planned.final_units,
            planned.final_bases,
            None,
            self.env_path.clone(),
        );
        request.rcon_host = self.rcon_host_for(slot);
        request.copy_from_install_dir = copy_from;
        request.rebuilding_existing_slot = refilling;

        let channel = invocation.channel_id;
        let first = vec![ProvisionStep {
            name: "Starting…".into(),
            status: ProvisionStatus::Running,
            detail: String::new(),
        }];
        let message = self
            .poster
            .send(channel, checklist(&spec, &first), Vec::new())
            .await
            .ok()
            .flatten();
        let (provisioner, layout, poster) = (
            Arc::clone(&self.provisioner),
            Arc::clone(&self.layout),
            Arc::clone(&self.poster),
        );
        tokio::spawn(async move {
            let progress_spec = spec.clone();
            let progress_poster = Arc::clone(&poster);
            let progress: pavlov_host::provisioner::Progress =
                Arc::new(move |steps: Vec<ProvisionStep>| {
                    let (spec, poster) = (progress_spec.clone(), Arc::clone(&progress_poster));
                    Box::pin(async move {
                        let embed = checklist(&spec, &steps);
                        match message {
                            Some(id) => {
                                if poster.edit(channel, id, embed, Vec::new()).await
                                    != PostEdit::Edited
                                {
                                    tracing::warn!("could not update the provisioning checklist");
                                }
                            }
                            None => {
                                let _ = poster.send(channel, embed, Vec::new()).await;
                            }
                        }
                    })
                });
            let outcome = provisioner.provision(&request, progress).await;
            // The gate stays shut until the restart when the run changed `.env`.
            layout.exit(Some(&outcome));
        });
        Ok(true)
    }
}

/// This machine's outward address, from the route a UDP socket would take (nothing is sent).
#[must_use]
pub fn local_address() -> Option<String> {
    let socket = std::net::UdpSocket::bind("0.0.0.0:0").ok()?;
    socket.connect("8.8.8.8:65530").ok()?;
    socket.local_addr().ok().map(|a| a.ip().to_string())
}

impl SlashCommand for ProvisionServerCommand {
    fn name(&self) -> &str {
        "provisionserver"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        build_spec()
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            if !self.access.allows(RequiredAccess::Owner, caller) {
                let refusal = self.access.explain_refusal(caller, RequiredAccess::Owner);
                return context
                    .reply(Reply::embed(
                        theme::denied("Not allowed", Some(&refusal)).brand(None),
                    ))
                    .await;
            }
            if let Some(busy) = self.layout.try_enter("provisioning a server") {
                return context
                    .reply(Reply::embed(
                        theme::failure("Cannot provision a server", Some(&busy)).brand(None),
                    ))
                    .await;
            }
            let handed_off = self.start(&context).await;
            if !matches!(handed_off, Ok(true)) {
                self.layout.abandon();
            }
            handed_off.map(|_| ())
        })
    }
}

/// The command's definition. It touches no dependency (the C# test built it with none).
#[must_use]
pub fn build_spec() -> CommandSpec {
    let port = |name: &str, description: &str| {
        OptionSpec::new(OptionKind::Integer, name, description).range(Some(1.0), Some(65_535.0))
    };
    let text =
        |name: &str, description: &str| OptionSpec::new(OptionKind::String, name, description);
    CommandSpec::new(
        "provisionserver",
        "Owner - Install a new Pavlov server, configure it and wire it into this bot (needs root)",
    )
    .option(text("name", "Public server name").required())
    .option(port(
        "gameport",
        "Game port (UDP; defaults step per server: 7777, 7778, 7779...)",
    ))
    .option(port(
        "queryport",
        "Query/beacon port (UDP; defaults to the game port + 400)",
    ))
    .option(port(
        "rconport",
        "RCON port (TCP; defaults step per server: 9100, 9200, 9300...)",
    ))
    .option(
        OptionSpec::new(
            OptionKind::Integer,
            "maxplayers",
            "Max players (default 24 - Shack's hard cap)",
        )
        .range(Some(1.0), Some(f64::from(MAX_SHACK_PLAYERS))),
    )
    .option(text(
        "maps",
        "Rotation as MapId:Mode,MapId:Mode (default: this network's map)",
    ))
    .option(OptionSpec::new(
        OptionKind::Boolean,
        "copy",
        "Copy server 1's install instead of downloading it with SteamCMD",
    ))
    .option(text(
        "rconpassword",
        "RCON password (default: a strong one is generated)",
    ))
    .option(text(
        "gamepassword",
        "Optional join password (default: open)",
    ))
    .option(
        text(
            "serverkey",
            "Optional server key, written to Game.ini as ServerKey=",
        )
        .length(None, Some(128)),
    )
}
