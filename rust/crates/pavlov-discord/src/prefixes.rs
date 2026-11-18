//! Every component custom-id prefix the bot routes on, in one place (C# each command's `Id`).
//!
//! THEY MUST NOT COLLIDE: a button is routed by its prefix, and two handlers claiming one went
//! to whichever registered first, so a menu claim could land in the arrest booking silently.
//! [`ALL`] is what the router and the test check for duplicates.

/// `/arrest`'s booking flow.
pub const ARREST_BOOKING: &str = "arr";
/// Paged replies' previous and next buttons.
pub const PAGED: &str = "page";
/// `/configure`.
pub const CONFIG_PANEL: &str = "cfg";
/// `/server lookup`'s refresh button.
pub const SERVER_LOOKUP: &str = "srv";
/// `/whitelist wipe`'s confirmation.
pub const WHITELIST_WIPE: &str = "wlwipe";
/// Account verification.
pub const VERIFY: &str = pavlov_core::verification::COMPONENT_PREFIX;

/// Every prefix.
pub const ALL: [&str; 6] = [
    ARREST_BOOKING,
    PAGED,
    CONFIG_PANEL,
    SERVER_LOOKUP,
    WHITELIST_WIPE,
    VERIFY,
];
