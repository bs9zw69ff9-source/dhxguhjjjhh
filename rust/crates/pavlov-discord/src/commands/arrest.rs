//! `/arrest` and its booking picker (C# `ArrestCommand` in `PoliceCommands.cs` and
//! `ArrestBooking.cs`): an officer's own time, the receipt line each charge gets, the picker,
//! and what recording an arrest means.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};

use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::penal::{BailRule, Booking, Charge, PenalCode};
use pavlov_core::police::{Arrest, PoliceConfig, Warrant};
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::Clock;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::{ARRESTS, POLICE_CONFIG, WARRANTS};

use crate::access::{Access, RequiredAccess};
use crate::command::CommandSpec;
use crate::component::{
    ActionRow, Button, ButtonStyle, Modal, SelectMenu, SelectOption, TextInput,
};
use crate::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply, Responder};
use crate::paged::ComponentHandler;
use crate::prefixes::ARREST_BOOKING;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// An officer's time instead of the computed one. HELD TO THE CAP like any other sentence:
/// an officer wanting longer needs the cap raised, which is a deliberate policy act. Bail and
/// bailability are left alone, so an override never makes a non-bailable booking payable.
#[must_use]
pub fn override_time(booking: &Booking, minutes: i32, cap_minutes: i32) -> Booking {
    let wanted = minutes.max(0);
    let capped = cap_minutes > 0 && wanted > cap_minutes;
    Booking {
        jail_minutes: if capped { cap_minutes } else { wanted },
        capped,
        cap_minutes: capped.then_some(cap_minutes),
        ..booking.clone()
    }
}

/// `2 min, $10` / `3 min, no bail` / `no jail, $50`: the price, or that there is none - the
/// difference between "free" and "you cannot pay this" is the whole point of the receipt.
#[must_use]
pub fn charge_line(charge: &Charge, rate: f64) -> String {
    let sentence = if charge.sentence_ranges {
        "ranges with the associated charge".to_owned()
    } else if charge.jail_minutes > 0 {
        format!("{} min", charge.jail_minutes)
    } else {
        "no jail".to_owned()
    };
    let price = match charge.bail_at(rate) {
        Some(bail) => lore::amount(i64::from(bail)),
        None if charge.rule == BailRule::Associated => "bail from the associated charge".to_owned(),
        None => "no bail".to_owned(),
    };
    format!("{sentence}, {price}")
}

// ---- recording ----

/// What an arrest MEANS: the record, the warrant it satisfies, the audit entry, the receipt.
/// Shared by the picker's confirm and anything else that books, so the two cannot drift.
pub struct ArrestRecorder {
    pub store: Arc<SerializedStore>,
    pub audit: Arc<AuditLog>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for ArrestRecorder {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ArrestRecorder").finish_non_exhaustive()
    }
}

impl ArrestRecorder {
    /// Record `booking` against `player` and build the receipt.
    ///
    /// # Errors
    /// When the store or the audit log fails.
    pub async fn record(
        &self,
        player: &str,
        booking: &Booking,
        officer: &str,
        rate: f64,
    ) -> Result<Embed, CommandError> {
        // A non-bailable booking records NO bail figure: the shared record has no field for
        // "cannot be bailed", and a sum would read as a payable price.
        let arrest = Arrest {
            player: player.to_owned(),
            codes: booking.charges.iter().map(|c| c.code.to_owned()).collect(),
            jail_minutes: booking.jail_minutes,
            bail: if booking.bailable { booking.bail } else { 0 },
            arrested_by: officer.to_owned(),
            at: self.clock.now(),
        };
        let codes = arrest.codes.join(",");
        self.store
            .update(
                ARRESTS,
                IgnoreCaseMap::new,
                |mut arrests: IgnoreCaseMap<Vec<Arrest>>| {
                    let mut list = arrests.remove(player).unwrap_or_default();
                    list.push(arrest);
                    arrests.insert(player, list);
                    Some(arrests)
                },
            )
            .await?;
        // An arrest satisfies the warrant that prompted it.
        self.store
            .update(
                WARRANTS,
                IgnoreCaseMap::new,
                |mut warrants: IgnoreCaseMap<Vec<Warrant>>| {
                    warrants.remove(player);
                    Some(warrants)
                },
            )
            .await?;
        self.audit
            .record("arrest", officer, player, Some(&codes))
            .await?;
        tracing::info!(player, codes = %codes, minutes = booking.jail_minutes, bail = booking.bail, by = officer, "arrest");
        let lines: Vec<String> = booking
            .charges
            .iter()
            .map(|c| format!("`{}` {} — {}", c.code, c.name, charge_line(c, rate)))
            .collect();
        let words = &police().words;
        let mut embed = theme::punishment(
            &format!(
                "{} Booked — {}",
                words.jail_glyph,
                Sanitize::code(Some(player))
            ),
            Some(&lines.join("\n")),
        )
        .field("Sentence", booking.sentence_label(), true)
        .field("Bail", booking.bail_label(&words.money), true)
        .field("Arresting officer", officer.to_owned(), true);
        if booking.capped {
            embed = embed.field(
                "Capped",
                format!(
                    "Charges totalled more than the **{} min** limit. Bail is not capped.",
                    booking.cap_minutes.unwrap_or(0)
                ),
                false,
            );
        }
        Ok(embed)
    }
}

// ---- the booking picker (C# `ArrestBooking.cs`) ----

/// The value of the disabled option an empty server gets: a select with no options is
/// rejected outright, taking the whole message with it.
const NOBODY_ONLINE: &str = "-none-";
/// Bookings held open at once; the oldest are dropped past it.
const MAX_OPEN: usize = 100;
/// Discord's cap on a select's options.
const MAX_OPTIONS: usize = 25;
/// Discord's cap on an option's label and description.
const MAX_LABEL: usize = 100;

/// One booking in progress.
#[derive(Debug, Clone)]
struct Open {
    player: String,
    codes: Vec<String>,
    rate: f64,
    cap: i32,
    owner: u64,
    at: chrono::DateTime<chrono::Utc>,
    minutes: Option<i32>,
    roster: Vec<String>,
}

fn trim(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn id(step: &str) -> String {
    ComponentId::encode(ARREST_BOOKING, &[step]).unwrap_or_else(|_| ARREST_BOOKING.to_owned())
}

impl Open {
    fn result(&self) -> Booking {
        let booked = PenalCode::book(self.codes.iter().map(String::as_str), self.rate, self.cap);
        match self.minutes {
            Some(chosen) => override_time(&booked, chosen, self.cap),
            None => booked,
        }
    }

    fn render(&self) -> Embed {
        let result = self.result();
        let lines = if self.codes.is_empty() {
            "*No charges yet.*".to_owned()
        } else {
            result
                .charges
                .iter()
                .map(|c| {
                    let bail = c
                        .bail_at(self.rate)
                        .map(|b| format!(" • {}", lore::amount(i64::from(b))))
                        .unwrap_or_default();
                    format!("`{}`  {} — {:?}{bail}", c.code, c.name, c.class)
                })
                .collect::<Vec<_>>()
                .join("\n")
        };
        let set = if self.minutes.is_some() {
            " *(set by you)*"
        } else {
            ""
        };
        let sentence = format!(
            "**Jail:** {}{set}  •  **Bail:** {}",
            result.sentence_label(),
            result.bail_label(&police().words.money)
        );
        let note = if result.capped {
            format!(
                "\n*Charges total more than the {} min cap. Bail is not capped.*",
                result.cap_minutes.unwrap_or(0)
            )
        } else {
            String::new()
        };
        let who = if self.player.is_empty() {
            "no player chosen".to_owned()
        } else {
            Sanitize::code(Some(&self.player))
        };
        theme::warning(
            &format!("Booking: {who}"),
            Some(&format!(
                "Pick the player, then a section, then the charges. Confirm when done.\n**Set time** overrides the \
                 sentence; bail still stacks.\n\n{sentence}{note}\n{lines}"
            )),
        )
        .brand(None)
    }

    fn controls(&self, section: Option<i32>) -> Vec<ActionRow> {
        let placeholder = if !self.player.is_empty() {
            format!("Player: {}", trim(&self.player, 80))
        } else if self.roster.is_empty() {
            "Nobody is online - use \"Type a name\"".to_owned()
        } else {
            "Choose the player...".to_owned()
        };
        // Truncated at Discord's cap: "Type a name" reaches anybody it left out.
        let mut players: Vec<SelectOption> = self
            .roster
            .iter()
            .take(MAX_OPTIONS)
            .map(|n| SelectOption {
                label: trim(n, MAX_LABEL),
                value: trim(n, MAX_LABEL),
                description: None,
                default: false,
            })
            .collect();
        if players.is_empty() {
            players.push(SelectOption {
                label: "(nobody online)".into(),
                value: NOBODY_ONLINE.into(),
                description: None,
                default: false,
            });
        }
        let mut rows = vec![
            ActionRow::Select(SelectMenu {
                custom_id: id("player"),
                placeholder: Some(placeholder),
                options: players,
                min_values: 1,
                max_values: 1,
            }),
            ActionRow::Select(SelectMenu {
                custom_id: id("section"),
                placeholder: Some("Choose a penal code section...".into()),
                options: PenalCode::section_list()
                    .into_iter()
                    .take(MAX_OPTIONS)
                    .map(|(number, title, count)| SelectOption {
                        label: trim(&format!("{number}. {title}"), MAX_LABEL),
                        value: number.to_string(),
                        description: Some(format!("{count} charges")),
                        default: false,
                    })
                    .collect(),
                min_values: 1,
                max_values: 1,
            }),
        ];
        if let Some(charges) = section.and_then(|s| PenalCode::sections().remove(&s)) {
            let options: Vec<SelectOption> = charges
                .iter()
                .take(MAX_OPTIONS)
                .map(|c| SelectOption {
                    label: trim(&format!("{} {}", c.code, c.name), MAX_LABEL),
                    value: c.code.to_owned(),
                    description: Some(trim(
                        &format!("{:?} • {} min", c.class, c.jail_minutes),
                        MAX_LABEL,
                    )),
                    default: false,
                })
                .collect();
            let max = u8::try_from(options.len()).unwrap_or(25);
            rows.push(ActionRow::Select(SelectMenu {
                custom_id: id("charge"),
                placeholder: Some(format!(
                    "Add charge(s) from section {}...",
                    section.unwrap_or(0)
                )),
                options,
                min_values: 1,
                max_values: max,
            }));
        }
        // Both halves required: no charges punishes nobody for nothing, no player nobody.
        let mut confirm = Button::new(ButtonStyle::Danger, "Confirm arrest", id("confirm"));
        if self.codes.is_empty() || self.player.is_empty() {
            confirm = confirm.disabled();
        }
        rows.push(ActionRow::Buttons(vec![
            confirm,
            Button::new(ButtonStyle::Secondary, "Type a name", id("name")),
            Button::new(ButtonStyle::Primary, "Set time", id("time")),
            Button::new(ButtonStyle::Secondary, "Cancel", id("cancel")),
        ]));
        rows
    }

    fn reply(&self, section: Option<i32>) -> Reply {
        Reply::embed(self.render()).with_components(self.controls(section))
    }
}

/// The booking flow: pick a player, sections and charges, optionally a name or a time by
/// modal, then confirm. Only the officer who opened a booking can touch it, and a confirm
/// REMOVES it before the work, so a double-click cannot book twice.
pub struct ArrestBooking {
    open: Mutex<HashMap<u64, Open>>,
    recorder: Arc<ArrestRecorder>,
}

impl std::fmt::Debug for ArrestBooking {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ArrestBooking").finish_non_exhaustive()
    }
}

const EXPIRED: &str = "That booking has expired. Run `/arrest` again.";

impl ArrestBooking {
    #[must_use]
    pub fn new(recorder: Arc<ArrestRecorder>) -> Self {
        Self {
            open: Mutex::new(HashMap::new()),
            recorder,
        }
    }

    fn open(&self) -> std::sync::MutexGuard<'_, HashMap<u64, Open>> {
        self.open.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Open a booking as the deferred reply.
    ///
    /// # Errors
    /// When Discord refuses the edit.
    pub async fn begin(
        &self,
        responder: &dyn Responder,
        owner: u64,
        online: Vec<String>,
        rate: f64,
        cap: i32,
    ) -> Result<(), DiscordError> {
        let booking = Open {
            player: String::new(),
            codes: Vec::new(),
            rate,
            cap,
            owner,
            at: self.recorder.clock.now(),
            minutes: None,
            roster: online,
        };
        let Some(message) = responder.edit_original_message(booking.reply(None)).await? else {
            tracing::warn!("the booking message id is unknown, so its controls cannot be followed");
            return Ok(());
        };
        let mut open = self.open();
        open.insert(message, booking);
        if open.len() > MAX_OPEN {
            let mut by_age: Vec<(u64, chrono::DateTime<chrono::Utc>)> =
                open.iter().map(|(k, v)| (*k, v.at)).collect();
            by_age.sort_by_key(|(_, at)| *at);
            for (stale, _) in by_age.into_iter().take(open.len() - MAX_OPEN) {
                open.remove(&stale);
            }
        }
        Ok(())
    }

    /// Whether a booking is held for `message` (for tests and diagnostics).
    #[must_use]
    pub fn is_open(&self, message: u64) -> bool {
        self.open().contains_key(&message)
    }

    async fn on_modal(
        &self,
        step: &str,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> Result<(), DiscordError> {
        let Some(mut booking) = self.open().get(&invocation.message_id).cloned() else {
            return responder.respond(Reply::text(EXPIRED), true).await;
        };
        if step == "setname" {
            let typed = Sanitize::id(invocation.field("player"));
            if typed.is_empty() {
                return responder
                    .respond(Reply::text("That name has nothing usable in it."), true)
                    .await;
            }
            booking.player = typed;
        } else {
            match invocation
                .field("minutes")
                .map(str::trim)
                .and_then(|m| m.parse::<i32>().ok())
                .filter(|m| *m >= 0)
            {
                Some(minutes) => booking.minutes = Some(minutes),
                None => {
                    return responder
                        .respond(
                            Reply::text("Give a whole number of minutes, e.g. `10`."),
                            true,
                        )
                        .await;
                }
            }
        }
        self.open().insert(invocation.message_id, booking.clone());
        responder.update(booking.reply(None)).await
    }

    async fn confirm(
        &self,
        booking: Open,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> Result<(), DiscordError> {
        responder.acknowledge().await?;
        let result = booking.result();
        let config: PoliceConfig = self
            .recorder
            .store
            .read(POLICE_CONFIG, PoliceConfig::default())
            .unwrap_or_default();
        let reply = match self
            .recorder
            .record(
                &booking.player,
                &result,
                &invocation.username,
                config.bail_rate,
            )
            .await
        {
            Ok(receipt) => receipt.brand(None),
            Err(error) => {
                tracing::error!(player = %booking.player, %error, "booking failed to record");
                theme::failure(
                    "Arrest not recorded",
                    Some("That failed. The error has been logged."),
                )
                .brand(None)
            }
        };
        // Replaces the booking, so the picker does not sit re-clickable beside its arrest.
        responder.edit_original(Reply::embed(reply)).await
    }

    async fn dispatch(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> Result<(), DiscordError> {
        let parsed = ComponentId::parse(&invocation.custom_id);
        let step = parsed.argument(0).unwrap_or("");
        // A modal submit carries the message it came from: handled first, or the typed
        // sentence would go nowhere.
        if matches!(step, "setname" | "settime") {
            return self.on_modal(step, invocation, responder).await;
        }
        let Some(mut booking) = self.open().get(&invocation.message_id).cloned() else {
            return responder.respond(Reply::text(EXPIRED), true).await;
        };
        // ONLY THE OFFICER WHO OPENED IT: an arrest is somebody's punishment.
        if invocation.caller.id != booking.owner {
            return responder
                .respond(
                    Reply::text("That booking belongs to whoever ran the command."),
                    true,
                )
                .await;
        }
        match step {
            "player" => {
                let picked = invocation.values.first().map(String::as_str).unwrap_or("");
                if picked.is_empty() || picked == NOBODY_ONLINE {
                    return responder.acknowledge().await;
                }
                booking.player = picked.to_owned();
                self.open().insert(invocation.message_id, booking.clone());
                responder.update(booking.reply(None)).await
            }
            // Modals open on an UNACKNOWLEDGED interaction, so neither of these defers.
            "name" => {
                responder
                    .show_modal(Modal {
                        custom_id: id("setname"),
                        title: "Who is being booked?".into(),
                        inputs: vec![TextInput {
                            custom_id: "player".into(),
                            label: "Player name or ID".into(),
                            paragraph: false,
                            required: true,
                            placeholder: Some("exactly as it appears in game".into()),
                            value: None,
                            min_length: None,
                            max_length: Some(64),
                        }],
                    })
                    .await
            }
            "time" => {
                let label = if booking.cap > 0 {
                    format!("Minutes (max {})", booking.cap)
                } else {
                    "Minutes".into()
                };
                responder
                    .show_modal(Modal {
                        custom_id: id("settime"),
                        title: "Set the sentence".into(),
                        inputs: vec![TextInput {
                            custom_id: "minutes".into(),
                            label,
                            paragraph: false,
                            required: true,
                            placeholder: Some("e.g. 10".into()),
                            value: None,
                            min_length: None,
                            max_length: Some(4),
                        }],
                    })
                    .await
            }
            "cancel" => {
                self.open().remove(&invocation.message_id);
                responder
                    .update(Reply::embed(
                        theme::notice("Arrest cancelled", Some("No booking was recorded."))
                            .brand(None),
                    ))
                    .await
            }
            "section" => {
                let chosen = invocation
                    .values
                    .first()
                    .and_then(|v| v.parse::<i32>().ok());
                responder.update(booking.reply(chosen)).await
            }
            "charge" => {
                // Deduplicated: picking a charge twice must not jail somebody twice for it.
                for code in &invocation.values {
                    if PenalCode::get(Some(code)).is_some() && !booking.codes.contains(code) {
                        booking.codes.push(code.clone());
                    }
                }
                self.open().insert(invocation.message_id, booking.clone());
                responder.update(booking.reply(None)).await
            }
            "confirm" => {
                if booking.codes.is_empty() || booking.player.is_empty() {
                    return responder.acknowledge().await;
                }
                // REMOVED BEFORE THE WORK: a double-click cannot book twice.
                if self.open().remove(&invocation.message_id).is_none() {
                    return responder.acknowledge().await;
                }
                self.confirm(booking, invocation, responder).await
            }
            _ => responder.acknowledge().await,
        }
    }
}

impl ComponentHandler for ArrestBooking {
    fn prefix(&self) -> &str {
        ARREST_BOOKING
    }

    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            if let Err(error) = self.dispatch(invocation, responder).await {
                tracing::warn!(%error, "could not answer a booking interaction");
            }
        })
    }
}

// ---- /arrest ----

/// `/arrest`: opens the booking picker with who is online and the bail rate and cap.
pub struct ArrestCommand {
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
    pub booking: Arc<ArrestBooking>,
    pub rcon: Arc<RconRegistry>,
}

impl std::fmt::Debug for ArrestCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ArrestCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for ArrestCommand {
    fn name(&self) -> &str {
        "arrest"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("arrest", "Police - Book a player on penal-code charges")
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            if !self.access.allows(RequiredAccess::Police, caller) {
                let refusal = self.access.explain_refusal(caller, RequiredAccess::Police);
                return context
                    .reply(Reply::embed(
                        theme::denied("Not allowed", Some(&refusal)).brand(None),
                    ))
                    .await;
            }
            let config: PoliceConfig = self
                .store
                .read(POLICE_CONFIG, PoliceConfig::default())
                .unwrap_or_default();
            Ok(self
                .booking
                .begin(
                    context.responder,
                    caller.id,
                    self.rcon.all_online_players(),
                    config.bail_rate,
                    config.max_jail_minutes,
                )
                .await?)
        })
    }
}
