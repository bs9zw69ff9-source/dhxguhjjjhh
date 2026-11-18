//! `/deleteserver` (C# `DeleteServerCommand.cs`): delete a server's service, install directory
//! and slot. Owner only, confirmed, one layout change at a time.
//!
//! - ONLY THE HIGHEST server can go: servers are positional (server N is the Nth unit), so
//!   removing one from the middle re-points the rest at the wrong servers.
//! - The LAST one may go: the bot starts with no RCON servers and `/provisionserver` builds from
//!   nothing.
//! - The option's range starts at 1 so the REASON reaches the operator, rather than Discord's
//!   own generic "enter a number between 2 and 9".

use std::path::PathBuf;
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_config::Config;
use pavlov_core::embed::Embed;
use pavlov_core::provisioning::validation::MAX_SERVERS;
use pavlov_core::servers::units::without_legacy_docker_prefix;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::layout_gate::ServerLayoutGate;
use pavlov_host::moderation::AuditLog;
use pavlov_host::provisioner::{DeleteRequest, ProvisionStatus, ProvisionStep, Provisioner};

use crate::access::{Access, RequiredAccess};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::commands::provision_server::{glyph, split_list, used_rcon_indices};
use crate::interaction::Reply;
use crate::post::{ChannelPoster, PostEdit};
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Why `slot` cannot be deleted, or `None` when it can.
#[must_use]
pub fn problem(
    slot: i32,
    used_rcon_indices: &[i32],
    units: &[String],
    bases: &[String],
) -> Option<String> {
    let count = used_rcon_indices.len();
    let slot_index = usize::try_from(slot).unwrap_or(0);
    if slot_index > count {
        return Some(format!(
            "there is no server {slot} - this box has {count} configured. Nothing was deleted."
        ));
    }
    if slot_index != count {
        return Some(format!(
            "only server {count} can be deleted. Servers are positional - the unit for server N is the Nth entry of \
             PAVLOV_UNITS - so removing server {slot} would re-point the rest at the wrong servers.\nDelete from the \
             top down: server {count} first."
        ));
    }
    if units.len() != count || bases.len() != count {
        return Some(format!(
            "PAVLOV_UNITS and PAVLOV_BASES must each list exactly {count} entries before a server can be removed \
             (they list {} and {}). Set them explicitly first.",
            units.len(),
            bases.len()
        ));
    }
    None
}

/// The live checklist.
#[must_use]
pub fn checklist(slot: i32, unit: &str, steps: &[ProvisionStep]) -> Embed {
    let body = if steps.is_empty() {
        "Starting…".to_owned()
    } else {
        steps
            .iter()
            .map(|s| {
                let line = format!("{} {}", glyph(s.status), s.name);
                if s.detail.is_empty() {
                    line
                } else {
                    format!("{line} — {}", Sanitize::code(Some(&s.detail)))
                }
            })
            .collect::<Vec<_>>()
            .join("\n")
    };
    let title = format!("Deleting server {slot} ({unit})");
    let failed = steps.iter().any(|s| s.status == ProvisionStatus::Failed);
    let running = steps.is_empty()
        || steps.iter().any(|s| {
            matches!(
                s.status,
                ProvisionStatus::Pending | ProvisionStatus::Running
            )
        });
    if failed && !running {
        theme::failure(&title, Some(&body))
    } else if running {
        theme::warning(&title, Some(&body))
    } else {
        theme::success(&title, Some(&body))
    }
}

fn without<T: Clone>(values: &[T], slot: i32) -> Vec<T> {
    let skip = usize::try_from(slot).ok().and_then(|s| s.checked_sub(1));
    values
        .iter()
        .enumerate()
        .filter(|(i, _)| Some(*i) != skip)
        .map(|(_, v)| v.clone())
        .collect()
}

/// What the command is wired to.
pub struct DeleteServerCommand {
    pub provisioner: Arc<dyn Provisioner>,
    pub layout: Arc<ServerLayoutGate>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    pub config: Arc<Config>,
    pub poster: Arc<dyn ChannelPoster>,
    pub env_path: PathBuf,
}

impl std::fmt::Debug for DeleteServerCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DeleteServerCommand")
            .finish_non_exhaustive()
    }
}

impl DeleteServerCommand {
    async fn start(&self, context: &CommandContext<'_>, slot: i32) -> Result<bool, CommandError> {
        let used = used_rcon_indices(&self.config);
        let units: Vec<String> = split_list(self.config.get("PAVLOV_UNITS"))
            .iter()
            .map(|u| without_legacy_docker_prefix(u).to_owned())
            .collect();
        let bases = split_list(self.config.get("PAVLOV_BASES"));
        if let Some(problem) = problem(slot, &used, &units, &bases) {
            context
                .reply(Reply::embed(
                    theme::failure(&format!("Cannot delete server {slot}"), Some(&problem))
                        .brand(None),
                ))
                .await?;
            return Ok(false);
        }
        let index = usize::try_from(slot - 1).unwrap_or(0);
        let (unit, install_dir) = (units[index].clone(), bases[index].clone());
        let who = &context.invocation.username;
        self.audit
            .record(
                "deleteserver",
                who,
                &unit,
                Some(&format!("slot {slot}, dir {install_dir}")),
            )
            .await?;
        tracing::warn!(user = %who, slot, unit = %unit, dir = %install_dir, "DELETESERVER");
        context
            .reply(Reply::embed(
                theme::warning(
                    &format!("Deleting server {slot}"),
                    Some(&format!(
                        "Removing `{}` and erasing `{}`. Watch the checklist in this channel; the bot restarts itself \
                         at the end.",
                        Sanitize::code(Some(&unit)),
                        Sanitize::code(Some(&install_dir))
                    )),
                )
                .brand(None),
            ))
            .await?;
        let request = DeleteRequest {
            slot,
            unit_name: unit.clone(),
            install_dir,
            final_pavlov_units: without(&units, slot),
            final_pavlov_bases: without(&bases, slot),
            env_path: self.env_path.clone(),
        };
        let channel = context.invocation.channel_id;
        let message = self
            .poster
            .send(channel, checklist(slot, &unit, &[]), Vec::new())
            .await
            .ok()
            .flatten();
        let (provisioner, layout, poster) = (
            Arc::clone(&self.provisioner),
            Arc::clone(&self.layout),
            Arc::clone(&self.poster),
        );
        tokio::spawn(async move {
            let (progress_poster, progress_unit) = (Arc::clone(&poster), unit.clone());
            let progress: pavlov_host::provisioner::Progress =
                Arc::new(move |steps: Vec<ProvisionStep>| {
                    let (poster, unit) = (Arc::clone(&progress_poster), progress_unit.clone());
                    Box::pin(async move {
                        let embed = checklist(slot, &unit, &steps);
                        match message {
                            Some(id) => {
                                if poster.edit(channel, id, embed, Vec::new()).await
                                    != PostEdit::Edited
                                {
                                    tracing::warn!("could not update the deletion checklist");
                                }
                            }
                            None => {
                                let _ = poster.send(channel, embed, Vec::new()).await;
                            }
                        }
                    })
                });
            let outcome = provisioner.delete(&request, progress).await;
            // Deleting the last server skips the restart on purpose (the gate decides).
            layout.exit(Some(&outcome));
        });
        Ok(true)
    }
}

impl SlashCommand for DeleteServerCommand {
    fn name(&self) -> &str {
        "deleteserver"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "deleteserver",
            "Owner - Delete a server: its service, its install directory and its slot (needs root)",
        )
        .option(
            OptionSpec::new(
                OptionKind::Integer,
                "server",
                "Which server number to delete (the highest one only)",
            )
            .range(Some(1.0), Some(f64::from(MAX_SERVERS)))
            .required(),
        )
        .option(
            OptionSpec::new(
                OptionKind::Boolean,
                "confirm",
                "Yes, delete it - the install directory is erased and does not come back",
            )
            .required(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let caller = &invocation.caller;
            if !self.access.allows(RequiredAccess::Owner, caller) {
                let refusal = self.access.explain_refusal(caller, RequiredAccess::Owner);
                return context
                    .reply(Reply::embed(
                        theme::denied("Not allowed", Some(&refusal)).brand(None),
                    ))
                    .await;
            }
            let slot = invocation
                .integer("server")
                .and_then(|s| i32::try_from(s).ok())
                .unwrap_or(0);
            if invocation.boolean("confirm") != Some(true) {
                return context
                    .reply(Reply::embed(
                        theme::notice(
                            "Nothing deleted",
                            Some("Set `confirm` to True if you really mean it - this erases the install directory."),
                        )
                        .brand(None),
                    ))
                    .await;
            }
            if let Some(busy) = self.layout.try_enter(&format!("deleting server {slot}")) {
                return context
                    .reply(Reply::embed(
                        theme::failure(&format!("Cannot delete server {slot}"), Some(&busy))
                            .brand(None),
                    ))
                    .await;
            }
            let handed_off = self.start(&context, slot).await;
            if !matches!(handed_off, Ok(true)) {
                self.layout.abandon();
            }
            handed_off.map(|_| ())
        })
    }
}
