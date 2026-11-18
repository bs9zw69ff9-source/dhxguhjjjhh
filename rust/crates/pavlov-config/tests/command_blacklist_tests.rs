//! Port of `CommandBlacklistTests.cs`, the setting half (`BLACKLIST_IDS`). The bar itself, in
//! the interaction dispatcher, arrives with `pavlov-discord` (M7).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use common::features;

const BARRED: u64 = 678_362_059_905_171_471;
const STRANGER: u64 = 555_000_000_000_000_001;

#[test]
fn blacklist_ids_reads_the_node_bots_separators() {
    // Spaces as well as commas, as the Node bot split it - "1 2" must not be one bad id.
    for value in [
        "678362059905171471 555000000000000001",
        "678362059905171471,555000000000000001",
        " 678362059905171471 ,\t555000000000000001 ",
    ] {
        assert_eq!(
            features(&[("BLACKLIST_IDS", value)]).barred_user_ids,
            [BARRED, STRANGER],
            "{value:?}"
        );
    }
}
