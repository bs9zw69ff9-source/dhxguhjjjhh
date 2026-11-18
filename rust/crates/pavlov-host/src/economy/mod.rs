//! The economy's services (C# `Host/Economy`). The ledger itself is pure, in
//! `pavlov_core::economy`; the ModSave file store is `pavlov_storage::ledger_store`.

pub mod payroll;

pub use payroll::{Payroll, PayrollParts, PayrollRun, PayrollState, SharedLedger};
