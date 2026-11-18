//! Port of `FactionBotTests.cs`: FactionBotTests and BanFilePathTests.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

mod faction_bot_tests {
    //! The whitelist bot: a second Discord application owning the whitelist commands. The C#
    //! port first read neither setting, so after its cutover that application never logged in
    //! and its four commands silently moved to the main bot.

    use pavlov_config::BotOptions;

    use super::common::bot;

    fn bind(settings: &[(&str, &str)]) -> BotOptions {
        let mut pairs = settings.to_vec();
        pairs.push(("DISCORD_TOKEN", "main-token"));
        bot(&pairs)
    }

    #[test]
    fn both_halves_are_needed() {
        /* A token with no client id cannot register commands, and a client id with no token
        cannot log in. Either alone as "enabled" starts a bot that connects and does
        nothing, which looks broken rather than misconfigured. */
        assert!(!bind(&[("FACTION_BOT_TOKEN", "t")]).faction_bot_enabled());
        assert!(!bind(&[("FACTION_CLIENT_ID", "123456789012345678")]).faction_bot_enabled());
        assert!(
            bind(&[
                ("FACTION_BOT_TOKEN", "t"),
                ("FACTION_CLIENT_ID", "123456789012345678")
            ])
            .faction_bot_enabled()
        );
    }

    #[test]
    fn it_is_off_by_default() {
        // Most servers run one bot. Off must be the default, and off must be silent.
        let options = bind(&[]);

        assert!(!options.faction_bot_enabled());
        assert!(options.faction_token.is_none());
        assert!(options.faction_client_id.is_none());
    }

    #[test]
    fn a_blank_token_is_not_a_token() {
        // An empty FACTION_BOT_TOKEN= line is how somebody turns this off.
        assert!(
            !bind(&[
                ("FACTION_BOT_TOKEN", "   "),
                ("FACTION_CLIENT_ID", "123456789012345678")
            ])
            .faction_bot_enabled()
        );
    }

    #[test]
    fn a_malformed_client_id_is_not_enabled() {
        // Rather than parsing to zero and registering commands against application 0.
        assert!(
            !bind(&[
                ("FACTION_BOT_TOKEN", "t"),
                ("FACTION_CLIENT_ID", "not-a-snowflake")
            ])
            .faction_bot_enabled()
        );
    }

    #[test]
    fn the_faction_token_is_not_the_main_token() {
        /* Separate applications. Logging the second client in with the main token connects two
        gateways to ONE application, which answers every command twice. */
        let options = bind(&[
            ("FACTION_BOT_TOKEN", "faction-token"),
            ("FACTION_CLIENT_ID", "123456789012345678"),
        ]);

        assert_eq!(options.discord_token.expose(), "main-token");
        assert_eq!(options.faction_token.unwrap().expose(), "faction-token");
    }
}

mod ban_file_path_tests {
    //! Where the ban file lives: `Config/blacklist.txt`, the file the SERVER reads.
    //! `MODSAVE_BLACKLIST_PATH` is retired; `BLACKLIST_PATH` is the override.

    use super::common::features;

    const DEFAULT: &str = "/home/steam/pavlovserver/Pavlov/Saved/Config/blacklist.txt";

    #[test]
    fn an_explicit_override_wins() {
        let options = features(&[
            ("BLACKLIST_PATH", "/custom/blacklist.txt"),
            ("PAVLOV_BASE_1", "/home/steam/pavlovserver"),
            (
                "MODSAVE_PATH",
                "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave",
            ),
        ]);

        assert_eq!(
            options.ban_file_path.as_deref(),
            Some("/custom/blacklist.txt")
        );
    }

    #[test]
    fn the_retired_name_no_longer_chooses_the_file() {
        /* IT USED TO WIN, and that kept the original bug alive: every .env that had set it went
        on syncing a mod's file while the server enforced Config/blacklist.txt. */
        let options = features(&[(
            "MODSAVE_BLACKLIST_PATH",
            "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/banlist.txt",
        )]);

        assert_eq!(options.ban_file_path.as_deref(), Some(DEFAULT));
    }

    #[test]
    fn the_retired_name_is_carried_so_startup_can_say_it_is_ignored() {
        // Silently ignoring a setting somebody wrote is worse than honouring a wrong one.
        let options = features(&[("MODSAVE_BLACKLIST_PATH", "/legacy/banlist.txt")]);

        assert_eq!(
            options.ignored_ban_file_path.as_deref(),
            Some("/legacy/banlist.txt")
        );
        assert!(
            features(&[("BLACKLIST_PATH", "/new/blacklist.txt")])
                .ignored_ban_file_path
                .is_none()
        );
    }

    #[test]
    fn sync_can_be_turned_off_outright() {
        /* A SECOND BOT SHARING ONE INSTALL MUST NOT MANAGE THIS FILE - the export rewrites it
        whole from one store, so two of them erase each other every five minutes. Blank never
        disabled anything, because the default fills it in. */
        assert!(
            features(&[
                ("BLACKLIST_SYNC", "false"),
                ("BLACKLIST_PATH", "/custom/blacklist.txt")
            ])
            .ban_file_path
            .is_none()
        );
        assert!(
            features(&[("BLACKLIST_SYNC", "true")])
                .ban_file_path
                .is_some()
        );
        assert!(features(&[]).ban_file_path.is_some());
    }

    #[test]
    fn the_new_name_wins_when_both_are_set() {
        let options = features(&[
            ("BLACKLIST_PATH", "/new/blacklist.txt"),
            ("MODSAVE_BLACKLIST_PATH", "/legacy/blacklist.txt"),
        ]);

        assert_eq!(options.ban_file_path.as_deref(), Some("/new/blacklist.txt"));
    }

    #[test]
    fn it_is_derived_from_the_install_root_not_from_the_ledger_directory() {
        /* The ledger directory IS .../Config/ModSave, and this used to be built from it, which
        produced .../Config/ModSave/ModSave/banlist.txt. It comes off the install root. */
        let options = features(&[
            ("PAVLOV_BASE_1", "/home/steam/pavlovserver"),
            (
                "MODSAVE_PATH",
                "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave",
            ),
        ]);

        let path = options.ban_file_path.unwrap();
        assert_eq!(path, DEFAULT);
        assert!(!path.contains("ModSave"));
    }

    #[test]
    fn the_default_is_the_file_the_server_reads() {
        /* Config/blacklist.txt, beside mods.txt and whitelist.txt. It used to default to a mod's
        ModSave/banlist.txt: the bot synced that while the server read this one, so a player
        listed here was refused by the server while /checkban said he was not banned. */
        assert_eq!(features(&[]).ban_file_path.as_deref(), Some(DEFAULT));
    }
}
