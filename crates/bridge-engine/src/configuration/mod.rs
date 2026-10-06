//! Actor-local configuration custody. Paths, native handles and source bytes
//! never enter the public protocol. Native owners implement persistence ports;
//! this module neither creates a catalog nor writes through `std::fs`.
mod drafts;
mod ports;
mod schema;
mod toml;
mod transaction;
mod types;

pub use drafts::ConfigurationWorkspace;
pub use ports::*;
pub use schema::{
    AdoptedSchema, AliasPolicy, FieldPolicy, ResolvedSyncEdit, SchemaSource, SemanticMutation,
    SyncProjectedProxy, SyncProjection,
};
pub use toml::{CanonicalToml, TomlPreparation};
pub use transaction::{
    ConfigurationRecoveryTransaction, ConfigurationTransaction, PreparedConfiguration,
    RecoveryConfiguration,
};
pub use types::*;
