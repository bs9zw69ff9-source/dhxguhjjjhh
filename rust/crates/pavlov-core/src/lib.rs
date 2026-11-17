//! Pure domain rules of the Pavlov bot, ported from `dotnet/src/PavlovBot.Core`.
//!
//! Nothing here touches Discord, a socket, the filesystem or the clock directly: every
//! input, the current time included, is passed in. That is what lets every rule be tested
//! without a server, and it is the same boundary the C# `PavlovBot.Core` project keeps.

mod patterns;

pub mod text;
pub mod time;
