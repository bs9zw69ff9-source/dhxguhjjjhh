//! `/arrest`'s pure parts (C# `ArrestCommand` in `PoliceCommands.cs`): an officer's own time
//! replacing the computed one, and the receipt line each charge gets.

use pavlov_core::penal::{BailRule, Booking, Charge};
use pavlov_core::skin::lore;

/// An officer's time instead of the computed one. HELD TO THE CAP like any other sentence:
/// an officer wanting longer needs the cap raised, which is a deliberate policy act. Bail and
/// bailability are left alone, so an override never makes a non-bailable booking payable.
#[must_use]
pub fn override_time(booking: &Booking, minutes: i32, cap_minutes: i32) -> Booking {
    let wanted = minutes.max(0);
    let capped = cap_minutes > 0 && wanted > cap_minutes;
    Booking {
        jail_minutes: if capped { cap_minutes } else { wanted },
        capped,
        cap_minutes: capped.then_some(cap_minutes),
        ..booking.clone()
    }
}

/// `2 min, $10` / `3 min, no bail` / `no jail, $50`: the price, or that there is none - the
/// difference between "free" and "you cannot pay this" is the whole point of the receipt.
#[must_use]
pub fn charge_line(charge: &Charge, rate: f64) -> String {
    let sentence = if charge.sentence_ranges {
        "ranges with the associated charge".to_owned()
    } else if charge.jail_minutes > 0 {
        format!("{} min", charge.jail_minutes)
    } else {
        "no jail".to_owned()
    };
    let price = match charge.bail_at(rate) {
        Some(bail) => lore::amount(i64::from(bail)),
        None if charge.rule == BailRule::Associated => "bail from the associated charge".to_owned(),
        None => "no bail".to_owned(),
    };
    format!("{sentence}, {price}")
}
