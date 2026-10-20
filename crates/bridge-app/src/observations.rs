//! Production read-only application host. Both the CLI and desktop use this
//! dispatcher and the existing v1 protocol. It admits no game/catalog mutations.
pub mod native;
mod projection;
pub use projection::ProfilesObservations;

use crate::{NativeClock, NativeIdentitySource};
use bridge_contracts::v1::*;
use bridge_engine::{
    host::{HostFailure, LocalHost},
    operations::HostClock,
};
use serde_json::Value;
use std::collections::VecDeque;

pub struct Views {
    pub profiles: Observation<Inventory<ProfileProjection>>,
    pub installations: Observation<Inventory<InstallationProjection>>,
    pub sessions: Observation<Inventory<SessionProjection>>,
}
pub trait ObservationSource {
    fn read(&mut self, evidence: &Evidence) -> Views;
}
pub fn unavailable<T>(reason: ObservationReason) -> Observation<T> {
    Observation::Unavailable { reason }
}
pub fn wire_value<T: serde::de::DeserializeOwned>(value: Value) -> Result<T, HostFailure> {
    serde_json::from_value(value).map_err(|_| HostFailure::InvalidObservation)
}
pub(crate) fn semantic_value(mut value: Value) -> Value {
    fn strip(value: &mut Value) {
        match value {
            Value::Object(map) => {
                map.remove("evidence");
                map.remove("cursor");
                for child in map.values_mut() {
                    strip(child);
                }
            }
            Value::Array(items) => {
                for child in items {
                    strip(child);
                }
            }
            _ => {}
        }
    }
    strip(&mut value);
    value
}
fn list<T, const N: usize>(items: Vec<T>) -> BoundedList<T, N> {
    BoundedList::new(items).expect("internally bounded collection")
}
fn error(code: ErrorCode) -> Box<BridgeError> {
    Box::new(BridgeError {
        code,
        retry_disposition: RetryDisposition::Never,
        supported_versions: None,
        violations: list(vec![]),
        expected_revision: None,
        observed_revision: None,
        recovery: None,
    })
}

pub struct ReadOnlyApplication<S: ObservationSource> {
    source: S,
    clock: NativeClock,
    cursor: Cursor,
    events: VecDeque<Event>,
    signature: Option<Value>,
    closing: bool,
}
impl<S: ObservationSource> ReadOnlyApplication<S> {
    pub fn new(source: S) -> Result<Self, HostFailure> {
        let mut ids = NativeIdentitySource::new();
        Ok(Self {
            source,
            clock: NativeClock::new(),
            cursor: Cursor {
                host_epoch: ids
                    .host_epoch()
                    .map_err(|_| HostFailure::UnavailableService)?,
                stream_id: ids
                    .stream_id()
                    .map_err(|_| HostFailure::UnavailableService)?,
                sequence: Sequence::new(0),
            },
            events: VecDeque::new(),
            signature: None,
            closing: false,
        })
    }
    fn evidence(&self) -> Result<Evidence, HostFailure> {
        Ok(Evidence {
            observation_id: ObservationId::new(self.cursor.host_epoch.as_str())
                .map_err(|_| HostFailure::InvalidObservation)?,
            observed_at: self
                .clock
                .now()
                .map_err(|_| HostFailure::UnavailableService)?
                .utc,
            source: EvidenceSource::NativeLive,
        })
    }
    fn snapshot(&mut self) -> Result<Snapshot, HostFailure> {
        let evidence = self.evidence()?;
        let views = self.source.read(&evidence);
        let mut snapshot = Snapshot {
            cursor: self.cursor.clone(),
            preferences: unavailable(ObservationReason::NativeUnavailable),
            profiles: views.profiles,
            installations: views.installations,
            sessions: views.sessions,
            operations: Inventory {
                items: list(vec![]),
                completeness: Completeness::Complete,
                issues: list(vec![]),
                revision: OpaqueRevision::new("read-only-host").unwrap(),
            },
            capabilities: Inventory {
                items: list(vec![]),
                completeness: Completeness::Partial,
                issues: list(vec![ProjectionIssue {
                    code: ProjectionIssueCode::NativeUnavailable,
                    resource: None,
                }]),
                revision: OpaqueRevision::new("read-only-host").unwrap(),
            },
        };
        let signature =
            serde_json::to_value(&snapshot).map_err(|_| HostFailure::InvalidObservation)?;
        // Evidence time is freshness, not a domain change. The original snapshot
        // retains it; only comparison ignores evidence and the stream watermark.
        let signature = semantic_value(signature);
        if self.signature.as_ref() != Some(&signature) {
            self.cursor.sequence = Sequence::new(
                self.cursor
                    .sequence
                    .get()
                    .checked_add(1)
                    .ok_or(HostFailure::InvalidObservation)?,
            );
            self.signature = Some(signature);
            self.events.push_back(Event {
                protocol_version: ProtocolVersion,
                cursor: self.cursor.clone(),
                body: EventBody::SnapshotInvalidated {
                    reason: SnapshotInvalidationReason::SessionChanged,
                },
            });
            if self.events.len() > 16 {
                self.events.pop_front();
            }
        }
        snapshot.cursor = self.cursor.clone();
        Ok(snapshot)
    }
    fn query(&mut self, query: Query) -> Result<QueryResult, Box<BridgeError>> {
        let failed = |_| error(ErrorCode::InternalFailure);
        match query {
            Query::Hello(_) => Ok(QueryResult::Hello(HelloResult {
                supported_versions: [ProtocolVersion],
                host_epoch: self.cursor.host_epoch.clone(),
                host_kind: if cfg!(windows) {
                    HostKind::WindowsX64
                } else {
                    HostKind::MacosArm64
                },
                implemented_commands: list(vec![CommandId::RequestHostClose]),
            })),
            Query::Snapshot(_) => self.snapshot().map(QueryResult::Snapshot).map_err(failed),
            Query::ListInstallations(_) => self
                .snapshot()
                .map(|s| QueryResult::ListInstallations(s.installations))
                .map_err(failed),
            Query::ListSessions(_) => self
                .snapshot()
                .map(|s| QueryResult::ListSessions(s.sessions))
                .map_err(failed),
            Query::ListProfiles(input) if input.state != ProfileFilter::Active => Ok(
                QueryResult::ListProfiles(unavailable(ObservationReason::NativeUnavailable)),
            ),
            Query::ListProfiles(_) => self
                .snapshot()
                .map(|s| QueryResult::ListProfiles(s.profiles))
                .map_err(failed),
            Query::GetActions(input) => Ok(QueryResult::GetActions(list(
                input
                    .actions
                    .as_slice()
                    .iter()
                    .map(|action| ActionProjection {
                        action: *action,
                        availability: ActionAvailability::Unavailable {
                            reason: AvailabilityReason {
                                code: AvailabilityReasonCode::UnavailableNativeRoute,
                                resource: None,
                                remediation: None,
                            },
                        },
                    })
                    .collect(),
            ))),
            Query::ResolveTarget(input) => {
                let snapshot = self.snapshot().map_err(failed)?;
                let evidence = self.evidence().map_err(failed)?;
                let target = resolve(&snapshot, &input.target).unwrap_or(Observation::Unknown {
                    reason: ObservationReason::UnrecognizedIdentity,
                    evidence,
                });
                Ok(QueryResult::ResolveTarget(ResolveTargetResult { target }))
            }
            Query::ResumeEvents(input) => {
                let mut batch = self
                    .events_after(&input.after)
                    .map_err(|_| error(ErrorCode::ResnapshotRequired))?;
                let events = batch
                    .events
                    .as_slice()
                    .iter()
                    .take(input.maximum_events.get() as usize)
                    .cloned()
                    .collect::<Vec<_>>();
                batch.next = events
                    .last()
                    .map_or_else(|| input.after.clone(), |event| event.cursor.clone());
                batch.events = list(events);
                Ok(QueryResult::ResumeEvents(batch))
            }
            _ => Err(error(ErrorCode::UnsupportedCapability)),
        }
    }
}
fn resolve(snapshot: &Snapshot, selector: &TargetSelector) -> Option<Observation<ResolvedTarget>> {
    let InstallationSelector::Registered {
        id,
        directory_assertion: None,
        revision_assertion,
    } = &selector.installation
    else {
        return None;
    };
    let ProfileSelector::Ordinary {
        catalog_id_assertion,
    } = &selector.profile
    else {
        return None;
    };
    let Observation::Observed {
        value: installations,
        evidence,
    } = &snapshot.installations
    else {
        return None;
    };
    let installation = installations.items.as_slice().iter().find(|item| matches!(&item.binding,
        InstallationBinding::Registered { registration_id, registration_revision, .. }
        if registration_id == id && revision_assertion.as_ref().is_none_or(|revision| revision == registration_revision)))?;
    let Observation::Observed {
        value: profiles, ..
    } = &snapshot.profiles
    else {
        return None;
    };
    for profile in profiles.items.as_slice() {
        if let ProfileProjection::Ordinary {
            reference:
                OrdinaryProfileRef::Ordinary {
                    catalog_id,
                    owner_scope,
                    ..
                },
            ..
        } = profile
            && catalog_id_assertion
                .as_ref()
                .is_none_or(|id| id == catalog_id)
        {
            return Some(Observation::Observed {
                value: ResolvedTarget {
                    installation: installation.binding.clone(),
                    profile: ProfileBinding::Ordinary {
                        ordinary_id: Some(catalog_id.clone()),
                        owner_scope: owner_scope.clone(),
                    },
                },
                evidence: evidence.clone(),
            });
        }
    }
    None
}
impl<S: ObservationSource> LocalHost for ReadOnlyApplication<S> {
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        let request = request.into_inner();
        let result = match request.body {
            RequestBody::Query { query } if !self.closing => {
                self.query(query).map(|query| ResultPayload::Query {
                    query: Box::new(query),
                })
            }
            RequestBody::Command {
                command: Command::RequestHostClose(input),
            } if input.expected_cursor == self.cursor => {
                self.closing = true;
                Ok(ResultPayload::Command {
                    command: Box::new(CommandResult::RequestHostClose(CloseDisposition::Ready)),
                })
            }
            _ => Err(error(ErrorCode::UnsupportedCapability)),
        };
        let reply = Reply {
            protocol_version: ProtocolVersion,
            request_id: ReplyRequestId::new(Some(request.request_id)),
            body: match result {
                Ok(result) => ReplyBody::Result { result },
                Err(error) => ReplyBody::Rejected { error },
            },
        };
        decode_reply(&serde_json::to_vec(&reply).map_err(|_| HostFailure::InvalidReply)?)
            .map_err(|_| HostFailure::InvalidReply)
    }
    fn cursor(&self) -> Cursor {
        self.cursor.clone()
    }
    fn pump(&mut self) -> Result<(), HostFailure> {
        Ok(())
    }
    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        if after.host_epoch != self.cursor.host_epoch
            || after.stream_id != self.cursor.stream_id
            || after.sequence.get() > self.cursor.sequence.get()
            || self
                .events
                .front()
                .is_some_and(|event| after.sequence.get() + 1 < event.cursor.sequence.get())
        {
            return Err(HostFailure::ResnapshotRequired);
        }
        Ok(EventBatch {
            after: after.clone(),
            events: list(
                self.events
                    .iter()
                    .filter(|event| event.cursor.sequence.get() > after.sequence.get())
                    .cloned()
                    .collect(),
            ),
            next: self.cursor.clone(),
        })
    }
    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        self.closing = true;
        Ok(CloseDisposition::Ready)
    }
    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        Ok(CloseDisposition::Ready)
    }
}

#[cfg(test)]
mod tests;
