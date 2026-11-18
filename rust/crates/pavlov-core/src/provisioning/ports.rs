//! The default ports for server N, stepped so a second server is not a copy of the first (C#
//! `Core/Provisioning/ServerPortDefaults.cs`).
//!
//! EVERY DEFAULT IS A FUNCTION OF THE SLOT. They used to be three fixed constants, so
//! provisioning a second server proposed the first server's ports, and two servers sharing a
//! game port do not fail cleanly - the second one loses the bind and is just absent.
//!
//! - GAME PORT steps by one (7777, 7778, ...), the Pavlov wiki's convention.
//! - QUERY PORT is the game port PLUS 400, Pavlov's own offset, so it stays paired even when the
//!   operator names a game port by hand.
//! - RCON PORT steps by one hundred (9100, 9200, ...): round numbers an operator types, with room
//!   for game ports to grow without walking into the RCON range.

/// Server 1's game port, the wiki's default.
pub const BASE_GAME_PORT: i32 = 7777;
/// Pavlov's own game-to-query offset: 7777 pairs with 8177.
pub const QUERY_PORT_OFFSET: i32 = 400;
/// Server 1's RCON port.
pub const BASE_RCON_PORT: i32 = 9100;
/// How far apart consecutive servers' RCON ports sit.
pub const RCON_PORT_STRIDE: i32 = 100;

/// The default game port for a 1-based slot.
#[must_use]
pub const fn game_port(slot: i32) -> i32 {
    BASE_GAME_PORT + (slot - 1)
}

/// The query port for a GAME PORT (not a slot), so a hand-picked game port keeps its pair.
#[must_use]
pub const fn query_port_for(game_port: i32) -> i32 {
    game_port + QUERY_PORT_OFFSET
}

/// The default RCON port for a 1-based slot.
#[must_use]
pub const fn rcon_port(slot: i32) -> i32 {
    BASE_RCON_PORT + (slot - 1) * RCON_PORT_STRIDE
}
