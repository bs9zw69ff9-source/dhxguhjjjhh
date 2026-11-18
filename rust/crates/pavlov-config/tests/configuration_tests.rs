//! Port of `ConfigurationTests.cs`: DotEnvParsingTests and BotOptionsTests.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

mod dot_env_parsing_tests {
    //! The .env parser must agree with dotenv 16 on the same file. A divergence does not look
    //! like a parsing bug - it looks like a wrong password, a truncated webhook URL, or a token
    //! with a stray quote in it.

    use pavlov_config::dotenv::parse;

    fn value(line: &str) -> Option<String> {
        parse(line).get("KEY").map(str::to_owned)
    }

    #[test]
    fn plain_assignments_bind() {
        assert_eq!(value("KEY=value").as_deref(), Some("value"));
        assert_eq!(value("  KEY = value  ").as_deref(), Some("value"));
        assert_eq!(value("export KEY=value").as_deref(), Some("value"));
        assert_eq!(value("KEY: value").as_deref(), Some("value"));
    }

    #[test]
    fn quotes_are_stripped() {
        assert_eq!(value("KEY=\"value\"").as_deref(), Some("value"));
        assert_eq!(value("KEY='value'").as_deref(), Some("value"));
        assert_eq!(value("KEY=`value`").as_deref(), Some("value"));
    }

    #[test]
    fn escapes_expand_inside_double_quotes_only() {
        // dotenv's rule, not ours. A single-quoted value is literal.
        assert_eq!(value(r#"KEY="a\nb""#).as_deref(), Some("a\nb"));
        assert_eq!(value(r"KEY='a\nb'").as_deref(), Some(r"a\nb"));
    }

    #[test]
    fn only_newline_and_carriage_return_are_escapes() {
        /* dotenv expands \n and \r and NOTHING else - a backslash-quote stays literal. The C#
        port unescaped \" until a differential harness caught it, which would have silently
        changed the value of any token containing one. */
        assert_eq!(
            value(r#"KEY="escaped \" quote""#).as_deref(),
            Some(r#"escaped \" quote"#)
        );
        assert_eq!(value(r#"KEY="a\tb""#).as_deref(), Some(r"a\tb"));
    }

    #[test]
    fn an_unquoted_value_ends_at_the_first_hash() {
        /* dotenv's inline-comment rule, and a trap worth a test: a password containing a hash
        MUST be quoted or it is silently truncated. Pinned, not endorsed - diverging would
        make the same file mean different things to different builds. */
        assert_eq!(
            value("KEY=value # trailing comment").as_deref(),
            Some("value")
        );
        assert_eq!(value("KEY=\"pa#ss\"").as_deref(), Some("pa#ss"));
    }

    #[test]
    fn values_keep_internal_equals_signs() {
        // Base64 tokens and connection strings routinely end in '='.
        assert_eq!(value("KEY=a=b=c").as_deref(), Some("a=b=c"));
    }

    #[test]
    fn comments_and_blank_lines_are_ignored() {
        let data = parse("# a comment\n\nKEY=value\n\n# another\n");
        assert_eq!(data.len(), 1);
        assert_eq!(data.get("KEY"), Some("value"));
    }

    #[test]
    fn an_empty_value_is_an_empty_string_not_a_missing_key() {
        // "set to nothing" and "not set" are different states, and code branches on which.
        let data = parse("KEY=\nOTHER=x");
        assert!(data.contains_key("KEY"));
        assert_eq!(data.get("KEY"), Some(""));
    }

    #[test]
    fn keys_are_case_insensitive_matching_i_configuration() {
        assert_eq!(parse("Discord_Token=abc").get("DISCORD_TOKEN"), Some("abc"));
    }

    #[test]
    fn crlf_files_parse() {
        let data = parse("A=1\r\nB=2\r\n");
        assert_eq!(data.get("A"), Some("1"));
        assert_eq!(data.get("B"), Some("2"));
    }

    /// RELIED ON WHEN SETTING UP A SECOND BOT: its .env starts as a COPY of the first's, and
    /// the safe way to correct that is to APPEND overrides. A change to first-wins would leave
    /// a second bot running on the first bot's token - two gateways on one identity.
    #[test]
    fn a_later_value_overrides_an_earlier_one_for_the_same_key() {
        let parsed = parse(
            "DISCORD_TOKEN=the-first-bots-token\n\
             GUILD_ID=111\n\
             \n\
             # appended overrides\n\
             DISCORD_TOKEN=the-second-bots-token\n\
             GUILD_ID=222\n",
        );

        assert_eq!(parsed.get("DISCORD_TOKEN"), Some("the-second-bots-token"));
        assert_eq!(parsed.get("GUILD_ID"), Some("222"));
    }

    /// How a copied .env turns a feature OFF: `PAYROLL_AMOUNT=` appended beats the figure.
    #[test]
    fn an_appended_blank_value_clears_an_inherited_setting() {
        let parsed = parse("PAYROLL_AMOUNT=500\nPAYROLL_AMOUNT=\n");
        assert_eq!(parsed.get("PAYROLL_AMOUNT"), Some(""));
    }

    // ---- beyond the C# suite ----

    #[test]
    fn a_double_quoted_value_may_span_lines() {
        let parsed = parse("KEY=\"line one\nline two\"\nOTHER=x\n");
        assert_eq!(parsed.get("KEY"), Some("line one\nline two"));
        assert_eq!(parsed.get("OTHER"), Some("x"));
    }
}

mod bot_options_tests {
    use std::time::Duration;

    use super::common::bot;

    const MINIMAL: [(&str, &str); 4] = [
        ("DISCORD_TOKEN", "t"),
        ("RCON_HOST_1", "10.0.0.1"),
        ("RCON_PORT_1", "9100"),
        ("RCON_PASSWORD_1", "p"),
    ];

    fn with(extra: &[(&'static str, &'static str)]) -> Vec<(&'static str, &'static str)> {
        MINIMAL.iter().chain(extra).copied().collect()
    }

    #[test]
    fn servers_are_collected_by_index() {
        let options = bot(&[
            ("RCON_HOST_1", "a"),
            ("RCON_PORT_1", "1"),
            ("RCON_PASSWORD_1", "p1"),
            ("RCON_HOST_2", "b"),
            ("RCON_PORT_2", "2"),
            ("RCON_PASSWORD_2", "p2"),
        ]);

        assert_eq!(options.servers.len(), 2);
        assert_eq!(options.servers[0].name, "server1");
        assert_eq!(options.servers[1].host, "b");
    }

    #[test]
    fn a_zero_health_interval_falls_back_but_a_zero_read_cache_stays_off() {
        // Zero means "off" for the read cache and "a timer that cannot run" for the probe.
        let options = bot(&[
            ("RCON_HOST_1", "a"),
            ("RCON_PORT_1", "1"),
            ("RCON_PASSWORD_1", "p"),
            ("RCON_HEALTH_INTERVAL_MS", "0"),
            ("RCON_READ_CACHE_MS", "0"),
        ]);

        assert_eq!(options.rcon_health_interval, Duration::from_secs(60));
        assert_eq!(options.servers[0].read_cache_duration, Duration::ZERO);
    }

    #[test]
    fn a_gap_in_the_indices_is_not_a_stop_signal() {
        // Server 2 decommissioned, 1 and 3 still running: stopping at the gap drops server 3.
        let options = bot(&[
            ("RCON_HOST_1", "a"),
            ("RCON_PORT_1", "1"),
            ("RCON_PASSWORD_1", "p"),
            ("RCON_HOST_3", "c"),
            ("RCON_PORT_3", "3"),
            ("RCON_PASSWORD_3", "p"),
        ]);

        assert_eq!(options.servers.len(), 2);
        assert_eq!(options.servers[1].name, "server3");
    }

    #[test]
    fn validate_reports_every_problem_at_once() {
        let problems = bot(&[("RCON_HOST_1", "a")]).validate();
        assert!(problems.iter().any(|p| p.contains("DISCORD_TOKEN")));
        assert!(problems.iter().any(|p| p.contains("RCON_PORT_1")));
        assert!(problems.iter().any(|p| p.contains("RCON_PASSWORD_1")));
    }

    #[test]
    fn no_rcon_servers_is_allowed() {
        /* Deleting the last server left a bot that refused to start, and /provisionserver - the
        only way to make a server - needs the bot running. Zero servers must start. */
        let options = bot(&[("DISCORD_TOKEN", "t")]);

        assert!(options.servers.is_empty());
        assert!(options.validate().is_empty());
    }

    #[test]
    fn a_missing_port_or_password_is_named_specifically() {
        let problems = bot(&[("DISCORD_TOKEN", "t"), ("RCON_HOST_2", "a")]).validate();
        assert!(problems.iter().any(|p| p.contains("RCON_PORT_2")));
        assert!(problems.iter().any(|p| p.contains("RCON_PASSWORD_2")));
    }

    #[test]
    fn a_valid_configuration_has_no_problems() {
        assert!(bot(&MINIMAL).validate().is_empty());
    }

    #[test]
    fn an_unset_metrics_port_disables_the_endpoint() {
        let monitoring = bot(&MINIMAL).monitoring;
        assert!(!monitoring.enabled());
        assert!(monitoring.validate().is_empty());
    }

    #[test]
    fn a_port_of_zero_is_rejected_not_silently_treated_as_disabled() {
        /* The tri-state bug the Node bot shipped: `if (!port)` read a configured 0 as "no
        port", and the endpoint never came up with nothing in the log to say why. */
        let monitoring = bot(&with(&[("METRICS_PORT", "0")])).monitoring;
        assert!(monitoring.enabled());
        assert!(!monitoring.validate().is_empty());
    }

    #[test]
    fn metrics_defaults_to_loopback() {
        assert_eq!(bot(&MINIMAL).monitoring.host, "127.0.0.1");
    }

    #[test]
    fn exposing_metrics_without_a_token_is_reported() {
        let monitoring = bot(&with(&[
            ("METRICS_PORT", "9109"),
            ("METRICS_HOST", "0.0.0.0"),
        ]))
        .monitoring;
        assert!(
            monitoring
                .validate()
                .iter()
                .any(|p| p.contains("METRICS_TOKEN"))
        );
    }

    #[test]
    fn exposing_metrics_with_a_token_is_fine() {
        let monitoring = bot(&with(&[
            ("METRICS_PORT", "9109"),
            ("METRICS_HOST", "0.0.0.0"),
            ("METRICS_TOKEN", "s"),
        ]))
        .monitoring;
        assert!(monitoring.validate().is_empty());
    }

    #[test]
    fn the_read_cache_duration_is_configurable_and_reaches_every_server() {
        let options = bot(&with(&[("RCON_READ_CACHE_MS", "500")]));
        assert!(
            options
                .servers
                .iter()
                .all(|s| s.read_cache_duration == Duration::from_millis(500))
        );
    }

    #[test]
    fn an_unparseable_interval_falls_back_rather_than_crashing() {
        let options = bot(&with(&[("RCON_READ_CACHE_MS", "soon")]));
        assert_eq!(
            options.servers[0].read_cache_duration,
            Duration::from_millis(2500)
        );
    }

    /* ─── COMMANDS_DISABLED ───
    A server that does not run the police RP has no use for /arrest, /warrant or /bail. The
    parsing is what these pin; the filtering lives with the command registry (M7). */

    #[test]
    fn no_commands_are_disabled_by_default() {
        // Every existing deployment keeps every command without configuring anything.
        assert!(bot(&MINIMAL).disabled_commands.is_empty());
    }

    #[test]
    fn the_disabled_list_is_split_and_trimmed() {
        let options = bot(&with(&[("COMMANDS_DISABLED", "arrest, warrant ,bail")]));
        assert_eq!(options.disabled_commands, ["arrest", "warrant", "bail"]);
    }

    /// "/arrest" is what somebody copies out of Discord. A list that silently failed to match
    /// it would leave the command enabled and look like the setting being ignored.
    #[test]
    fn a_leading_slash_is_accepted() {
        assert_eq!(
            bot(&with(&[("COMMANDS_DISABLED", "/arrest,/warrant")])).disabled_commands,
            ["arrest", "warrant"]
        );
    }

    #[test]
    fn empty_entries_from_a_trailing_comma_are_dropped() {
        // The commonest .env typo; an empty name would match nothing while counting as one.
        assert_eq!(
            bot(&with(&[("COMMANDS_DISABLED", "arrest,,  ,")])).disabled_commands,
            ["arrest"]
        );
    }

    #[test]
    fn an_env_file_readable_by_others_is_flagged() {
        const OWNER_ONLY: u32 = 0o600;

        assert!(!pavlov_config::env_file_too_open(OWNER_ONLY));
        assert!(pavlov_config::env_file_too_open(OWNER_ONLY | 0o040));
        assert!(pavlov_config::env_file_too_open(OWNER_ONLY | 0o004));
    }

    // ---- beyond the C# suite ----

    #[test]
    fn the_data_directory_defaults_under_the_working_directory() {
        assert_eq!(
            bot(&MINIMAL).data_directory,
            std::path::Path::new("/srv/bot/data")
        );
        assert_eq!(
            bot(&with(&[("DATA_DIR", " /var/lib/pavlov ")])).data_directory,
            std::path::Path::new("/var/lib/pavlov")
        );
    }

    #[test]
    fn an_interval_too_big_for_a_timespan_falls_back_rather_than_crashing_startup() {
        // .NET throws OverflowException from TimeSpan.FromMilliseconds for these.
        for huge in ["Infinity", "1e300", "NaN"] {
            let options = bot(&with(&[("RCON_HEALTH_INTERVAL_MS", huge)]));
            assert_eq!(
                options.rcon_health_interval,
                Duration::from_secs(60),
                "{huge}"
            );
        }
    }

    #[test]
    fn secrets_never_print() {
        let options = bot(&with(&[("FACTION_BOT_TOKEN", "faction-secret")]));
        let printed = format!("{options:?}");
        for secret in ["faction-secret", "\"p\""] {
            assert!(!printed.contains(secret), "{secret} leaked: {printed}");
        }
        assert_eq!(options.servers[0].password.expose(), "p");
    }
}

mod layering_tests {
    //! The .env with the real environment over it, as the C# composes `AddDotEnvFile` then
    //! `AddEnvironmentVariables`.

    use std::ffi::OsString;

    use pavlov_config::Config;

    fn os(pairs: &[(&str, &str)]) -> Vec<(OsString, OsString)> {
        pairs
            .iter()
            .map(|(k, v)| ((*k).into(), (*v).into()))
            .collect()
    }

    #[test]
    fn the_environment_wins_over_the_env_file_ignoring_case() {
        let dir = std::env::temp_dir().join(format!("pavlov-config-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join(".env");
        std::fs::write(&path, "DISCORD_TOKEN=from-file\nGUILD_ID=111\n").unwrap();

        let config = Config::load(&path, os(&[("discord_token", "from-environment")])).unwrap();

        assert_eq!(config.get("DISCORD_TOKEN"), Some("from-environment"));
        assert_eq!(config.get("GUILD_ID"), Some("111"));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn a_missing_env_file_is_not_an_error() {
        let config = Config::load(
            std::path::Path::new("/nonexistent/pavlov/.env"),
            os(&[("DISCORD_TOKEN", "t")]),
        )
        .unwrap();
        assert_eq!(config.get("DISCORD_TOKEN"), Some("t"));
    }

    #[test]
    fn the_config_never_prints_its_values() {
        let config = Config::from_pairs([("DISCORD_TOKEN", "super-secret")]);
        let printed = format!("{config:?}");
        assert!(printed.contains("DISCORD_TOKEN"));
        assert!(!printed.contains("super-secret"));
    }
}
