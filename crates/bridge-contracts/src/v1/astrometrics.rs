//! Local companion protocol. Collection is an explicit, session-only choice.
use super::primitives::*;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SetAstrometricsInput {
    pub enabled: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum AstrometricsNotice {
    Disabled,
    Listening,
    Unavailable,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AstrometricsEvent {
    pub row_id: ProgressCount,
    #[schemars(length(max = 128))]
    pub source_session: String,
    #[schemars(length(max = 64))]
    pub stream: String,
    #[schemars(length(max = 20))]
    pub sequence: String,
    #[schemars(length(max = 128))]
    pub event_type: String,
    #[schemars(length(max = 20))]
    pub utc_ms: Option<String>,
    #[schemars(length(max = 256))]
    pub summary: String,
    #[schemars(length(max = 512))]
    pub payload_json: String,
    pub payload_truncated: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct AstrometricsView {
    pub available: bool,
    pub enabled: bool,
    pub notice: AstrometricsNotice,
    pub stored_events: ProgressCount,
    pub rejected_requests: ProgressCount,
    #[schemars(length(max = 128))]
    pub endpoint: Option<String>,
    /// Revealed only on this explicit local collection page, never a diagnostic export.
    pub sync_token: Option<HostEpoch>,
    pub events: BoundedList<AstrometricsEvent, 30>,
}
impl AstrometricsView {
    pub(crate) fn valid(&self) -> bool {
        (match self.notice {
            AstrometricsNotice::Listening => self.enabled && self.available,
            AstrometricsNotice::Disabled => !self.enabled && self.available,
            AstrometricsNotice::Unavailable => !self.available,
        }) && self.endpoint.is_some() == self.enabled
            && self.sync_token.is_some() == self.enabled
            && self
                .endpoint
                .as_ref()
                .is_none_or(|s| s == "http://127.0.0.1:58237/sync")
            && self.events.as_slice().iter().all(|e| {
                e.source_session.len() <= 128
                    && e.stream.len() <= 64
                    && e.sequence.len() <= 20
                    && e.event_type.len() <= 128
                    && e.summary.len() <= 256
                    && e.payload_json.len() <= 512
                    && e.utc_ms.as_ref().is_none_or(|s| s.len() <= 20)
            })
    }
}
