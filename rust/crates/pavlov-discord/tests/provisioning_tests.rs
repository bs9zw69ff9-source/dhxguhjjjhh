#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]
//! The Discord half of C# `ProvisioningTests.cs`: the `/provisionserver` definition. The rest
//! of that file (Game.ini, the unit file, the provisioner's argv) lives in pavlov-core and
//! pavlov-host.

use pavlov_discord::command::{OptionKind, problems};
use pavlov_discord::commands::provision_server::build_spec;

#[test]
fn the_provision_command_is_within_discords_limits_and_takes_an_optional_server_key() {
    let built = build_spec();

    assert!(problems(&built).is_empty(), "{:?}", problems(&built));
    let keys: Vec<_> = built
        .options
        .iter()
        .filter(|o| o.name == "serverkey")
        .collect();
    assert_eq!(keys.len(), 1);
    let key = keys[0];
    assert!(!key.required);
    assert_eq!(key.max_length, Some(128));
    assert_eq!(key.kind, OptionKind::String);
}

#[test]
fn only_the_name_is_required_and_the_ports_are_bounded() {
    let built = build_spec();
    let required: Vec<&str> = built
        .options
        .iter()
        .filter(|o| o.required)
        .map(|o| o.name.as_str())
        .collect();
    assert_eq!(required, ["name"]);
    for port in ["gameport", "queryport", "rconport"] {
        let option = built.options.iter().find(|o| o.name == port).unwrap();
        assert_eq!(
            (option.min_value, option.max_value),
            (Some(1.0), Some(65_535.0))
        );
    }
    let players = built
        .options
        .iter()
        .find(|o| o.name == "maxplayers")
        .unwrap();
    assert_eq!(players.max_value, Some(24.0));
}
