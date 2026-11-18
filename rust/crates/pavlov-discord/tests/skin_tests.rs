//! Port of `SkinTests.cs`, the Discord half: the boards and the money commands in the police
//! skin's words. The vocabulary itself is pinned in `pavlov-core`, `THEME` in `pavlov-config`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::embed::palette;
use pavlov_core::skin::theme::brand_name;
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::time::SystemClock;
use pavlov_discord::boards::{Boards, cash_table};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;

fn boards() -> Boards {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::default())));
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    Boards::new(store, rcon, None, None, Arc::new(SystemClock))
}

#[test]
fn the_police_skin_is_the_gwpd() {
    assert_eq!(brand_name(), "GWPD");
    assert_eq!(
        theme::notice("x", None).brand(None).footer.as_deref(),
        Some("🚔 GWPD")
    );
    assert_eq!(lore::amount(1250), "$1,250");
    assert_eq!(lore::head_count(12), "12 players");
    assert_eq!(lore::richest_heading(10), "Top 10 richest players");
    let warrants = boards().build_warrant_board();
    assert_eq!(warrants.title.as_deref(), Some("✅ 🚨 No active warrants"));
    assert_eq!(
        warrants.description.as_deref(),
        Some("Nobody is currently wanted.")
    );
    assert_eq!(
        boards().build_arrest_board().title.as_deref(),
        Some("ℹ️ 🔒 Most wanted")
    );
    assert_eq!(lore::warrants_heading(), "Warrants");
    assert_eq!(palette::BLUE, 0x60_A5_FA);
}

#[test]
fn the_cash_table_is_headed_in_the_skins_words() {
    assert!(
        cash_table(&[("Alice".into(), 5)], 1000)
            .0
            .contains("Balance")
    );
}

#[test]
fn the_police_bot_types_give_money_not_give_caps() {
    // The money commands take their names from the skin (the M8 commands read these).
    assert_eq!(police().words.give_money_command, "givemoney");
    assert_eq!(police().words.adjust_money_command, "adjustmoney");
}
