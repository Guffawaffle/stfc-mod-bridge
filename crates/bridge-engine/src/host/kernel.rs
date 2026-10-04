//! A local adapter of the existing kernel, not another admission policy.
use super::{HostFailure, LocalHost};
use crate::operations::{DurableJournal, Engine, HostClock, IdentitySource, OperationPorts};
use bridge_contracts::v1::*;

pub struct KernelHost<P: OperationPorts, J: DurableJournal, C: HostClock, I: IdentitySource> {
    engine: Engine<P, J, C, I>,
    last_advanced: Option<OperationId>,
}

impl<P: OperationPorts, J: DurableJournal, C: HostClock, I: IdentitySource> KernelHost<P, J, C, I> {
    pub fn new(engine: Engine<P, J, C, I>) -> Self {
        Self {
            engine,
            last_advanced: None,
        }
    }

    /// Only the owning application service may inspect/recover/handoff native
    /// work. No transport channel exposes this local reference.
    pub fn engine_mut(&mut self) -> &mut Engine<P, J, C, I> {
        &mut self.engine
    }

    fn query(&mut self, query: Query) -> Result<ValidatedReply, HostFailure> {
        self.engine
            .dispatch(internal_request(RequestBody::Query { query })?)
            .map_err(HostFailure::Kernel)
    }
}

impl<P: OperationPorts, J: DurableJournal, C: HostClock, I: IdentitySource> LocalHost
    for KernelHost<P, J, C, I>
{
    fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, HostFailure> {
        self.engine.dispatch(request).map_err(HostFailure::Kernel)
    }

    fn cursor(&self) -> Cursor {
        self.engine.cursor()
    }

    fn pump(&mut self) -> Result<(), HostFailure> {
        let reply = self.query(Query::Snapshot(EmptyInput {}))?;
        let ReplyBody::Result {
            result: ResultPayload::Query { query },
        } = &reply.as_inner().body
        else {
            return Err(HostFailure::InvalidObservation);
        };
        let QueryResult::Snapshot(snapshot) = query.as_ref() else {
            return Err(HostFailure::InvalidObservation);
        };
        let ready: Vec<_> = snapshot
            .operations
            .items
            .as_slice()
            .iter()
            .filter(|operation| {
                matches!(
                    operation.state,
                    OperationState::Admitted
                        | OperationState::Running { .. }
                        | OperationState::CancellationRequested { .. }
                )
            })
            .map(|operation| operation.operation_id.clone())
            .collect();
        let next = ready
            .iter()
            .find(|id| self.last_advanced.as_ref().is_none_or(|last| *id > last))
            .or_else(|| ready.first())
            .cloned();
        if let Some(id) = next {
            self.engine.advance(&id).map_err(HostFailure::Kernel)?;
            self.last_advanced = Some(id);
        }
        // Recovery requires the owning application's explicit typed policy;
        // observation and shutdown never automatically recover or hand off.
        Ok(())
    }

    fn events_after(&mut self, after: &Cursor) -> Result<EventBatch, HostFailure> {
        let reply = self.query(Query::ResumeEvents(ResumeEventsInput {
            after: after.clone(),
            maximum_events: ProgressCount::new(128),
        }))?;
        match reply.into_inner().body {
            ReplyBody::Result {
                result: ResultPayload::Query { query },
            } => match *query {
                QueryResult::ResumeEvents(batch) => Ok(batch),
                _ => Err(HostFailure::InvalidObservation),
            },
            ReplyBody::Rejected { error } if error.code == ErrorCode::ResnapshotRequired => {
                Err(HostFailure::ResnapshotRequired)
            }
            _ => Err(HostFailure::InvalidObservation),
        }
    }

    fn request_close(&mut self) -> Result<CloseDisposition, HostFailure> {
        let request = internal_request(RequestBody::Command {
            command: Command::RequestHostClose(RequestHostCloseInput {
                expected_cursor: self.engine.cursor(),
            }),
        })?;
        let reply = self.engine.dispatch(request).map_err(HostFailure::Kernel)?;
        match reply.into_inner().body {
            ReplyBody::Result {
                result: ResultPayload::Command { command },
            } => match *command {
                CommandResult::RequestHostClose(close) => Ok(close),
                _ => Err(HostFailure::CloseUnavailable),
            },
            _ => Err(HostFailure::CloseUnavailable),
        }
    }

    fn close_disposition(&self) -> Result<CloseDisposition, HostFailure> {
        self.engine
            .close_disposition()
            .map_err(|_| HostFailure::CloseUnavailable)
    }
}

pub(super) fn internal_request(body: RequestBody) -> Result<ValidatedRequest, HostFailure> {
    // Internal read/control requests are not renderer identities or authority.
    let request = Request {
        protocol_version: ProtocolVersion,
        request_id: RequestId::new("ffffffff-ffff-4fff-8fff-ffffffffffff")
            .map_err(|_| HostFailure::InvalidObservation)?,
        body,
    };
    let bytes = serde_json::to_vec(&request).map_err(|_| HostFailure::InvalidObservation)?;
    decode_request(&bytes).map_err(|_| HostFailure::InvalidObservation)
}
