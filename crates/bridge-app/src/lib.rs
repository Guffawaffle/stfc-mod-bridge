//! Backend application services shared by CLI and native UI composition.
//!
//! These providers establish host-local clock and identity inputs. They create
//! no journal, engine host, game target, permission or release authority.
//! Native persistence and production dispatcher construction remain separate.
mod providers;

pub use providers::{NativeClock, NativeIdentitySource};
