//! Protocol v1 describes requested and observed work; it grants no native authority.
//! Untrusted transports must use `decode_*`, not raw DTO deserialization.
mod codec;
mod configuration;
mod distribution;
mod management;
mod outcomes;
mod primitives;
mod projections;
mod targets;
mod wire;
mod workflow;

pub use codec::{
    DecodeFailure, MAX_CONTAINER_DEPTH, MAX_MESSAGE_BYTES, ValidatedEvent, ValidatedReply,
    ValidatedRequest, decode_event, decode_reply, decode_request, diagnostic_preview_digest,
    schema_event, schema_reply, schema_request, semantic_plan_digest,
};
pub use configuration::*;
pub use distribution::*;
pub use management::*;
pub use outcomes::*;
pub use primitives::*;
pub use projections::*;
pub use targets::*;
pub use wire::*;
pub use workflow::*;

pub(crate) fn unique<T: PartialEq>(items: &[T]) -> bool {
    items
        .iter()
        .enumerate()
        .all(|(i, item)| !items[..i].contains(item))
}
pub(crate) fn unique_by<T, K: PartialEq>(items: &[T], key: impl Fn(&T) -> K) -> bool {
    items
        .iter()
        .enumerate()
        .all(|(i, item)| !items[..i].iter().any(|other| key(item) == key(other)))
}
