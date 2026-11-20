//! `/setpin`, `/removepin`, `/iplookup` and `/vpncheck` (C# `AdminCommands.cs`).
//!
//! - A generated PIN comes from the OS random source: a predictable PIN is no lock. The PIN
//!   is NEVER logged, since a log line containing it defeats the lock for whoever reads it.
//! - `SetPin` is VERIFIED, so "Locked" means locked; clearing sends a BARE `SetPin`, because
//!   `SetPin 0` sets the PIN to zero and the server stays locked.
//! - An `/iplookup` with no verdict is NOT clean: it means nothing could be determined.

use std::io::Read;
use std::net::IpAddr;
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::vpn::{VpnRecord, VpnStance, verdict};
use pavlov_host::registry::RconRegistry;
use pavlov_host::vpn::{DetectorProbe, StaticMap, VpnScreeningService};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::{FileUpload, Reply};
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The address `/vpncheck` probes with by default.
const DEFAULT_PROBE: &str = "8.8.8.8";
/// The file name the location map is attached under.
const MAP_FILE: &str = "location.png";

fn refuse(access: &Access, caller: &Caller, required: RequiredAccess) -> Option<Embed> {
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

fn server_option(rcon: &RconRegistry, description: &str) -> OptionSpec {
    let servers = rcon.servers();
    let mut option = servers.iter().fold(
        OptionSpec::new(OptionKind::String, "server", description).required(),
        |option, name| option.choice(Choice::string(name.clone(), name.clone())),
    );
    if servers.len() > 1 {
        option = option.choice(Choice::string("All servers", "all"));
    }
    option
}

fn targets(rcon: &RconRegistry, target: &str) -> Vec<String> {
    if target == "all" {
        rcon.servers()
    } else {
        vec![target.to_owned()]
    }
}

/// 1 to 4 digits, no leading zero: Pavlov treats the PIN as a number.
#[must_use]
pub fn is_valid_pin(pin: &str) -> bool {
    let bytes = pin.as_bytes();
    (1..=4).contains(&bytes.len()) && bytes.iter().all(u8::is_ascii_digit) && bytes[0] != b'0'
}

/// A PIN in 1000..=9999 from the OS random source, by rejection sampling. `None` only when the
/// source cannot be read, which refuses the lock rather than weakening it.
#[must_use]
pub fn generate_pin() -> Option<String> {
    let mut source = std::fs::File::open("/dev/urandom").ok()?;
    let mut bytes = [0u8; 2];
    loop {
        source.read_exact(&mut bytes).ok()?;
        let value = u16::from_le_bytes(bytes);
        // 63000 is the largest multiple of 9000 below 65536.
        if value < 63_000 {
            return Some((1000 + value % 9000).to_string());
        }
    }
}

// ---- /setpin ----

pub struct SetPinCommand {
    pub rcon: Arc<RconRegistry>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for SetPinCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SetPinCommand").finish_non_exhaustive()
    }
}

impl SetPinCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Embed {
        let invocation = context.invocation;
        let target = invocation.string("server").unwrap_or("");
        let supplied = invocation
            .string("pin")
            .map(str::trim)
            .filter(|p| !p.is_empty());
        let pin = match supplied {
            Some(pin) if !is_valid_pin(pin) => {
                return theme::failure(
                    "Invalid PIN",
                    Some("Use **1 to 4 digits, no leading zero**."),
                );
            }
            Some(pin) => pin.to_owned(),
            None => match generate_pin() {
                Some(pin) => pin,
                None => {
                    return theme::failure(
                        "Could not lock the server",
                        Some("A PIN could not be generated."),
                    );
                }
            },
        };
        let mut applied = Vec::new();
        for server in targets(&self.rcon, target) {
            match self
                .rcon
                .send_verified(&server, &format!("SetPin {pin}"))
                .await
            {
                Ok(_) => applied.push(server),
                Err(error) => tracing::warn!(%server, %error, "SetPin failed"),
            }
        }
        tracing::info!(servers = %applied.join(","), by = %invocation.username, "setpin");
        if applied.is_empty() {
            theme::failure(
                "Could not lock the server",
                Some("No server accepted the command."),
            )
        } else {
            theme::success(
                "Locked",
                Some(&format!(
                    "**{}** now requires the PIN **{pin}**.",
                    applied.join(", ")
                )),
            )
        }
    }
}

impl SlashCommand for SetPinCommand {
    fn name(&self) -> &str {
        "setpin"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("setpin", "Admin - Lock a server behind a join PIN")
            .option(server_option(&self.rcon, "Which server to lock"))
            .option(OptionSpec::new(
                OptionKind::String,
                "pin",
                "1-4 digits, no leading zero. Leave blank to generate one.",
            ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(
                &self.access,
                &context.invocation.caller,
                RequiredAccess::Admin,
            ) {
                Some(refusal) => refusal,
                None => self.run(&context).await,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /removepin ----

pub struct RemovePinCommand {
    pub rcon: Arc<RconRegistry>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for RemovePinCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RemovePinCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for RemovePinCommand {
    fn name(&self) -> &str {
        "removepin"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("removepin", "Admin - Clear a server's join PIN")
            .option(server_option(&self.rcon, "Which server to unlock"))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = if let Some(refusal) =
                refuse(&self.access, &invocation.caller, RequiredAccess::Admin)
            {
                refusal
            } else {
                let mut cleared = Vec::new();
                for server in targets(&self.rcon, invocation.string("server").unwrap_or("")) {
                    // BARE SetPin: "SetPin 0" sets the PIN to zero and the server stays locked.
                    match self.rcon.send(&server, "SetPin").await {
                        Ok(_) => cleared.push(server),
                        Err(error) => tracing::warn!(%server, %error, "SetPin (clear) failed"),
                    }
                }
                tracing::info!(servers = %cleared.join(","), by = %invocation.username, "removepin");
                if cleared.is_empty() {
                    theme::failure(
                        "Could not unlock the server",
                        Some("No server accepted the command."),
                    )
                } else {
                    theme::success(
                        "Unlocked",
                        Some(&format!(
                            "**{}** no longer requires a PIN.",
                            cleared.join(", ")
                        )),
                    )
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /iplookup ----

pub struct IpLookupCommand {
    pub vpn: Arc<VpnScreeningService>,
    pub access: Arc<Access>,
    pub map: Option<Arc<StaticMap>>,
}

impl std::fmt::Debug for IpLookupCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("IpLookupCommand").finish_non_exhaustive()
    }
}

/// The verdict line: the headline, the plain sentence and the signals.
#[must_use]
pub fn verdict_line(record: &VpnRecord) -> String {
    let summary = verdict::of(Some(record));
    let mark = match summary.stance {
        VpnStance::Confirmed => theme::BAD,
        VpnStance::Disputed | VpnStance::Likely => theme::WARN,
        VpnStance::Clean => theme::OK,
        _ => theme::DOT,
    };
    let line = format!("{mark} **{}**\n{}", summary.headline, summary.plain);
    let signals = verdict::signals(Some(record));
    if signals.is_empty() {
        line
    } else {
        format!("{line}\nSignals: **{}**", signals.join(", "))
    }
}

/// The lookup embed, without the map.
#[must_use]
pub fn lookup_embed(ip: &str, record: &VpnRecord) -> Embed {
    let location: Vec<&str> = [&record.city, &record.region, &record.country]
        .into_iter()
        .filter_map(|p| p.as_deref().filter(|s| !s.is_empty()))
        .collect();
    let mut embed = theme::notice(&format!("IP lookup — {ip}"), None)
        .field(
            "Location",
            if location.is_empty() {
                "unknown".into()
            } else {
                location.join(", ")
            },
            true,
        )
        .field(
            "ISP",
            record
                .isp
                .clone()
                .or_else(|| record.organization.clone())
                .unwrap_or_else(|| "unknown".into()),
            true,
        )
        .field("VPN / proxy", verdict_line(record), true);
    if let Some(provider) = record.provider.as_deref().filter(|p| !p.is_empty()) {
        embed = embed.field("Provider", provider.to_owned(), true);
    }
    if let Some(asn) = record.asn.as_deref().filter(|a| !a.is_empty()) {
        embed = embed.field("ASN", asn.to_owned(), true);
    }
    if let Some(score) = record.threat_score {
        embed = embed.field("Risk", format!("{score:.0}/100"), true);
    }
    embed.brand(Some(&format!(
        "Screened {} by {} detector(s)",
        record.checked_at.format("%Y-%m-%d"),
        record.sources.len()
    )))
}

impl IpLookupCommand {
    async fn run(&self, raw: &str) -> Reply {
        let ip = raw.trim();
        if ip.parse::<IpAddr>().is_err() {
            return Reply::embed(
                theme::failure(
                    "Not an IP address",
                    Some(&format!(
                        "`{}` could not be parsed.",
                        Sanitize::code(Some(ip))
                    )),
                )
                .brand(None),
            );
        }
        let Some(record) = self.vpn.check(ip).await else {
            return Reply::embed(
                theme::warning(
                    "No verdict",
                    Some(
                        "Every detector failed or is unconfigured. This is **not** a clean result.",
                    ),
                )
                .brand(None),
            );
        };
        let embed = lookup_embed(ip, &record);
        // The map is a bonus: any failure just means no picture, and a private address has
        // no place on one.
        let image = match (&self.map, record.local, record.latitude, record.longitude) {
            (Some(map), false, Some(lat), Some(lon)) => map.render(lat, lon).await,
            _ => None,
        };
        match image {
            // attachment:// points at the file in this message, so the map key never leaves.
            Some(bytes) => Reply {
                embeds: vec![embed.image(format!("attachment://{MAP_FILE}"))],
                files: vec![FileUpload {
                    filename: MAP_FILE.into(),
                    bytes,
                }],
                ..Reply::default()
            },
            None => Reply::embed(embed),
        }
    }
}

impl SlashCommand for IpLookupCommand {
    fn name(&self) -> &str {
        "iplookup"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "iplookup",
            "Owner - Look up an IP: location, ISP and VPN status",
        )
        .option(OptionSpec::new(OptionKind::String, "ip", "The address to look up").required())
        .option(OptionSpec::new(
            OptionKind::Boolean,
            "refresh",
            "Re-screen instead of using the cached verdict",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let reply = match refuse(&self.access, &invocation.caller, RequiredAccess::Owner) {
                Some(refusal) => Reply::embed(refusal.brand(None)),
                None => self.run(invocation.string("ip").unwrap_or("")).await,
            };
            context.reply(reply).await
        })
    }
}

// ---- /vpncheck ----

pub struct VpnCheckCommand {
    pub vpn: Arc<VpnScreeningService>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for VpnCheckCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("VpnCheckCommand").finish_non_exhaustive()
    }
}

/// Three states, not two: never asked, answered, or failed WITH THE REASON (a bad key, a spent
/// quota, a timeout and a blocked port are four different fixes).
#[must_use]
pub fn probe_report(ip: &str, probes: &[DetectorProbe], warning: Option<&str>) -> Embed {
    let mut sorted: Vec<&DetectorProbe> = probes.iter().collect();
    sorted.sort_by(|a, b| a.tier.cmp(&b.tier).then_with(|| a.name.cmp(&b.name)));
    let lines: Vec<String> = sorted
        .iter()
        .map(|p| {
            let mark = if !p.configured {
                theme::DOT
            } else if p.answered {
                theme::OK
            } else {
                theme::BAD
            };
            let took = if p.configured {
                format!(" ({:.0}ms)", p.duration_ms)
            } else {
                String::new()
            };
            format!(
                "{mark} **{}** — {}\n{} {}{took}",
                p.name,
                p.role(),
                theme::DOT,
                p.outcome()
            )
        })
        .collect();
    let answered = probes.iter().filter(|p| p.answered).count();
    let configured = probes.iter().filter(|p| p.configured).count();
    let mut embed = theme::notice(
        &format!("VPN detectors — probed with {ip}"),
        Some(&format!(
            "**{answered}** of **{configured}** configured detector(s) returned a verdict.\n\n{}",
            lines.join("\n")
        )),
    );
    if let Some(warning) = warning {
        embed = embed.field(
            format!("{} Configuration", theme::WARN),
            warning.to_owned(),
            false,
        );
    }
    embed
}

impl SlashCommand for VpnCheckCommand {
    fn name(&self) -> &str {
        "vpncheck"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "vpncheck",
            "Owner - Probe every VPN detector: which are up and each one's role",
        )
        .option(OptionSpec::new(
            OptionKind::String,
            "ip",
            "Address to probe with (default 8.8.8.8)",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = if let Some(refusal) =
                refuse(&self.access, &invocation.caller, RequiredAccess::Owner)
            {
                refusal
            } else {
                let ip = invocation
                    .string("ip")
                    .map(str::trim)
                    .filter(|i| !i.is_empty())
                    .unwrap_or(DEFAULT_PROBE);
                // One lookup per configured detector, which is why this is owner-gated.
                let probes = self.vpn.probe(ip).await;
                if probes.is_empty() {
                    theme::warning(
                        "No detectors configured",
                        Some("VPN screening is off. Set at least one API key."),
                    )
                } else {
                    probe_report(ip, &probes, self.vpn.configuration_warning().as_deref())
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
