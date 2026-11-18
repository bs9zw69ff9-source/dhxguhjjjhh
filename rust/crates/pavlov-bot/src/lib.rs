//! The composition root (C# `Program.cs`): the startup checks, the object graph, the
//! background services, and the run itself.

#![forbid(unsafe_code)]

pub mod graph;
pub mod run;
pub mod services;
pub mod startup;
