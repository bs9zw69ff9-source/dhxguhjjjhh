//! Our plain values to serenity's builders, and serenity's interactions to our plain values.
//! Pure: no REST call happens here, so every conversion is tested by serialising the builder
//! or by feeding in a deserialised interaction.

use std::collections::HashMap;

use pavlov_core::embed::Embed;
use pavlov_discord::autocomplete::AutocompleteRequest;
use pavlov_discord::caller::{Caller, MemberView};
use pavlov_discord::command::{ChoiceValue, CommandSpec, OptionKind, OptionSpec};
use pavlov_discord::component::{ActionRow, ButtonStyle, Modal};
use pavlov_discord::interaction::{
    Attachment, ComponentInvocation, IntegrationOwners, Invocation, InvokedOption, OptionValue,
    Reply, ResolvedUser,
};
use serenity::all::{
    ActionRowComponent, AuthorizingIntegrationOwner, AuthorizingIntegrationOwners,
    ButtonStyle as SerenityButtonStyle, CommandDataOption, CommandDataOptionValue,
    CommandDataResolved, CommandInteraction, CommandOptionType, ComponentInteraction,
    ComponentInteractionDataKind, CreateActionRow, CreateAllowedMentions, CreateAttachment,
    CreateButton, CreateCommand, CreateCommandOption, CreateEmbed, CreateEmbedAuthor,
    CreateEmbedFooter, CreateInputText, CreateInteractionResponseFollowup,
    CreateInteractionResponseMessage, CreateMessage, CreateModal, CreateSelectMenu,
    CreateSelectMenuKind, CreateSelectMenuOption, EditInteractionResponse, EditMessage,
    InputTextStyle, InstallationContext, InteractionContext, Member, ModalInteraction,
    PartialMember, Permissions, ReactionType, Timestamp, User,
};

/// A command for registration.
pub fn command(spec: &CommandSpec) -> CreateCommand {
    let mut built = CreateCommand::new(spec.name.clone()).description(spec.description.clone());
    for option in &spec.options {
        built = built.add_option(option_of(option));
    }
    if let Some(bits) = spec.default_member_permissions {
        built = built.default_member_permissions(Permissions::from_bits_truncate(bits));
    }
    if let Some(installs) = spec.installs {
        let mut kinds = Vec::new();
        if installs.guild {
            kinds.push(InstallationContext::Guild);
        }
        if installs.user {
            kinds.push(InstallationContext::User);
        }
        built = built.integration_types(kinds);
    }
    if let Some(contexts) = spec.contexts {
        let mut kinds = Vec::new();
        if contexts.guild {
            kinds.push(InteractionContext::Guild);
        }
        if contexts.bot_dm {
            kinds.push(InteractionContext::BotDm);
        }
        if contexts.private_channel {
            kinds.push(InteractionContext::PrivateChannel);
        }
        built = built.contexts(kinds);
    }
    built
}

fn option_type(kind: OptionKind) -> CommandOptionType {
    match kind {
        OptionKind::SubCommand => CommandOptionType::SubCommand,
        OptionKind::SubCommandGroup => CommandOptionType::SubCommandGroup,
        OptionKind::String => CommandOptionType::String,
        OptionKind::Integer => CommandOptionType::Integer,
        OptionKind::Boolean => CommandOptionType::Boolean,
        OptionKind::User => CommandOptionType::User,
        OptionKind::Channel => CommandOptionType::Channel,
        OptionKind::Role => CommandOptionType::Role,
        OptionKind::Mentionable => CommandOptionType::Mentionable,
        OptionKind::Number => CommandOptionType::Number,
        OptionKind::Attachment => CommandOptionType::Attachment,
    }
}

fn option_kind(kind: CommandOptionType) -> OptionKind {
    match kind {
        CommandOptionType::SubCommand => OptionKind::SubCommand,
        CommandOptionType::SubCommandGroup => OptionKind::SubCommandGroup,
        CommandOptionType::Integer => OptionKind::Integer,
        CommandOptionType::Boolean => OptionKind::Boolean,
        CommandOptionType::User => OptionKind::User,
        CommandOptionType::Channel => OptionKind::Channel,
        CommandOptionType::Role => OptionKind::Role,
        CommandOptionType::Mentionable => OptionKind::Mentionable,
        CommandOptionType::Number => OptionKind::Number,
        CommandOptionType::Attachment => OptionKind::Attachment,
        _ => OptionKind::String,
    }
}

/// An integral, non-negative bound goes out as an integer: Discord validates an INTEGER
/// option's bounds as integers, and `1.0` is not one. Anything else goes as a number.
fn bounded(
    option: CreateCommandOption,
    value: f64,
    min: bool,
    integer: bool,
) -> CreateCommandOption {
    let whole = integer && value >= 0.0 && value.fract() == 0.0 && value <= u64::MAX as f64;
    match (whole, min) {
        (true, true) => option.min_int_value(value as u64),
        (true, false) => option.max_int_value(value as u64),
        (false, true) => option.min_number_value(value),
        (false, false) => option.max_number_value(value),
    }
}

fn option_of(spec: &OptionSpec) -> CreateCommandOption {
    let mut option = CreateCommandOption::new(
        option_type(spec.kind),
        spec.name.clone(),
        spec.description.clone(),
    );
    if !spec.kind.is_subcommand() {
        option = option.required(spec.required);
    }
    if spec.autocomplete {
        option = option.set_autocomplete(true);
    }
    for choice in &spec.choices {
        option = match &choice.value {
            ChoiceValue::String(v) => option.add_string_choice(choice.name.clone(), v.clone()),
            ChoiceValue::Integer(v) => match i32::try_from(*v) {
                Ok(v) => option.add_int_choice(choice.name.clone(), v),
                Err(_) => option.add_number_choice(choice.name.clone(), *v as f64),
            },
            ChoiceValue::Number(v) => option.add_number_choice(choice.name.clone(), *v),
        };
    }
    let integer = spec.kind == OptionKind::Integer;
    if let Some(min) = spec.min_value {
        option = bounded(option, min, true, integer);
    }
    if let Some(max) = spec.max_value {
        option = bounded(option, max, false, integer);
    }
    if let Some(min) = spec.min_length {
        option = option.min_length(min);
    }
    if let Some(max) = spec.max_length {
        option = option.max_length(max);
    }
    for sub in &spec.options {
        option = option.add_sub_option(option_of(sub));
    }
    option
}

/// An embed.
pub fn embed(embed: &Embed) -> CreateEmbed {
    let mut built = CreateEmbed::new();
    if let Some(title) = &embed.title {
        built = built.title(title.clone());
    }
    if let Some(description) = &embed.description {
        built = built.description(description.clone());
    }
    if let Some(color) = embed.color {
        built = built.colour(color);
    }
    if let Some(author) = &embed.author {
        let mut line = CreateEmbedAuthor::new(author.clone());
        if let Some(icon) = &embed.author_icon {
            line = line.icon_url(icon.clone());
        }
        built = built.author(line);
    }
    for field in &embed.fields {
        built = built.field(field.name.clone(), field.value.clone(), field.inline);
    }
    if let Some(footer) = &embed.footer {
        built = built.footer(CreateEmbedFooter::new(footer.clone()));
    }
    if let Some(at) = embed.timestamp
        && let Ok(at) = Timestamp::from_millis(at.timestamp_millis())
    {
        built = built.timestamp(at);
    }
    if let Some(image) = &embed.image {
        built = built.image(image.clone());
    }
    built
}

fn unicode_emoji(emoji: &str) -> ReactionType {
    ReactionType::Unicode(emoji.to_owned())
}

/// Message components.
#[must_use]
pub fn rows(rows: &[ActionRow]) -> Vec<CreateActionRow> {
    rows.iter()
        .map(|row| match row {
            ActionRow::Buttons(buttons) => CreateActionRow::Buttons(
                buttons
                    .iter()
                    .map(|b| {
                        let mut button = match b.style {
                            ButtonStyle::Link => CreateButton::new_link(b.target.clone()),
                            style => CreateButton::new(b.target.clone()).style(match style {
                                ButtonStyle::Primary => SerenityButtonStyle::Primary,
                                ButtonStyle::Success => SerenityButtonStyle::Success,
                                ButtonStyle::Danger => SerenityButtonStyle::Danger,
                                _ => SerenityButtonStyle::Secondary,
                            }),
                        };
                        button = button.label(b.label.clone()).disabled(b.disabled);
                        if let Some(emoji) = &b.emoji {
                            button = button.emoji(unicode_emoji(emoji));
                        }
                        button
                    })
                    .collect(),
            ),
            ActionRow::Select(menu) => {
                let options = menu
                    .options
                    .iter()
                    .map(|o| {
                        let mut option =
                            CreateSelectMenuOption::new(o.label.clone(), o.value.clone())
                                .default_selection(o.default);
                        if let Some(description) = &o.description {
                            option = option.description(description.clone());
                        }
                        if let Some(emoji) = &o.emoji {
                            option = option.emoji(unicode_emoji(emoji));
                        }
                        option
                    })
                    .collect();
                let mut select = CreateSelectMenu::new(
                    menu.custom_id.clone(),
                    CreateSelectMenuKind::String { options },
                )
                .min_values(menu.min_values)
                .max_values(menu.max_values);
                if let Some(placeholder) = &menu.placeholder {
                    select = select.placeholder(placeholder.clone());
                }
                CreateActionRow::SelectMenu(select)
            }
        })
        .collect()
}

/// A modal: one text input per row, as Discord requires.
pub fn modal(modal: &Modal) -> CreateModal {
    let inputs = modal
        .inputs
        .iter()
        .map(|i| {
            let style = if i.paragraph {
                InputTextStyle::Paragraph
            } else {
                InputTextStyle::Short
            };
            let mut input = CreateInputText::new(style, i.label.clone(), i.custom_id.clone())
                .required(i.required);
            if let Some(placeholder) = &i.placeholder {
                input = input.placeholder(placeholder.clone());
            }
            if let Some(value) = &i.value {
                input = input.value(value.clone());
            }
            if let Some(min) = i.min_length {
                input = input.min_length(min);
            }
            if let Some(max) = i.max_length {
                input = input.max_length(max);
            }
            CreateActionRow::InputText(input)
        })
        .collect();
    CreateModal::new(modal.custom_id.clone(), modal.title.clone()).components(inputs)
}

/// Replies never ping anybody: names in them are player text.
fn no_mentions() -> CreateAllowedMentions {
    CreateAllowedMentions::new()
}

fn files(reply: &Reply) -> Vec<CreateAttachment> {
    reply
        .files
        .iter()
        .map(|f| CreateAttachment::bytes(f.bytes.clone(), f.filename.clone()))
        .collect()
}

fn embeds(reply: &Reply) -> Vec<CreateEmbed> {
    reply.embeds.iter().map(embed).collect()
}

/// A first answer (or an update of the component's message).
pub fn response_message(reply: &Reply, ephemeral: bool) -> CreateInteractionResponseMessage {
    let mut message = CreateInteractionResponseMessage::new()
        .embeds(embeds(reply))
        .components(rows(&reply.components))
        .allowed_mentions(no_mentions())
        .files(files(reply));
    if let Some(content) = &reply.content {
        message = message.content(content.clone());
    }
    if ephemeral {
        message = message.ephemeral(true);
    }
    message
}

/// An edit of the first answer. Every part is set, so an edit REPLACES: an empty content or
/// component list clears what was there, which is what a refreshed panel means.
pub fn edit_response(reply: &Reply) -> EditInteractionResponse {
    let mut edit = EditInteractionResponse::new()
        .content(reply.content.clone().unwrap_or_default())
        .embeds(embeds(reply))
        .components(rows(&reply.components))
        .allowed_mentions(no_mentions());
    for file in files(reply) {
        edit = edit.new_attachment(file);
    }
    edit
}

/// A followup message.
pub fn followup(reply: &Reply, ephemeral: bool) -> CreateInteractionResponseFollowup {
    let mut message = CreateInteractionResponseFollowup::new()
        .embeds(embeds(reply))
        .components(rows(&reply.components))
        .allowed_mentions(no_mentions())
        .files(files(reply))
        .ephemeral(ephemeral);
    if let Some(content) = &reply.content {
        message = message.content(content.clone());
    }
    message
}

/// A channel post.
pub fn channel_message(body: &Embed, components: &[ActionRow]) -> CreateMessage {
    CreateMessage::new()
        .embed(embed(body))
        .components(rows(components))
        .allowed_mentions(no_mentions())
}

/// An edit of a channel post. Components are re-applied every time: leaving them unset
/// strips the buttons, and the verification panel lost its Verify button that way.
pub fn channel_edit(body: &Embed, components: &[ActionRow]) -> EditMessage {
    EditMessage::new()
        .embed(embed(body))
        .components(rows(components))
        .allowed_mentions(no_mentions())
}

/// The caller: the member the interaction carried, if any.
#[must_use]
pub fn caller(user: &User, member: Option<&Member>) -> Caller {
    Caller {
        id: user.id.get(),
        member: member.map(|m| MemberView {
            guild_id: m.guild_id.get(),
            role_ids: m.roles.iter().map(|r| r.get()).collect(),
            administrator: m.permissions.is_some_and(Permissions::administrator),
        }),
    }
}

/// A member fetched over REST (no interaction permissions): Administrator is unknown there,
/// so it reads false and the role map decides, as in the C#.
#[must_use]
pub fn member_view(member: &Member) -> MemberView {
    MemberView {
        guild_id: member.guild_id.get(),
        role_ids: member.roles.iter().map(|r| r.get()).collect(),
        administrator: member.permissions.is_some_and(Permissions::administrator),
    }
}

/// Which installs an interaction came through. A guild install used from a DM has no guild
/// id; it is still a guild install, recorded as guild 0.
#[must_use]
pub fn owners(owners: &AuthorizingIntegrationOwners) -> IntegrationOwners {
    let mut result = IntegrationOwners::default();
    for owner in &owners.0 {
        match owner {
            AuthorizingIntegrationOwner::GuildInstall(guild) => {
                result.guild_install = Some(guild.map_or(0, |g| g.get()));
            }
            AuthorizingIntegrationOwner::UserInstall(user) => {
                result.user_install = Some(user.get());
            }
            _ => {}
        }
    }
    result
}

fn invoked(option: &CommandDataOption, resolved: &CommandDataResolved) -> InvokedOption {
    let name = option.name.clone();
    let value = |kind, value| InvokedOption::value(name.clone(), kind, value);
    match &option.value {
        CommandDataOptionValue::SubCommand(options) => InvokedOption::sub(
            name.clone(),
            options.iter().map(|o| invoked(o, resolved)).collect(),
        ),
        CommandDataOptionValue::SubCommandGroup(options) => InvokedOption {
            kind: OptionKind::SubCommandGroup,
            ..InvokedOption::sub(
                name.clone(),
                options.iter().map(|o| invoked(o, resolved)).collect(),
            )
        },
        CommandDataOptionValue::String(s) => {
            value(OptionKind::String, OptionValue::String(s.clone()))
        }
        CommandDataOptionValue::Autocomplete { kind, value: typed } => {
            value(option_kind(*kind), OptionValue::String(typed.clone()))
        }
        CommandDataOptionValue::Integer(i) => value(OptionKind::Integer, OptionValue::Integer(*i)),
        CommandDataOptionValue::Number(n) => value(OptionKind::Number, OptionValue::Number(*n)),
        CommandDataOptionValue::Boolean(b) => value(OptionKind::Boolean, OptionValue::Boolean(*b)),
        CommandDataOptionValue::User(u) => value(OptionKind::User, OptionValue::User(u.get())),
        CommandDataOptionValue::Channel(c) => {
            value(OptionKind::Channel, OptionValue::Channel(c.get()))
        }
        CommandDataOptionValue::Role(r) => value(OptionKind::Role, OptionValue::Role(r.get())),
        CommandDataOptionValue::Mentionable(m) => {
            value(OptionKind::Mentionable, OptionValue::Mentionable(m.get()))
        }
        CommandDataOptionValue::Attachment(id) => match resolved.attachments.get(id) {
            Some(a) => value(
                OptionKind::Attachment,
                OptionValue::Attachment(Attachment {
                    filename: a.filename.clone(),
                    url: a.url.clone(),
                    size: u64::from(a.size),
                    content_type: a.content_type.clone(),
                }),
            ),
            None => InvokedOption {
                name,
                kind: OptionKind::Attachment,
                value: None,
                options: Vec::new(),
            },
        },
        _ => InvokedOption {
            name,
            kind: OptionKind::String,
            value: None,
            options: Vec::new(),
        },
    }
}

fn resolved_user(user: &User, _member: Option<&PartialMember>) -> ResolvedUser {
    ResolvedUser {
        id: user.id.get(),
        username: user.name.clone(),
        global_name: user.global_name.clone(),
        bot: user.bot,
    }
}

/// A slash command as our [`Invocation`]. `guild_name` comes from the cache, when it has it.
#[must_use]
pub fn invocation(interaction: &CommandInteraction, guild_name: Option<String>) -> Invocation {
    let resolved = &interaction.data.resolved;
    let resolved_users: HashMap<u64, ResolvedUser> = resolved
        .users
        .iter()
        .map(|(id, user)| (id.get(), resolved_user(user, resolved.members.get(id))))
        .collect();
    Invocation {
        command: interaction.data.name.clone(),
        options: interaction
            .data
            .options
            .iter()
            .map(|o| invoked(o, resolved))
            .collect(),
        caller: caller(&interaction.user, interaction.member.as_deref()),
        username: interaction.user.name.clone(),
        avatar_url: interaction.user.avatar_url(),
        guild_id: interaction.guild_id.map(|g| g.get()),
        guild_name,
        channel_id: interaction.channel_id.get(),
        resolved_users,
        resolved_roles: resolved
            .roles
            .iter()
            .map(|(id, role)| (id.get(), role.name.clone()))
            .collect(),
        resolved_channels: resolved
            .channels
            .iter()
            .map(|(id, channel)| (id.get(), channel.name.clone().unwrap_or_default()))
            .collect(),
        owners: owners(&interaction.authorizing_integration_owners),
    }
}

/// The focused option and the `faction` beside it. Serenity nests a subcommand's fields, so
/// the deepest level that holds the focused one is searched.
#[must_use]
pub fn autocomplete_request(interaction: &CommandInteraction) -> Option<AutocompleteRequest> {
    let focused = interaction.data.autocomplete()?;
    let mut level = &interaction.data.options;
    while let Some(inner) = level.iter().find_map(|o| match &o.value {
        CommandDataOptionValue::SubCommand(inner)
        | CommandDataOptionValue::SubCommandGroup(inner) => Some(inner),
        _ => None,
    }) {
        level = inner;
    }
    let faction = level
        .iter()
        .find(|o| o.name == "faction")
        .and_then(|o| match &o.value {
            CommandDataOptionValue::String(s) => Some(s.clone()),
            CommandDataOptionValue::Autocomplete { value, .. } => Some(value.clone()),
            _ => None,
        });
    Some(AutocompleteRequest {
        user_id: interaction.user.id.get(),
        username: interaction.user.name.clone(),
        focused: focused.name.to_owned(),
        typed: focused.value.to_owned(),
        faction,
    })
}

/// A button press or select choice.
#[must_use]
pub fn component_invocation(interaction: &ComponentInteraction) -> ComponentInvocation {
    let values = match &interaction.data.kind {
        ComponentInteractionDataKind::StringSelect { values } => values.clone(),
        ComponentInteractionDataKind::UserSelect { values } => {
            values.iter().map(ToString::to_string).collect()
        }
        ComponentInteractionDataKind::RoleSelect { values } => {
            values.iter().map(ToString::to_string).collect()
        }
        ComponentInteractionDataKind::ChannelSelect { values } => {
            values.iter().map(ToString::to_string).collect()
        }
        ComponentInteractionDataKind::MentionableSelect { values } => {
            values.iter().map(ToString::to_string).collect()
        }
        _ => Vec::new(),
    };
    ComponentInvocation {
        custom_id: interaction.data.custom_id.clone(),
        caller: caller(&interaction.user, interaction.member.as_ref()),
        username: interaction.user.name.clone(),
        guild_id: interaction.guild_id.map(|g| g.get()),
        channel_id: interaction.channel_id.get(),
        message_id: interaction.message.id.get(),
        message_title: interaction
            .message
            .embeds
            .first()
            .and_then(|e| e.title.clone()),
        values,
        fields: Vec::new(),
    }
}

/// A modal submit: its text inputs by custom id.
#[must_use]
pub fn modal_invocation(interaction: &ModalInteraction) -> ComponentInvocation {
    let fields = interaction
        .data
        .components
        .iter()
        .flat_map(|row| row.components.iter())
        .filter_map(|c| match c {
            ActionRowComponent::InputText(input) => Some((
                input.custom_id.clone(),
                input.value.clone().unwrap_or_default(),
            )),
            _ => None,
        })
        .collect();
    ComponentInvocation {
        custom_id: interaction.data.custom_id.clone(),
        caller: caller(&interaction.user, interaction.member.as_ref()),
        username: interaction.user.name.clone(),
        guild_id: interaction.guild_id.map(|g| g.get()),
        channel_id: interaction.channel_id.get(),
        message_id: interaction.message.as_ref().map_or(0, |m| m.id.get()),
        message_title: interaction
            .message
            .as_ref()
            .and_then(|m| m.embeds.first())
            .and_then(|e| e.title.clone()),
        values: Vec::new(),
        fields,
    }
}

/// A REST failure's HTTP status and Discord JSON code, when it was one.
#[must_use]
pub fn http_failure(error: &serenity::Error) -> Option<(u16, isize)> {
    match error {
        serenity::Error::Http(serenity::http::HttpError::UnsuccessfulRequest(response)) => {
            Some((response.status_code.as_u16(), response.error.code))
        }
        _ => None,
    }
}

/// Discord's "Unknown Message".
pub const UNKNOWN_MESSAGE: isize = 10008;
/// Discord's "Cannot send messages to this user" (closed DMs, no shared server).
pub const CANNOT_DM: isize = 50007;
