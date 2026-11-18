//! The adapter's conversions, checked on the wire: what Discord receives is the builder's
//! JSON, and what arrives is a deserialised interaction payload. Discord.Net did this
//! inside the library, so the C# had nothing to port; the shapes below are Discord's own.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use chrono::{TimeZone, Utc};
use pavlov_core::embed::{Embed, EmbedField};
use pavlov_discord::command::{Choice, CommandSpec, Contexts, Installs, OptionKind, OptionSpec};
use pavlov_discord::component::{
    ActionRow, Button, ButtonStyle, Modal, SelectMenu, SelectOption, TextInput,
};
use pavlov_discord::interaction::{OptionValue, Reply};
use pavlov_discord::registration::GuildRef;
use pavlov_gateway::convert;
use pavlov_gateway::respond::autocomplete_choice;
use pavlov_gateway::rest::KnownGuilds;
use serde_json::{Value, json};
use serenity::all::{CommandInteraction, ModalInteraction};

fn wire<T: serde::Serialize>(value: &T) -> Value {
    serde_json::to_value(value).unwrap()
}

#[test]
fn a_command_registers_with_discords_option_types_and_integer_bounds() {
    let spec = CommandSpec {
        default_member_permissions: Some(8),
        installs: Some(Installs {
            guild: true,
            user: true,
        }),
        contexts: Some(Contexts {
            guild: true,
            bot_dm: true,
            private_channel: false,
        }),
        ..CommandSpec::new("ban", "Ban a player")
    }
    .option(
        OptionSpec::sub("temp", "A temporary ban")
            .option(
                OptionSpec::new(OptionKind::String, "player", "Who")
                    .required()
                    .autocomplete(),
            )
            .option(
                OptionSpec::new(OptionKind::Integer, "hours", "How long")
                    .range(Some(1.0), Some(720.0)),
            )
            .option(
                OptionSpec::new(OptionKind::String, "reason", "Why")
                    .choice(Choice::string("Cheating", "cheating"))
                    .length(None, Some(200)),
            ),
    );

    let json = wire(&convert::command(&spec));

    assert_eq!(json["name"], "ban");
    assert_eq!(json["default_member_permissions"], "8");
    assert_eq!(json["integration_types"], json!([0, 1]));
    assert_eq!(json["contexts"], json!([0, 1]));
    let temp = &json["options"][0];
    assert_eq!(temp["type"], 1, "a subcommand");
    assert!(temp.get("required").is_none_or(|r| r == false));
    let player = &temp["options"][0];
    assert_eq!(player["type"], 3);
    assert_eq!(player["required"], true);
    assert_eq!(player["autocomplete"], true);
    let hours = &temp["options"][1];
    assert_eq!(hours["type"], 4);
    assert_eq!(hours["min_value"], json!(1), "an integer, not 1.0");
    assert_eq!(hours["max_value"], json!(720));
    let reason = &temp["options"][2];
    assert_eq!(
        reason["choices"][0],
        json!({"name": "Cheating", "value": "cheating"})
    );
    assert_eq!(reason["max_length"], 200);
}

#[test]
fn a_negative_integer_bound_still_goes_out() {
    let spec = CommandSpec::new("money", "Adjust money").option(
        OptionSpec::new(OptionKind::Integer, "amount", "By how much").range(Some(-500.0), None),
    );
    let json = wire(&convert::command(&spec));
    assert_eq!(json["options"][0]["min_value"].as_f64(), Some(-500.0));
}

#[test]
fn an_embed_carries_every_part() {
    let embed = Embed {
        title: Some("✅ Banned".into()),
        description: Some("Alice is banned".into()),
        color: Some(0x00FF_8800),
        author: Some("mod".into()),
        author_icon: Some("https://cdn.example/a.png".into()),
        fields: vec![EmbedField {
            name: "Reason".into(),
            value: "cheating".into(),
            inline: true,
        }],
        footer: Some("Police".into()),
        timestamp: Some(Utc.with_ymd_and_hms(2026, 1, 2, 3, 4, 5).unwrap()),
        image: Some("attachment://card.png".into()),
    };

    let json = wire(&convert::embed(&embed));

    assert_eq!(json["title"], "✅ Banned");
    assert_eq!(json["color"], 0x00FF_8800);
    assert_eq!(json["author"]["name"], "mod");
    assert_eq!(json["author"]["icon_url"], "https://cdn.example/a.png");
    assert_eq!(
        json["fields"][0],
        json!({"name": "Reason", "value": "cheating", "inline": true})
    );
    assert_eq!(json["footer"]["text"], "Police");
    assert!(
        json["timestamp"]
            .as_str()
            .unwrap()
            .starts_with("2026-01-02T03:04:05")
    );
    assert_eq!(json["image"]["url"], "attachment://card.png");
}

#[test]
fn buttons_links_and_selects_map_to_discords_component_types() {
    let rows = vec![
        ActionRow::Buttons(vec![
            Button::new(ButtonStyle::Danger, "Ban", "ban:1"),
            Button {
                emoji: Some("🔗".into()),
                ..Button::new(ButtonStyle::Link, "Wiki", "https://wiki.example")
            },
            Button::new(ButtonStyle::Secondary, "Next", "pg:2").disabled(),
        ]),
        ActionRow::Select(SelectMenu {
            custom_id: "cfg:pick".into(),
            placeholder: Some("Choose".into()),
            options: vec![SelectOption {
                label: "Wipe".into(),
                value: "wipe".into(),
                description: Some("Clears it".into()),
                default: false,
                emoji: Some("🧹".into()),
            }],
            min_values: 1,
            max_values: 1,
        }),
    ];

    let json = wire(&convert::rows(&rows));

    let buttons = &json[0]["components"];
    assert_eq!(json[0]["type"], 1);
    assert_eq!(buttons[0]["style"], 4);
    assert_eq!(buttons[0]["custom_id"], "ban:1");
    assert_eq!(buttons[1]["style"], 5);
    assert_eq!(buttons[1]["url"], "https://wiki.example");
    assert!(buttons[1].get("custom_id").is_none());
    assert_eq!(buttons[1]["emoji"]["name"], "🔗");
    assert_eq!(buttons[2]["style"], 2);
    assert_eq!(buttons[2]["disabled"], true);
    let select = &json[1]["components"][0];
    assert_eq!(select["type"], 3);
    assert_eq!(select["custom_id"], "cfg:pick");
    assert_eq!(select["placeholder"], "Choose");
    assert_eq!(select["options"][0]["value"], "wipe");
    assert_eq!(select["options"][0]["emoji"]["name"], "🧹");
}

#[test]
fn a_modal_puts_each_text_input_in_its_own_row() {
    let modal = Modal {
        custom_id: "cfg:confirm".into(),
        title: "Type CONFIRM".into(),
        inputs: vec![
            TextInput {
                custom_id: "confirm".into(),
                label: "Confirm".into(),
                paragraph: false,
                required: true,
                placeholder: None,
                value: None,
                min_length: None,
                max_length: Some(7),
            },
            TextInput {
                custom_id: "note".into(),
                label: "Note".into(),
                paragraph: true,
                required: false,
                placeholder: Some("optional".into()),
                value: None,
                min_length: None,
                max_length: None,
            },
        ],
    };

    let json = wire(&convert::modal(&modal));

    assert_eq!(json["custom_id"], "cfg:confirm");
    assert_eq!(json["title"], "Type CONFIRM");
    let rows = json["components"].as_array().unwrap();
    assert_eq!(rows.len(), 2);
    assert_eq!(rows[0]["components"][0]["type"], 4);
    assert_eq!(rows[0]["components"][0]["style"], 1);
    assert_eq!(rows[0]["components"][0]["max_length"], 7);
    assert_eq!(rows[1]["components"][0]["style"], 2);
}

#[test]
fn a_reply_never_pings_and_an_ephemeral_one_is_flagged() {
    let reply = Reply::text("<@123> was banned");
    let json = wire(&convert::response_message(&reply, true));
    assert_eq!(json["flags"], 64);
    assert_eq!(json["allowed_mentions"]["parse"], json!([]));

    let edit = wire(&convert::edit_response(&reply));
    assert_eq!(edit["allowed_mentions"]["parse"], json!([]));
    let followup = wire(&convert::followup(&reply, false));
    assert_eq!(followup["allowed_mentions"]["parse"], json!([]));
}

#[test]
fn an_edit_replaces_the_components_so_a_refreshed_panel_loses_stale_buttons() {
    let json = wire(&convert::edit_response(&Reply::text("done")));
    assert_eq!(json["components"], json!([]));
}

#[test]
fn autocomplete_choices_keep_their_value_type() {
    assert_eq!(
        wire(&autocomplete_choice(&Choice::string("Alice", "Alice"))),
        json!({"name": "Alice", "value": "Alice"})
    );
    assert_eq!(
        wire(&autocomplete_choice(&Choice::integer("Server 2", 2))),
        json!({"name": "Server 2", "value": 2})
    );
}

fn user(id: &str, name: &str) -> Value {
    json!({"id": id, "username": name, "discriminator": "0", "global_name": null, "avatar": null})
}

fn command_payload(options: Value, extra_data: Value) -> Value {
    let mut data = json!({
        "id": "900", "name": "ban", "type": 1, "options": options
    });
    for (k, v) in extra_data.as_object().unwrap() {
        data[k] = v.clone();
    }
    json!({
        "id": "1000",
        "application_id": "2000",
        "type": 2,
        "data": data,
        "guild_id": "3000",
        "channel_id": "4000",
        "member": {
            "user": user("5000", "moddy"),
            "roles": ["77", "78"],
            "joined_at": "2024-01-01T00:00:00Z",
            "deaf": false,
            "mute": false,
            "flags": 0,
            "permissions": "8"
        },
        "token": "secret-token",
        "version": 1,
        "locale": "en-US",
        "entitlements": [],
        "attachment_size_limit": 8388608,
        "authorizing_integration_owners": {"0": "3000"},
        "context": 0
    })
}

#[test]
fn a_slash_command_payload_becomes_an_invocation() {
    let payload = command_payload(
        json!([{
            "name": "temp", "type": 1, "options": [
                {"name": "player", "type": 3, "value": "Alice"},
                {"name": "hours", "type": 4, "value": 24},
                {"name": "who", "type": 6, "value": "6000"}
            ]
        }]),
        json!({"resolved": {"users": {"6000": user("6000", "target")}}}),
    );
    let interaction: CommandInteraction = serde_json::from_value(payload).unwrap();

    let invocation = convert::invocation(&interaction, Some("Police RP".into()));

    assert_eq!(invocation.command, "ban");
    assert_eq!(invocation.subcommand(), Some("temp"));
    assert_eq!(invocation.string("player"), Some("Alice"));
    assert_eq!(invocation.integer("hours"), Some(24));
    assert_eq!(invocation.get("who"), Some(&OptionValue::User(6000)));
    assert_eq!(invocation.user("who").unwrap().username, "target");
    assert_eq!(invocation.caller.id, 5000);
    let member = invocation.caller.member.as_ref().unwrap();
    assert_eq!(member.guild_id, 3000);
    assert_eq!(member.role_ids, [77, 78]);
    assert!(member.administrator);
    assert_eq!(invocation.guild_id, Some(3000));
    assert_eq!(invocation.guild_name.as_deref(), Some("Police RP"));
    assert_eq!(invocation.channel_id, 4000);
    assert_eq!(invocation.owners.guild_install, Some(3000));
    assert_eq!(invocation.owners.user_install, None);
}

#[test]
fn a_user_install_in_a_dm_has_no_member_and_names_the_user_owner() {
    let mut payload = command_payload(json!([]), json!({}));
    let object = payload.as_object_mut().unwrap();
    object.remove("member");
    object.remove("guild_id");
    object.insert("user".into(), user("5000", "moddy"));
    object.insert(
        "authorizing_integration_owners".into(),
        json!({"1": "5000"}),
    );
    let interaction: CommandInteraction = serde_json::from_value(payload).unwrap();

    let invocation = convert::invocation(&interaction, None);

    assert!(invocation.caller.member.is_none());
    assert_eq!(invocation.guild_id, None);
    assert_eq!(invocation.owners.user_install, Some(5000));
    assert_eq!(invocation.owners.guild_install, None);
}

#[test]
fn an_autocomplete_reads_the_focused_field_and_the_faction_beside_it() {
    let mut payload = command_payload(
        json!([{
            "name": "setrank", "type": 1, "options": [
                {"name": "faction", "type": 3, "value": "lspd"},
                {"name": "rank", "type": 3, "value": "Ser", "focused": true}
            ]
        }]),
        json!({}),
    );
    payload["type"] = json!(4);
    let interaction: CommandInteraction = serde_json::from_value(payload).unwrap();

    let request = convert::autocomplete_request(&interaction).unwrap();

    assert_eq!(request.focused, "rank");
    assert_eq!(request.typed, "Ser");
    assert_eq!(request.faction.as_deref(), Some("lspd"));
    assert_eq!(request.user_id, 5000);
}

#[test]
fn a_modal_submit_carries_its_text_inputs_by_custom_id() {
    let payload = json!({
        "id": "1000",
        "application_id": "2000",
        "type": 5,
        "data": {
            "custom_id": "cfg:confirm:wipe",
            "components": [
                {"type": 1, "components": [{"type": 4, "custom_id": "confirm", "value": "CONFIRM"}]},
                {"type": 1, "components": [{"type": 4, "custom_id": "note", "value": ""}]}
            ]
        },
        "channel_id": "4000",
        "user": user("5000", "boss"),
        "token": "secret-token",
        "version": 1,
        "locale": "en-US",
        "entitlements": [],
        "attachment_size_limit": 8388608
    });
    let interaction: ModalInteraction = serde_json::from_value(payload).unwrap();

    let invocation = convert::modal_invocation(&interaction);

    assert_eq!(invocation.custom_id, "cfg:confirm:wipe");
    assert_eq!(invocation.field("confirm"), Some("CONFIRM"));
    assert_eq!(invocation.field("note"), Some(""));
    assert_eq!(invocation.caller.id, 5000);
    assert_eq!(
        invocation.message_id, 0,
        "a modal from a slash command has no message"
    );
}

#[test]
fn only_an_http_failure_has_a_status() {
    let error = serenity::Error::Other("socket closed");
    assert_eq!(convert::http_failure(&error), None);
}

#[test]
fn known_guilds_track_joins_renames_and_leaves() {
    let guilds = KnownGuilds::default();
    let guild = |id, name: &str| GuildRef {
        id,
        name: name.into(),
    };
    guilds.replace(vec![guild(1, "1")]);

    assert!(
        !guilds.upsert(guild(1, "Police RP")),
        "a rename, not a join"
    );
    assert!(guilds.upsert(guild(2, "New")));
    assert_eq!(guilds.all()[0].name, "Police RP");
    guilds.remove(1);
    assert!(!guilds.contains(1));
    assert!(guilds.contains(2));
}
