//! Address classification that has to happen before any lookup is spent (C#
//! `Core/Vpn/IpAddresses.cs`).

use std::net::{IpAddr, Ipv4Addr};

use chrono::{DateTime, Utc};

use super::{VpnDecision, VpnRecord};

/// Whether an address is private, loopback, link-local or otherwise unroutable.
///
/// Players on the server's LAN connect from a private address. No provider can say anything
/// about one, and asking costs a lookup per detector per join - on a keyless 100/day tier, one
/// LAN player reconnecting twenty times is the day's quota.
///
/// Anything that does not parse as an address is treated as unroutable too, so nothing is
/// spent looking it up. (.NET's parser also accepts shorthand like "127.1"; the log parser
/// only ever hands over full dotted quads, so the stricter parse here changes nothing real.)
#[must_use]
pub fn is_unroutable(ip: Option<&str>) -> bool {
    let text = ip.unwrap_or_default().trim();
    let Ok(address) = text.parse::<IpAddr>() else {
        return true;
    };

    let v4 = match address {
        IpAddr::V4(v4) => v4,
        IpAddr::V6(v6) => {
            if v6.is_loopback() || v6.is_multicast() {
                return true;
            }
            let segments = v6.segments();
            // fe80::/10 link-local, fec0::/10 site-local, fc00::/7 unique local.
            if (segments[0] & 0xffc0) == 0xfe80
                || (segments[0] & 0xffc0) == 0xfec0
                || (segments[0] & 0xfe00) == 0xfc00
            {
                return true;
            }
            // An IPv4-mapped address is really an IPv4 one; classify it as such.
            match v6.to_ipv4_mapped() {
                Some(v4) => v4,
                None => return false,
            }
        }
    };
    is_unroutable_v4(v4)
}

fn is_unroutable_v4(address: Ipv4Addr) -> bool {
    let [a, b, _, _] = address.octets();
    match a {
        0 | 10 | 127 => true,          // this-network, private, loopback
        169 => b == 254,               // link-local
        172 => (16..=31).contains(&b), // private
        192 => b == 168,               // private
        224..=255 => true,             // multicast and reserved
        _ => false,
    }
}

/// The record stored for an address nobody can check.
#[must_use]
pub fn local_record(ip: &str, checked_at: DateTime<Utc>) -> VpnRecord {
    VpnRecord {
        ip: ip.to_owned(),
        decision: VpnDecision::new(
            false,
            None,
            false,
            "private/unroutable address - detection skipped",
        ),
        local: true,
        checked_at,
        country: Some("Local network".to_owned()),
        isp: Some("private address".to_owned()),
        ..VpnRecord::default()
    }
}
