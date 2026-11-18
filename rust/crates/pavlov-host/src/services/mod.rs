//! The background services and their supervisor (C# `Host/Services`).

pub mod registry;

pub use registry::{
    HealthCheck, RegistryError, ServiceDefinition, ServiceFailure, ServiceRegistry, ServiceState,
    ServiceStatus, TickError, Work,
};
