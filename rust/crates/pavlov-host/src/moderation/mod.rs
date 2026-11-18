//! Bans and the rest of moderation's machinery (C# `Host/Moderation`). The rules themselves are
//! pure and live in `pavlov_core::moderation`.

pub mod alerts;
pub mod audit;
pub mod ban_file;
pub mod ban_firewall;
pub mod bans;
pub mod evasion;
pub mod master_names;

pub use alerts::{
    AlertDelivery, SecurityAlert, SecurityAlertKind, SecurityAlertSink, SecurityAlerts,
};
pub use audit::{AuditLog, ModAction, StaffLogSink};
pub use ban_file::{BanFileEntry, BanFileLookup, BanFileStatus, ServerBanFile};
pub use ban_firewall::{BanFirewall, BanFirewallPass, FirewalledBan};
pub use bans::{BanEvidence, BanFileExport, BanService, EnforcementResult, MasterNames};
pub use evasion::{AutoBanOutcome, EvasionResponder};
pub use master_names::StoredMasterNames;

/// An id-to-name lookup, from the account registry.
pub type ResolveName = std::sync::Arc<dyn Fn(&str) -> Option<String> + Send + Sync>;
