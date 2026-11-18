//! Answering an interaction: [`Responder`], [`ComponentResponder`] and [`Suggester`] over one
//! interaction's id and token. Commands, components and modals all answer through the same
//! interaction-callback endpoints, so one handle serves all three.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use futures_util::future::BoxFuture;
use pavlov_discord::command::{Choice, ChoiceValue};
use pavlov_discord::component::Modal;
use pavlov_discord::interaction::{ComponentResponder, DiscordError, Reply, Responder, Suggester};
use serenity::all::{
    AutocompleteChoice, Builder, CreateAutocompleteResponse, CreateInteractionResponse,
    CreateInteractionResponseMessage, Http, InteractionId,
};

use crate::convert;

/// One interaction, answerable.
pub struct InteractionHandle {
    http: Arc<Http>,
    id: InteractionId,
    token: String,
    acknowledged: AtomicBool,
}

impl std::fmt::Debug for InteractionHandle {
    // The token is a credential for this interaction: never printed.
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("InteractionHandle")
            .field("id", &self.id)
            .finish_non_exhaustive()
    }
}

fn failed(error: &serenity::Error) -> DiscordError {
    DiscordError(error.to_string())
}

impl InteractionHandle {
    #[must_use]
    pub fn new(http: Arc<Http>, id: InteractionId, token: String) -> Self {
        Self {
            http,
            id,
            token,
            acknowledged: AtomicBool::new(false),
        }
    }

    async fn callback(&self, response: CreateInteractionResponse) -> Result<(), DiscordError> {
        response
            .execute(&*self.http, (self.id, &self.token))
            .await
            .map_err(|e| failed(&e))?;
        self.acknowledged.store(true, Ordering::SeqCst);
        Ok(())
    }

    async fn edit(&self, reply: &Reply) -> Result<u64, DiscordError> {
        convert::edit_response(reply)
            .execute(&*self.http, &self.token)
            .await
            .map(|m| m.id.get())
            .map_err(|e| failed(&e))
    }

    async fn send_followup(&self, reply: &Reply, ephemeral: bool) -> Result<(), DiscordError> {
        convert::followup(reply, ephemeral)
            .execute(&*self.http, (None, &self.token))
            .await
            .map(|_| ())
            .map_err(|e| failed(&e))
    }

    fn deferral(ephemeral: bool) -> CreateInteractionResponse {
        CreateInteractionResponse::Defer(
            CreateInteractionResponseMessage::new().ephemeral(ephemeral),
        )
    }
}

impl Responder for InteractionHandle {
    fn defer(&self, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(self.callback(Self::deferral(ephemeral)))
    }

    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move {
            self.callback(CreateInteractionResponse::Message(
                convert::response_message(&reply, ephemeral),
            ))
            .await
        })
    }

    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move { self.edit(&reply).await.map(|_| ()) })
    }

    fn edit_original_message(
        &self,
        reply: Reply,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        Box::pin(async move { self.edit(&reply).await.map(Some) })
    }

    fn followup(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move { self.send_followup(&reply, ephemeral).await })
    }

    fn has_responded(&self) -> bool {
        self.acknowledged.load(Ordering::SeqCst)
    }
}

impl ComponentResponder for InteractionHandle {
    fn update(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move {
            self.callback(CreateInteractionResponse::UpdateMessage(
                convert::response_message(&reply, false),
            ))
            .await
        })
    }

    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Responder::respond(self, reply, ephemeral)
    }

    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Responder::edit_original(self, reply)
    }

    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(self.callback(CreateInteractionResponse::Acknowledge))
    }

    fn show_modal(&self, modal: Modal) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move {
            self.callback(CreateInteractionResponse::Modal(convert::modal(&modal)))
                .await
        })
    }

    fn defer_reply(&self, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(self.callback(Self::deferral(ephemeral)))
    }

    fn followup(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Responder::followup(self, reply, ephemeral)
    }
}

/// An autocomplete choice in Discord's shape.
pub fn autocomplete_choice(choice: &Choice) -> AutocompleteChoice {
    let value = match &choice.value {
        ChoiceValue::String(s) => serde_json::Value::from(s.clone()),
        ChoiceValue::Integer(i) => serde_json::Value::from(*i),
        ChoiceValue::Number(n) => serde_json::Value::from(*n),
    };
    AutocompleteChoice::new(choice.name.clone(), value)
}

impl Suggester for InteractionHandle {
    fn suggest(&self, choices: Vec<Choice>) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move {
            let response = CreateAutocompleteResponse::new()
                .set_choices(choices.iter().map(autocomplete_choice).collect());
            self.callback(CreateInteractionResponse::Autocomplete(response))
                .await
        })
    }
}
