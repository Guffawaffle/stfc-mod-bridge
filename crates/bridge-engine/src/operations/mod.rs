//! Portable headless admission and transaction custody. Native owners implement
//! the ports; callers observe operations without owning their writer leases.
mod bindings;
pub mod journal;
mod ports;

use crate::services::{ApplicationResult, ApplicationServices};
use bridge_contracts::v1::*;
pub use journal::{DurableOperation, DurablePhase, FileJournal, JournalRecord};
pub use ports::*;
use std::{
    cell::Cell,
    collections::{BTreeMap, BTreeSet, VecDeque},
};

const MAX_OPERATIONS: usize = 64;
const MAX_PREPARATIONS: usize = 128;
const MAX_ISSUED_PLANS: usize = 4096;
const EVENT_RETENTION: usize = 128;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum KernelFailure {
    Storage,
    UnsafeJournal,
    CorruptJournal,
    JournalBusy,
    Capacity,
    InvalidPortResult,
    Poisoned,
    CounterExhausted,
    ReusedHostIdentity,
}
impl std::fmt::Display for KernelFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("operation kernel unavailable")
    }
}
impl std::error::Error for KernelFailure {}

#[derive(Clone, Debug)]
pub struct HostConfiguration {
    pub epoch: HostEpoch,
    pub stream: StreamId,
    pub kind: HostKind,
    /// No platform evidence is inferred from this configured kind.
    pub preparation_lifetime_millis: u64,
}

struct Preparation {
    plan: PreparedPlan,
    resources: Vec<ResourceKey>,
    deadline: u64,
}
struct Worker<L> {
    lease: L,
}

#[derive(Default)]
struct ClockState {
    last_sample: Cell<Option<u64>>,
    regression: Cell<Option<ProviderFailure>>,
}

impl ClockState {
    fn latch_failure(&self, failure: ProviderFailure) {
        if failure == ProviderFailure::MonotonicRegression {
            self.regression.set(Some(failure));
        }
    }

    fn read<C: HostClock>(&self, clock: &C) -> Result<ClockReading, ProviderFailure> {
        if let Some(failure) = self.regression.get() {
            return Err(failure);
        }
        let sample = clock
            .now()
            .inspect_err(|failure| self.latch_failure(*failure))?;
        if self
            .last_sample
            .get()
            .is_some_and(|previous| sample.monotonic_millis < previous)
        {
            self.regression
                .set(Some(ProviderFailure::MonotonicRegression));
            return Err(ProviderFailure::MonotonicRegression);
        }
        // A future deadline is deliberately never fed into this high-water mark.
        self.last_sample.set(Some(sample.monotonic_millis));
        Ok(sample)
    }
}

/// The host owns this value through safe shutdown. Dropping a request, renderer,
/// channel or client handle has no relationship to worker exclusion ownership.
/// Actual process death drops workers; restart uses retained journal evidence.
pub struct Engine<
    P: OperationPorts + ApplicationServices,
    J: DurableJournal,
    C: HostClock,
    I: IdentitySource,
> {
    ports: P,
    journal: J,
    clock: C,
    clock_state: ClockState,
    ids: I,
    host: HostConfiguration,
    preparations: BTreeMap<PlanId, Preparation>,
    issued_plans: BTreeSet<PlanId>,
    operations: BTreeMap<OperationId, DurableOperation>,
    idempotency: BTreeMap<IdempotencyKey, OperationId>,
    workers: BTreeMap<OperationId, Worker<P::Lease>>,
    events: VecDeque<Event>,
    sequence: u64,
    closing: bool,
    poisoned: bool,
    observation_budget: usize,
    service_commands: Vec<CommandId>,
}

impl<P: OperationPorts + ApplicationServices, J: DurableJournal, C: HostClock, I: IdentitySource>
    Engine<P, J, C, I>
{
    pub fn open(
        ports: P,
        journal: J,
        clock: C,
        ids: I,
        host: HostConfiguration,
    ) -> Result<Self, KernelFailure> {
        Self::open_with_observation_budget(ports, journal, clock, ids, host, MAX_MESSAGE_BYTES)
    }

    /// A host may impose a smaller observation budget than the v1 wire maximum.
    /// This never permits larger protocol messages or omission of pending work.
    pub fn open_with_observation_budget(
        ports: P,
        mut journal: J,
        clock: C,
        ids: I,
        host: HostConfiguration,
        observation_budget: usize,
    ) -> Result<Self, KernelFailure> {
        if !(1024..=MAX_MESSAGE_BYTES).contains(&observation_budget) {
            return Err(KernelFailure::InvalidPortResult);
        }
        if host.preparation_lifetime_millis == 0 {
            return Err(KernelFailure::InvalidPortResult);
        }
        if ports
            .configuration_host_epoch()
            .is_some_and(|epoch| epoch != &host.epoch)
        {
            return Err(KernelFailure::InvalidPortResult);
        }
        let implemented = ports.implemented_commands();
        if implemented.iter().enumerate().any(|(index, command)| {
            !matches!(
                command,
                CommandId::OpenDraft
                    | CommandId::SetDraftChanges
                    | CommandId::DiscardDraft
                    | CommandId::RequestSensitiveInput
            ) || implemented[..index].contains(command)
        }) || (!implemented.is_empty() && ports.configuration_host_epoch().is_none())
        {
            return Err(KernelFailure::InvalidPortResult);
        }
        let mut operations: BTreeMap<OperationId, DurableOperation> = BTreeMap::new();
        let mut idempotency = BTreeMap::new();
        let mut epochs = BTreeSet::new();
        let mut streams = BTreeSet::new();
        for record in journal.records() {
            match record {
                JournalRecord::Host { epoch, stream } => {
                    if epoch == &host.epoch || stream == &host.stream {
                        return Err(KernelFailure::ReusedHostIdentity);
                    }
                    if !epochs.insert(epoch.clone()) || !streams.insert(stream.clone()) {
                        return Err(KernelFailure::CorruptJournal);
                    }
                }
                JournalRecord::Operation { value } => {
                    validate_durable(value).map_err(|_| KernelFailure::CorruptJournal)?;
                    if !epochs.contains(&value.commit.plan_ref.host_epoch) {
                        return Err(KernelFailure::CorruptJournal);
                    }
                    let id = &value.snapshot.operation_id;
                    match operations.get(id) {
                        Some(previous) => validate_transition(previous, value)?,
                        None => {
                            if value.phase != DurablePhase::Admitted
                                || value.snapshot.operation_revision.get() != 1
                                || idempotency.contains_key(&value.commit.idempotency_key)
                                || operations.len() == MAX_OPERATIONS
                            {
                                return Err(KernelFailure::CorruptJournal);
                            }
                            idempotency.insert(value.commit.idempotency_key.clone(), id.clone());
                        }
                    }
                    operations.insert(id.clone(), *value.clone());
                }
            }
        }
        journal.append(&JournalRecord::Host {
            epoch: host.epoch.clone(),
            stream: host.stream.clone(),
        })?;
        let mut engine = Self {
            ports,
            journal,
            clock,
            clock_state: ClockState::default(),
            ids,
            host,
            preparations: BTreeMap::new(),
            issued_plans: BTreeSet::new(),
            operations,
            idempotency,
            workers: BTreeMap::new(),
            events: VecDeque::new(),
            sequence: 0,
            closing: false,
            poisoned: false,
            observation_budget,
            service_commands: implemented,
        };
        let interrupted = engine
            .operations
            .values()
            .filter(|o| o.phase != DurablePhase::Terminal || o.session_custody.is_some())
            .cloned()
            .collect::<Vec<_>>();
        for mut operation in interrupted {
            if operation.phase == DurablePhase::Admitted {
                operation.snapshot.state = OperationState::Completed {
                    outcome: CompletionOutcome::CancelledBeforeCommit {
                        reason: CompletionReason::CancellationAccepted,
                    },
                };
                operation.phase = DurablePhase::Terminal;
                operation.safe_owner_boundary = true;
            } else if operation.phase != DurablePhase::Terminal {
                operation.snapshot.state = OperationState::RecoveryRequired {
                    recovery: Box::new(operation.recovery.clone()),
                    reason: RecoveryReason::InterruptedTransaction,
                };
                operation.phase = DurablePhase::Recovery;
                operation.safe_owner_boundary = false;
            } else {
                // A historical session handoff is not inferred. Retain its close
                // obligation until the canonical owner verifies actual custody.
                continue;
            }
            engine.persist_change(operation)?;
        }
        Ok(engine)
    }

    pub fn cursor(&self) -> Cursor {
        Cursor {
            host_epoch: self.host.epoch.clone(),
            stream_id: self.host.stream.clone(),
            sequence: Sequence::new(self.sequence),
        }
    }

    pub fn operation(&self, id: &OperationId) -> Option<&OperationSnapshot> {
        self.operations.get(id).map(|o| &o.snapshot)
    }

    pub fn dispatch(&mut self, request: ValidatedRequest) -> Result<ValidatedReply, KernelFailure> {
        let request = request.into_inner();
        let result = if let Some(result) = self.application_request(&request)? {
            result
        } else {
            match request.body {
                RequestBody::Command { command } => {
                    self.command(command).map(|command| ResultPayload::Command {
                        command: Box::new(command),
                    })
                }
                RequestBody::Query { query } => {
                    self.query(query).map(|query| ResultPayload::Query {
                        query: Box::new(query),
                    })
                }
            }
        };
        let body = match result {
            Ok(result) => ReplyBody::Result { result },
            Err(error) => ReplyBody::Rejected { error },
        };
        let reply = Reply {
            protocol_version: ProtocolVersion,
            request_id: ReplyRequestId::new(Some(request.request_id)),
            body,
        };
        validated_reply(reply)
    }

    fn application_request(
        &mut self,
        request: &Request,
    ) -> Result<Option<ApplicationResult<ResultPayload>>, KernelFailure> {
        let is_application = matches!(
            &request.body,
            RequestBody::Command {
                command: Command::OpenDraft(_)
                    | Command::SetDraftChanges(_)
                    | Command::DiscardDraft(_)
                    | Command::RequestSensitiveInput(_)
            } | RequestBody::Query {
                query: Query::ReadConfiguration(_)
                    | Query::ConfigurationHistory(_)
                    | Query::GetDraft(_)
            }
        );
        if !is_application {
            return Ok(None);
        }
        if let RequestBody::Command { command } = &request.body {
            let id = match command {
                Command::OpenDraft(_) => CommandId::OpenDraft,
                Command::SetDraftChanges(_) => CommandId::SetDraftChanges,
                Command::DiscardDraft(_) => CommandId::DiscardDraft,
                Command::RequestSensitiveInput(_) => CommandId::RequestSensitiveInput,
                _ => unreachable!("application command classification"),
            };
            if !self.service_commands.contains(&id) {
                return Ok(Some(Err(error(ErrorCode::UnsupportedCapability))));
            }
        }
        if let RequestBody::Query {
            query: Query::GetDraft(input),
        } = &request.body
        {
            if input.host_epoch != self.host.epoch {
                return Ok(Some(Err(
                    crate::configuration::ConfigurationFailure::HostMismatch.bridge_error(),
                )));
            }
        } else if let Err(error) = self.require_open() {
            return Ok(Some(Err(error)));
        }
        let evidence = if matches!(&request.body, RequestBody::Query { .. }) {
            match self.evidence() {
                Ok(evidence) => Some(evidence),
                Err(error) => return Ok(Some(Err(error))),
            }
        } else {
            None
        };
        let cursor = self.cursor();
        let budget = self.observation_budget;
        let mut preflight_failure = None;
        let mut prepared_payload = None;
        let mut prepared_events = vec![];
        let mut preflight = |payload: ResultPayload, changed: &[DraftSnapshot]| {
            if prepared_payload.is_some() {
                preflight_failure = Some(KernelFailure::InvalidPortResult);
                return Err(error(ErrorCode::InternalFailure));
            }
            match preflight_application(&request.request_id, &cursor, budget, &payload, changed) {
                Ok(events) => {
                    prepared_payload = Some(payload);
                    prepared_events = events;
                    Ok(())
                }
                Err(failure) => {
                    preflight_failure = Some(failure);
                    Err(error(ErrorCode::InternalFailure))
                }
            }
        };
        let result = match &request.body {
            RequestBody::Command { command } => match command {
                Command::OpenDraft(input) => self
                    .ports
                    .open_draft(input, &mut |snapshot| {
                        if snapshot.draft.document != input.document
                            || snapshot.draft.host_epoch != cursor.host_epoch
                        {
                            return Err(error(ErrorCode::InvalidRequest));
                        }
                        preflight(
                            command_payload(CommandResult::OpenDraft(snapshot.clone())),
                            std::slice::from_ref(snapshot),
                        )
                    })
                    .map(|snapshot| command_payload(CommandResult::OpenDraft(snapshot))),
                Command::SetDraftChanges(input) => self
                    .ports
                    .set_draft_changes(input.clone(), &mut |receipt, changed| {
                        if receipt.accepted != *input
                            || receipt.snapshot.draft.host_epoch != cursor.host_epoch
                        {
                            return Err(error(ErrorCode::InvalidRequest));
                        }
                        preflight(
                            command_payload(CommandResult::SetDraftChanges(Box::new(
                                receipt.clone(),
                            ))),
                            if changed {
                                std::slice::from_ref(&receipt.snapshot)
                            } else {
                                &[]
                            },
                        )
                    })
                    .map(|receipt| {
                        command_payload(CommandResult::SetDraftChanges(Box::new(receipt)))
                    }),
                Command::DiscardDraft(input) => self
                    .ports
                    .discard_draft(input, &mut |receipt| {
                        if receipt.draft_id != input.draft.draft_id
                            || receipt.host_epoch != cursor.host_epoch
                            || receipt.previous_revision != input.draft.revision
                        {
                            return Err(error(ErrorCode::InvalidRequest));
                        }
                        preflight(
                            command_payload(CommandResult::DiscardDraft(receipt.clone())),
                            &[],
                        )
                    })
                    .map(|receipt| command_payload(CommandResult::DiscardDraft(receipt))),
                Command::RequestSensitiveInput(input) => self
                    .ports
                    .request_sensitive_input(input, &mut |receipt| {
                        if receipt.binding != *input {
                            return Err(error(ErrorCode::InvalidRequest));
                        }
                        preflight(
                            command_payload(CommandResult::RequestSensitiveInput(receipt.clone())),
                            &[],
                        )
                    })
                    .map(|receipt| command_payload(CommandResult::RequestSensitiveInput(receipt))),
                _ => unreachable!("application command classification"),
            },
            RequestBody::Query { query } => match query {
                Query::ReadConfiguration(input) => self
                    .ports
                    .read_configuration(input, &mut |snapshot, changed| {
                        preflight(
                            query_payload(QueryResult::ReadConfiguration(Observation::Observed {
                                value: snapshot.clone(),
                                evidence: evidence.clone().expect("query evidence"),
                            })),
                            changed,
                        )
                    })
                    .map(|snapshot| {
                        query_payload(QueryResult::ReadConfiguration(Observation::Observed {
                            value: snapshot,
                            evidence: evidence.clone().expect("query evidence"),
                        }))
                    }),
                Query::ConfigurationHistory(input) => self
                    .ports
                    .configuration_history(input)
                    .and_then(|inventory| {
                        let payload = query_payload(QueryResult::ConfigurationHistory(
                            Observation::Observed {
                                value: inventory,
                                evidence: evidence.clone().expect("query evidence"),
                            },
                        ));
                        preflight(payload.clone(), &[])?;
                        Ok(payload)
                    }),
                Query::GetDraft(input) => self.ports.get_draft(input).and_then(|draft| {
                    if draft.as_ref().is_some_and(|snapshot| {
                        snapshot.draft.host_epoch != cursor.host_epoch
                            || snapshot.draft.draft_id != input.draft_id
                    }) {
                        return Err(error(ErrorCode::InvalidRequest));
                    }
                    let evidence = evidence.clone().expect("query evidence");
                    let payload = query_payload(QueryResult::GetDraft(GetDraftResult {
                        cursor: cursor.clone(),
                        draft: match draft {
                            Some(value) => Observation::Observed { value, evidence },
                            None => Observation::Missing { evidence },
                        },
                    }));
                    preflight(payload.clone(), &[])?;
                    Ok(payload)
                }),
                _ => unreachable!("application query classification"),
            },
        };
        if let Some(failure) = preflight_failure {
            return Err(failure);
        }
        if let Ok(payload) = &result {
            if prepared_payload.as_ref() != Some(payload) {
                return Err(KernelFailure::InvalidPortResult);
            }
            // No fallible work follows publication by the workspace. All
            // envelopes and every consecutive cursor were validated above.
            for event in prepared_events {
                self.publish_event(event);
            }
        }
        Ok(Some(result))
    }

    fn command(&mut self, command: Command) -> Result<CommandResult, Box<BridgeError>> {
        match command {
            Command::Commit(input) => self.commit(input).map(CommandResult::Commit),
            Command::Prepare(input) => self.prepare(input.intent).map(CommandResult::Prepare),
            Command::CancelOperation(input) => {
                self.cancel(input).map(CommandResult::CancelOperation)
            }
            Command::RequestHostClose(input) => {
                if input.expected_cursor != self.cursor() {
                    return Err(error(ErrorCode::ResnapshotRequired));
                }
                self.closing = true;
                self.close_disposition()
                    .map(CommandResult::RequestHostClose)
            }
            _ => Err(error(ErrorCode::UnsupportedCapability)),
        }
    }

    fn query(&self, query: Query) -> Result<QueryResult, Box<BridgeError>> {
        match query {
            Query::Hello(_) => Ok(QueryResult::Hello(HelloResult {
                supported_versions: [ProtocolVersion],
                host_epoch: self.host.epoch.clone(),
                host_kind: self.host.kind,
                implemented_commands: list({
                    let mut commands = vec![
                        CommandId::Prepare,
                        CommandId::Commit,
                        CommandId::CancelOperation,
                        CommandId::RequestHostClose,
                    ];
                    commands.extend(self.service_commands.iter().copied());
                    commands
                }),
            })),
            Query::GetOperation(input) => {
                let evidence = self.evidence()?;
                Ok(QueryResult::GetOperation(Box::new(GetOperationResult {
                    operation: match self.operations.get(&input.operation_id) {
                        Some(o) => Observation::Observed {
                            value: o.snapshot.clone(),
                            evidence,
                        },
                        None => Observation::Missing { evidence },
                    },
                })))
            }
            Query::Snapshot(_) => Ok(QueryResult::Snapshot(self.snapshot_with(None))),
            Query::ResumeEvents(input) => self.resume_events(input).map(QueryResult::ResumeEvents),
            _ => Err(error(ErrorCode::UnsupportedCapability)),
        }
    }

    fn read_clock(&self) -> Result<ClockReading, Box<BridgeError>> {
        self.clock_state
            .read(&self.clock)
            .map_err(|_| error(ErrorCode::InternalFailure))
    }
    fn evidence(&self) -> Result<Evidence, Box<BridgeError>> {
        Ok(Evidence {
            observation_id: ObservationId::new(self.host.epoch.as_str())
                .expect("host UUID is observation UUID"),
            observed_at: self.read_clock()?.utc,
            source: EvidenceSource::SessionReceipt,
        })
    }
    fn revision(&self) -> OpaqueRevision {
        OpaqueRevision::new(format!("operations:{}", self.sequence)).expect("bounded revision")
    }
    fn operation_inventory(&self) -> Inventory<OperationSnapshot> {
        Inventory {
            items: list(
                self.operations
                    .values()
                    .map(|o| o.snapshot.clone())
                    .collect(),
            ),
            completeness: Completeness::Complete,
            issues: list(vec![]),
            revision: self.revision(),
        }
    }
    fn snapshot_with(&self, replacement: Option<&DurableOperation>) -> Snapshot {
        let mut operations = self.operation_inventory();
        if let Some(replacement) = replacement {
            let mut items = operations.items.as_slice().to_vec();
            if let Some(existing) = items
                .iter_mut()
                .find(|o| o.operation_id == replacement.snapshot.operation_id)
            {
                *existing = replacement.snapshot.clone();
            } else {
                items.push(replacement.snapshot.clone());
            }
            operations.items = list(items);
        }
        Snapshot {
            cursor: self.cursor(),
            preferences: unavailable(),
            profiles: unavailable(),
            installations: unavailable(),
            sessions: unavailable(),
            operations,
            capabilities: Inventory {
                items: list(vec![]),
                completeness: Completeness::Partial,
                issues: list(vec![ProjectionIssue {
                    code: ProjectionIssueCode::NativeUnavailable,
                    resource: None,
                }]),
                revision: self.revision(),
            },
        }
    }
    fn observation_capacity(
        &self,
        candidate: &DurableOperation,
        reserve_recovery: bool,
    ) -> Result<(), KernelFailure> {
        let mut snapshot = self.snapshot_with(Some(candidate));
        if reserve_recovery {
            let mut items = snapshot.operations.items.as_slice().to_vec();
            for item in &mut items {
                let operation = if item.operation_id == candidate.snapshot.operation_id {
                    candidate
                } else {
                    &self.operations[&item.operation_id]
                };
                if operation.phase != DurablePhase::Terminal {
                    item.state = OperationState::RecoveryRequired {
                        recovery: Box::new(operation.recovery.clone()),
                        reason: RecoveryReason::NativeCustodyUnresolved,
                    };
                }
            }
            snapshot.operations.items = list(items);
        }
        let reply = query_reply(QueryResult::Snapshot(snapshot));
        if serde_json::to_vec(&reply)
            .map_err(|_| KernelFailure::InvalidPortResult)?
            .len()
            > self.observation_budget
        {
            return Err(KernelFailure::Capacity);
        }
        validated_reply(reply)
            .map(|_| ())
            .map_err(|_| KernelFailure::Capacity)
    }

    fn prepare(&mut self, intent: MutationIntent) -> Result<PreparedPlan, Box<BridgeError>> {
        self.require_open()?;
        if self.issued_plans.len() >= MAX_ISSUED_PLANS {
            return Err(error(ErrorCode::OperationBusy));
        }
        let pruning_sample = self.read_clock()?;
        self.preparations
            .retain(|_, p| p.deadline > pruning_sample.monotonic_millis);
        if self.preparations.len() >= MAX_PREPARATIONS {
            return Err(error(ErrorCode::OperationBusy));
        }
        let captured = self.ports.capture(&intent, &self.host.epoch)?;
        bindings::validate_capture(&intent, &captured)
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        let resources = normalize_resources(captured.resources)
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        if !bindings::required_resources(&captured.semantics.capture)
            .iter()
            .all(|r| resources.contains(r))
        {
            return Err(error(ErrorCode::InternalFailure));
        }
        let now = self.read_clock()?;
        let expected_deadline = now
            .monotonic_millis
            .checked_add(self.host.preparation_lifetime_millis)
            .ok_or_else(|| error(ErrorCode::InternalFailure))?;
        let deadline = self
            .clock
            .deadline(&now, self.host.preparation_lifetime_millis)
            .inspect_err(|failure| self.clock_state.latch_failure(*failure))
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        if deadline.monotonic_millis != expected_deadline {
            return Err(error(ErrorCode::InternalFailure));
        }
        let plan = PreparedPlan {
            plan_ref: PlanRef {
                plan_id: self
                    .ids
                    .plan_id()
                    .map_err(|_| error(ErrorCode::InternalFailure))?,
                host_epoch: self.host.epoch.clone(),
                review_digest: semantic_plan_digest(&captured.semantics)
                    .map_err(|_| error(ErrorCode::InternalFailure))?,
            },
            semantics: captured.semantics,
            expires_at: deadline.utc,
            grants_lock: FalseFlag,
            grants_permission: FalseFlag,
        };
        validate_command_output(CommandResult::Prepare(plan.clone()))
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        // Pruning expired captures must never make an old PlanRef fresh again.
        // Keep bounded identity custody until this host's lifetime ends.
        if !self.issued_plans.insert(plan.plan_ref.plan_id.clone()) {
            return Err(error(ErrorCode::InternalFailure));
        }
        self.preparations.insert(
            plan.plan_ref.plan_id.clone(),
            Preparation {
                plan: plan.clone(),
                resources,
                deadline: deadline.monotonic_millis,
            },
        );
        Ok(plan)
    }

    fn commit(&mut self, input: CommitInput) -> Result<OperationSnapshot, Box<BridgeError>> {
        // Durable admitted replay precedes host/expiry/preparation lookup and
        // close checks. It never acquires another lease or advances a transaction.
        if let Some(id) = self.idempotency.get(&input.idempotency_key) {
            let previous = &self.operations[id];
            if previous.commit != input {
                return Err(error(ErrorCode::IdempotencyConflict));
            }
            if self.poisoned {
                return Err(error(ErrorCode::PersistenceFailed));
            }
            return Ok(previous.snapshot.clone());
        }
        self.require_open()?;
        if input.plan_ref.host_epoch != self.host.epoch {
            return Err(error(ErrorCode::PlanHostMismatch));
        }
        let prepared = self
            .preparations
            .get(&input.plan_ref.plan_id)
            .ok_or_else(|| error(ErrorCode::PlanExpired))?;
        if prepared.plan.plan_ref != input.plan_ref {
            return Err(error(ErrorCode::InvalidRequest));
        }
        if prepared.deadline <= self.read_clock()?.monotonic_millis {
            return Err(error(ErrorCode::PlanExpired));
        }
        if self.operations.len() >= MAX_OPERATIONS {
            return Err(error(ErrorCode::OperationBusy));
        }
        let semantics = prepared.plan.semantics.clone();
        let resources = prepared.resources.clone();
        let deadline = prepared.deadline;
        for operation in self.operations.values() {
            if operation.phase == DurablePhase::Recovery
                && overlaps(&resources, &operation.resources)
            {
                return Err(recovery_error(operation.recovery.clone()));
            }
            if (operation.phase != DurablePhase::Terminal || operation.session_custody.is_some())
                && overlaps(&resources, &operation.resources)
            {
                return Err(error(ErrorCode::OperationBusy));
            }
        }
        let id = self
            .ids
            .operation_id()
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        if self.operations.contains_key(&id) {
            return Err(error(ErrorCode::InternalFailure));
        }
        let lease = self.ports.acquire(&resources)?;
        if normalize_resources(lease.resources().to_vec()).is_err()
            || lease.resources() != resources.as_slice()
        {
            return Err(error(ErrorCode::InternalFailure));
        }
        self.ports.revalidate(&semantics, &lease)?;
        if deadline <= self.read_clock()?.monotonic_millis {
            return Err(error(ErrorCode::PlanExpired));
        }
        let recovery = self.ports.recovery_binding(&id, &semantics, &lease)?;
        let snapshot = OperationSnapshot {
            operation_id: id.clone(),
            operation_revision: RevisionCounter::new(1),
            semantics,
            state: OperationState::Admitted,
        };
        // Validate owner binding against this exact operation/capture without
        // fabricating any native recovery target or transaction identity.
        let mut probe = snapshot.clone();
        probe.state = OperationState::RecoveryRequired {
            recovery: Box::new(recovery.clone()),
            reason: RecoveryReason::InterruptedTransaction,
        };
        validate_command_output(CommandResult::Commit(probe))
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        let operation = DurableOperation {
            commit: input.clone(),
            snapshot: snapshot.clone(),
            resources,
            recovery,
            phase: DurablePhase::Admitted,
            cancellation_requested: false,
            cancellable: true,
            safe_owner_boundary: false,
            session_custody: None,
        };
        self.observation_capacity(&operation, true)
            .map_err(|_| error(ErrorCode::OperationBusy))?;
        self.workers.insert(id.clone(), Worker { lease });
        if self
            .journal
            .append(&JournalRecord::Operation {
                value: Box::new(operation.clone()),
            })
            .is_err()
        {
            self.poisoned = true;
            // Retain uncertain admission and exclusion; never start native work.
            self.operations.insert(id.clone(), operation);
            self.idempotency.insert(input.idempotency_key, id);
            return Err(error(ErrorCode::PersistenceFailed));
        }
        self.idempotency.insert(input.idempotency_key, id.clone());
        self.operations.insert(id.clone(), operation);
        self.emit_operation(&id)
            .map_err(|_| error(ErrorCode::InternalFailure))?;
        Ok(snapshot)
    }

    /// Drive one admitted worker step from the headless host scheduler. The
    /// transport only submits/observes; closing it does not call this or drop leases.
    pub fn advance(&mut self, id: &OperationId) -> Result<OperationSnapshot, KernelFailure> {
        if self.poisoned {
            return Err(KernelFailure::Poisoned);
        }
        let mut operation = self
            .operations
            .get(id)
            .cloned()
            .ok_or(KernelFailure::InvalidPortResult)?;
        if operation.phase == DurablePhase::Terminal {
            return Ok(operation.snapshot);
        }
        if operation.phase == DurablePhase::Recovery {
            return Err(KernelFailure::InvalidPortResult);
        }
        if operation.phase == DurablePhase::Admitted {
            operation.phase = DurablePhase::Executing;
            operation.cancellable = false;
            operation.snapshot.state = OperationState::Running {
                progress: unknown_progress("starting"),
            };
            self.persist_change(operation.clone())?;
            operation = self.operations[id].clone();
        }
        let worker = self
            .workers
            .get(id)
            .ok_or(KernelFailure::InvalidPortResult)?;
        let step = self.ports.advance(
            &operation.snapshot,
            &operation.recovery,
            &worker.lease,
            operation.cancellation_requested,
        );
        self.finish_step(id, step)
    }

    /// No worker is assumed to have survived process death. Acquire exact owner
    /// custody anew, then ask that owner to inspect its retained transaction.
    pub fn recover(&mut self, id: &OperationId) -> Result<OperationSnapshot, KernelFailure> {
        if self.poisoned {
            return Err(KernelFailure::Poisoned);
        }
        let operation = self
            .operations
            .get(id)
            .cloned()
            .ok_or(KernelFailure::InvalidPortResult)?;
        if operation.phase != DurablePhase::Recovery {
            return Err(KernelFailure::InvalidPortResult);
        }
        if !self.workers.contains_key(id) {
            let lease = self
                .ports
                .acquire(&operation.resources)
                .map_err(|_| KernelFailure::InvalidPortResult)?;
            if lease.resources() != operation.resources.as_slice() {
                return Err(KernelFailure::InvalidPortResult);
            }
            self.workers.insert(id.clone(), Worker { lease });
        }
        let worker = &self.workers[id];
        let step = self
            .ports
            .recover(&operation.snapshot, &operation.recovery, &worker.lease);
        self.finish_step(id, step)
    }

    fn finish_step(
        &mut self,
        id: &OperationId,
        step: Result<TransactionStep, Box<BridgeError>>,
    ) -> Result<OperationSnapshot, KernelFailure> {
        let mut operation = self.operations[id].clone();
        match step {
            Ok(TransactionStep::Progress {
                progress,
                cancellable,
            }) => {
                if operation.phase == DurablePhase::Recovery {
                    return Err(KernelFailure::InvalidPortResult);
                }
                operation.cancellable = cancellable;
                operation.snapshot.state = if operation.cancellation_requested {
                    OperationState::CancellationRequested { progress }
                } else {
                    OperationState::Running { progress }
                };
            }
            Ok(TransactionStep::Complete {
                outcome,
                session_custody,
            }) => {
                if session_custody.is_some()
                    && !matches!(
                        operation.snapshot.semantics.capture,
                        PreparedCapture::LaunchOrdinary { .. }
                            | PreparedCapture::LaunchIsolated { .. }
                    )
                {
                    return Err(KernelFailure::InvalidPortResult);
                }
                if let Some(session) = &session_custody {
                    if matches!(&outcome, CompletionOutcome::Changed { receipt: Some(receipt), .. }
                        if matches!(receipt.as_ref(), EffectReceipt::SessionSpawned { session: projection }
                            if &projection.binding != session))
                    {
                        return Err(KernelFailure::InvalidPortResult);
                    }
                    let target = match &operation.snapshot.semantics.capture {
                        PreparedCapture::LaunchOrdinary { target, .. }
                        | PreparedCapture::LaunchIsolated { target, .. } => target,
                        _ => unreachable!(),
                    };
                    if &session.process.installation_physical_id
                        != target.installation.physical_id()
                        || !matches!(outcome, CompletionOutcome::Changed { .. })
                    {
                        return Err(KernelFailure::InvalidPortResult);
                    }
                }
                operation.phase = DurablePhase::Terminal;
                operation.safe_owner_boundary = true;
                operation.session_custody = session_custody;
                operation.snapshot.state = OperationState::Completed { outcome };
            }
            Ok(TransactionStep::RecoveryRequired {
                reason,
                safe_owner_boundary,
            }) => {
                operation.phase = DurablePhase::Recovery;
                operation.safe_owner_boundary = safe_owner_boundary;
                operation.snapshot.state = OperationState::RecoveryRequired {
                    recovery: Box::new(operation.recovery.clone()),
                    reason,
                };
            }
            Err(_) => {
                // An arbitrary native error cannot establish rollback or failure
                // without effects. Retain exact unresolved owned recovery.
                operation.phase = DurablePhase::Recovery;
                operation.safe_owner_boundary = false;
                operation.snapshot.state = OperationState::RecoveryRequired {
                    recovery: Box::new(operation.recovery.clone()),
                    reason: RecoveryReason::NativeCustodyUnresolved,
                };
            }
        }
        let complete_at_owner = operation.phase == DurablePhase::Terminal;
        let session_custody = operation.session_custody.clone();
        match self.persist_change(operation) {
            Ok(()) => {}
            Err(KernelFailure::Capacity) if complete_at_owner && !self.poisoned => {
                // The native owner has already reached a durable complete
                // boundary. A publication budget failure cannot turn that into
                // false failure or indefinitely Running. Admission reserved this
                // exact recovery projection before any native effects.
                let mut recovery = self.operations[id].clone();
                recovery.phase = DurablePhase::Recovery;
                recovery.safe_owner_boundary = true;
                recovery.session_custody = session_custody;
                recovery.snapshot.state = OperationState::RecoveryRequired {
                    recovery: Box::new(recovery.recovery.clone()),
                    reason: RecoveryReason::NativeCustodyUnresolved,
                };
                self.persist_change(recovery)?;
            }
            Err(failure) => return Err(failure),
        }
        let operation = &self.operations[id];
        if operation.phase == DurablePhase::Terminal && operation.session_custody.is_none()
            || operation.phase == DurablePhase::Recovery
                && operation.safe_owner_boundary
                && operation.session_custody.is_none()
        {
            self.workers.remove(id);
        }
        Ok(self.operations[id].snapshot.clone())
    }

    fn cancel(
        &mut self,
        input: CancelOperationInput,
    ) -> Result<CancelDisposition, Box<BridgeError>> {
        if self.poisoned {
            return Err(error(ErrorCode::PersistenceFailed));
        }
        let mut operation = self
            .operations
            .get(&input.operation_id)
            .cloned()
            .ok_or_else(|| error(ErrorCode::TargetMissing))?;
        if operation.snapshot.operation_revision != input.expected_operation_revision {
            return Err(error(ErrorCode::StaleRevision));
        }
        let id = operation.snapshot.operation_id.clone();
        match operation.phase {
            DurablePhase::Admitted => {
                operation.phase = DurablePhase::Terminal;
                operation.snapshot.state = OperationState::Completed {
                    outcome: CompletionOutcome::CancelledBeforeCommit {
                        reason: CompletionReason::CancellationAccepted,
                    },
                };
                operation.safe_owner_boundary = true;
                self.persist_change(operation)
                    .map_err(|_| error(ErrorCode::PersistenceFailed))?;
                self.workers.remove(&id);
                Ok(CancelDisposition::CancelledBeforeCommit {
                    operation: self.operations[&id].snapshot.clone(),
                })
            }
            DurablePhase::Executing if operation.cancellation_requested => {
                // This is observation of the already accepted cancellation
                // request, even if the native transaction has since crossed
                // its cancellable boundary. Do not retract that request or
                // manufacture a Running snapshot for a TooLate reply.
                Ok(CancelDisposition::Requested {
                    operation: operation.snapshot,
                })
            }
            DurablePhase::Executing if operation.cancellable => {
                operation.cancellation_requested = true;
                let progress = match &operation.snapshot.state {
                    OperationState::Running { progress }
                    | OperationState::CancellationRequested { progress } => progress.clone(),
                    _ => return Err(error(ErrorCode::InternalFailure)),
                };
                operation.snapshot.state = OperationState::CancellationRequested { progress };
                self.persist_change(operation)
                    .map_err(|_| error(ErrorCode::PersistenceFailed))?;
                Ok(CancelDisposition::Requested {
                    operation: self.operations[&id].snapshot.clone(),
                })
            }
            DurablePhase::Executing => Ok(CancelDisposition::TooLate {
                operation: operation.snapshot,
            }),
            DurablePhase::Terminal => Ok(CancelDisposition::AlreadyTerminal {
                operation: operation.snapshot,
            }),
            DurablePhase::Recovery => Ok(CancelDisposition::RecoveryRequired {
                operation: operation.snapshot,
            }),
        }
    }

    /// Host closure never stops a game. It retains operation/session custody or
    /// reports a verified durable native recovery boundary before ready-to-exit.
    pub fn close_disposition(&self) -> Result<CloseDisposition, Box<BridgeError>> {
        let mut obligations = Vec::new();
        let mut recoveries = Vec::new();
        let mut exit_deferred = false;
        for operation in self.operations.values() {
            if let Some(session) = &operation.session_custody {
                exit_deferred = true;
                obligations.push(CloseObligation::SessionCustody {
                    session: session.clone(),
                });
            }
            if operation.phase != DurablePhase::Terminal {
                // Deferred is a complete projection of every pending operation,
                // including recovery already at a durable owner boundary. A
                // separate session/worker blocker must not hide those IDs.
                obligations.push(CloseObligation::Operation {
                    operation_id: operation.snapshot.operation_id.clone(),
                    operation_revision: operation.snapshot.operation_revision,
                });
            }
            match operation.phase {
                DurablePhase::Terminal => {}
                DurablePhase::Recovery if operation.safe_owner_boundary && !self.poisoned => {
                    recoveries.push(operation.recovery.clone())
                }
                _ => exit_deferred = true,
            }
        }
        if exit_deferred {
            Ok(CloseDisposition::Deferred {
                obligations: BoundedList::new(obligations)
                    .map_err(|_| error(ErrorCode::OperationBusy))?,
            })
        } else if !recoveries.is_empty() {
            Ok(CloseDisposition::RecoveryRequired {
                recoveries: list(recoveries),
            })
        } else if self.poisoned {
            Err(error(ErrorCode::PersistenceFailed))
        } else {
            Ok(CloseDisposition::Ready)
        }
    }

    pub fn handoff_session(&mut self, id: &OperationId) -> Result<bool, KernelFailure> {
        if self.poisoned {
            return Err(KernelFailure::Poisoned);
        }
        let mut operation = self
            .operations
            .get(id)
            .cloned()
            .ok_or(KernelFailure::InvalidPortResult)?;
        let session = operation
            .session_custody
            .clone()
            .ok_or(KernelFailure::InvalidPortResult)?;
        if !self.workers.contains_key(id) {
            let lease = self
                .ports
                .acquire(&operation.resources)
                .map_err(|_| KernelFailure::InvalidPortResult)?;
            if lease.resources() != operation.resources.as_slice() {
                return Err(KernelFailure::InvalidPortResult);
            }
            self.workers.insert(id.clone(), Worker { lease });
        }
        if !self
            .ports
            .handoff_session(&session, &self.workers[id].lease)
            .map_err(|_| KernelFailure::InvalidPortResult)?
        {
            return Ok(false);
        }
        operation.session_custody = None;
        self.persist_change(operation)?;
        let operation = &self.operations[id];
        if operation.phase == DurablePhase::Terminal
            || operation.phase == DurablePhase::Recovery && operation.safe_owner_boundary
        {
            // Session handoff settles the lifetime obligation, not an unsafe
            // transaction recovery obligation retained after host restart.
            self.workers.remove(id);
        }
        Ok(true)
    }

    fn resume_events(&self, input: ResumeEventsInput) -> Result<EventBatch, Box<BridgeError>> {
        let after = input.after;
        if after.host_epoch != self.host.epoch
            || after.stream_id != self.host.stream
            || after.sequence.get() > self.sequence
        {
            return Err(error(ErrorCode::ResnapshotRequired));
        }
        if self.events.front().is_some_and(|e| {
            after
                .sequence
                .get()
                .checked_add(1)
                .is_none_or(|next| next < e.cursor.sequence.get())
        }) {
            return Err(error(ErrorCode::ResnapshotRequired));
        }
        let mut events = Vec::new();
        for event in self
            .events
            .iter()
            .filter(|e| e.cursor.sequence > after.sequence)
            .take(input.maximum_events.get() as usize)
        {
            let mut candidate = events.clone();
            candidate.push(event.clone());
            let batch = EventBatch {
                after: after.clone(),
                next: event.cursor.clone(),
                events: list(candidate.clone()),
            };
            let payload = serde_json::to_vec(&Reply {
                protocol_version: ProtocolVersion,
                request_id: ReplyRequestId::new(Some(
                    RequestId::new("00000000-0000-4000-8000-000000000001")
                        .expect("static request UUID"),
                )),
                body: ReplyBody::Result {
                    result: ResultPayload::Query {
                        query: Box::new(QueryResult::ResumeEvents(batch)),
                    },
                },
            })
            .map_err(|_| error(ErrorCode::InternalFailure))?;
            if payload.len() > MAX_MESSAGE_BYTES {
                break;
            }
            events = candidate;
        }
        let next = events
            .last()
            .map(|e| e.cursor.clone())
            .unwrap_or_else(|| after.clone());
        Ok(EventBatch {
            after,
            events: list(events),
            next,
        })
    }

    fn persist_change(&mut self, mut operation: DurableOperation) -> Result<(), KernelFailure> {
        operation.snapshot.operation_revision = RevisionCounter::new(
            operation
                .snapshot
                .operation_revision
                .get()
                .checked_add(1)
                .ok_or(KernelFailure::CounterExhausted)?,
        );
        validate_durable(&operation)?;
        self.observation_capacity(&operation, false)?;
        self.observation_capacity(&operation, true)?;
        validate_transition(
            &self.operations[&operation.snapshot.operation_id],
            &operation,
        )?;
        if let Err(failure) = self.journal.append(&JournalRecord::Operation {
            value: Box::new(operation.clone()),
        }) {
            self.poisoned = true;
            return Err(failure);
        }
        let id = operation.snapshot.operation_id.clone();
        self.operations.insert(id.clone(), operation);
        self.emit_operation(&id)
    }

    fn emit_operation(&mut self, id: &OperationId) -> Result<(), KernelFailure> {
        let mut cursor = self.cursor();
        cursor.sequence = Sequence::new(
            self.sequence
                .checked_add(1)
                .ok_or(KernelFailure::CounterExhausted)?,
        );
        let event = Event {
            protocol_version: ProtocolVersion,
            cursor,
            body: EventBody::OperationChanged {
                operation: Box::new(self.operations[id].snapshot.clone()),
            },
        };
        let bytes = serde_json::to_vec(&event).map_err(|_| KernelFailure::InvalidPortResult)?;
        decode_event(&bytes).map_err(|_| KernelFailure::InvalidPortResult)?;
        self.publish_event(event);
        Ok(())
    }
    /// Only prevalidated events reach the shared emitter. Application services
    /// supply projections, never sequence numbers or a second event stream.
    fn publish_event(&mut self, event: Event) {
        self.sequence = event.cursor.sequence.get();
        self.events.push_back(event);
        if self.events.len() > EVENT_RETENTION {
            self.events.pop_front();
        }
    }
    fn require_open(&self) -> Result<(), Box<BridgeError>> {
        if self.poisoned {
            Err(error(ErrorCode::PersistenceFailed))
        } else if self.closing {
            Err(error(ErrorCode::OperationBusy))
        } else {
            Ok(())
        }
    }
}

pub fn error(code: ErrorCode) -> Box<BridgeError> {
    Box::new(BridgeError {
        code,
        retry_disposition: match code {
            ErrorCode::StaleRevision | ErrorCode::ResnapshotRequired => {
                RetryDisposition::AfterResnapshot
            }
            ErrorCode::OperationBusy | ErrorCode::PlanExpired | ErrorCode::PlanHostMismatch => {
                RetryDisposition::AfterUserChoice
            }
            _ => RetryDisposition::Never,
        },
        supported_versions: None,
        violations: list(vec![]),
        expected_revision: None,
        observed_revision: None,
        recovery: None,
    })
}
fn recovery_error(recovery: RecoveryRef) -> Box<BridgeError> {
    Box::new(BridgeError {
        code: ErrorCode::RecoveryRequired,
        retry_disposition: RetryDisposition::AfterRecovery,
        recovery: Some(recovery),
        ..*error(ErrorCode::InternalFailure)
    })
}
fn list<T, const N: usize>(items: Vec<T>) -> BoundedList<T, N> {
    BoundedList::new(items).unwrap_or_else(|_| panic!("internal bounded collection exceeded"))
}
fn unavailable<T>() -> Observation<T> {
    Observation::Unavailable {
        reason: ObservationReason::NativeUnavailable,
    }
}
fn unknown_progress(phase: &str) -> Progress {
    Progress {
        phase: PhaseId::new(phase).expect("static phase"),
        measurement: Measurement::Unknown,
    }
}
fn validated_reply(reply: Reply) -> Result<ValidatedReply, KernelFailure> {
    let bytes = serde_json::to_vec(&reply).map_err(|_| KernelFailure::InvalidPortResult)?;
    decode_reply(&bytes).map_err(|_| KernelFailure::InvalidPortResult)
}
fn command_payload(command: CommandResult) -> ResultPayload {
    ResultPayload::Command {
        command: Box::new(command),
    }
}
fn query_payload(query: QueryResult) -> ResultPayload {
    ResultPayload::Query {
        query: Box::new(query),
    }
}
fn preflight_application(
    request_id: &RequestId,
    cursor: &Cursor,
    budget: usize,
    payload: &ResultPayload,
    changed: &[DraftSnapshot],
) -> Result<Vec<Event>, KernelFailure> {
    let reply = Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(request_id.clone())),
        body: ReplyBody::Result {
            result: payload.clone(),
        },
    };
    let bytes = serde_json::to_vec(&reply).map_err(|_| KernelFailure::InvalidPortResult)?;
    if bytes.len() > budget {
        return Err(KernelFailure::Capacity);
    }
    decode_reply(&bytes).map_err(|_| KernelFailure::InvalidPortResult)?;
    let mut next = cursor.clone();
    let mut events = Vec::with_capacity(changed.len());
    for draft in changed {
        if draft.draft.host_epoch != cursor.host_epoch {
            return Err(KernelFailure::InvalidPortResult);
        }
        next.sequence = Sequence::new(
            next.sequence
                .get()
                .checked_add(1)
                .ok_or(KernelFailure::CounterExhausted)?,
        );
        let event = Event {
            protocol_version: ProtocolVersion,
            cursor: next.clone(),
            body: EventBody::DraftChanged {
                draft: Box::new(draft.clone()),
            },
        };
        let bytes = serde_json::to_vec(&event).map_err(|_| KernelFailure::InvalidPortResult)?;
        if bytes.len() > budget {
            return Err(KernelFailure::Capacity);
        }
        decode_event(&bytes).map_err(|_| KernelFailure::InvalidPortResult)?;
        events.push(event);
    }
    Ok(events)
}
fn validate_command_output(command: CommandResult) -> Result<(), KernelFailure> {
    validated_reply(Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(
            RequestId::new("00000000-0000-4000-8000-000000000001").expect("static request UUID"),
        )),
        body: ReplyBody::Result {
            result: ResultPayload::Command {
                command: Box::new(command),
            },
        },
    })
    .map(|_| ())
}
fn query_reply(query: QueryResult) -> Reply {
    Reply {
        protocol_version: ProtocolVersion,
        request_id: ReplyRequestId::new(Some(
            RequestId::new("00000000-0000-4000-8000-000000000001").expect("static request UUID"),
        )),
        body: ReplyBody::Result {
            result: ResultPayload::Query {
                query: Box::new(query),
            },
        },
    }
}
pub(super) fn resources_unique(resources: &[ResourceKey]) -> bool {
    resources
        .iter()
        .enumerate()
        .all(|(i, r)| !resources[..i].contains(r))
}
fn normalize_resources(mut resources: Vec<ResourceKey>) -> Result<Vec<ResourceKey>, KernelFailure> {
    if resources.is_empty() || resources.len() > 16 || !resources_unique(&resources) {
        return Err(KernelFailure::InvalidPortResult);
    }
    resources.sort_by_cached_key(|r| serde_json::to_string(r).expect("resource serialization"));
    Ok(resources)
}
fn overlaps(left: &[ResourceKey], right: &[ResourceKey]) -> bool {
    left.iter().any(|r| right.contains(r))
}
fn validate_durable(operation: &DurableOperation) -> Result<(), KernelFailure> {
    validate_command_output(CommandResult::Commit(operation.snapshot.clone()))?;
    if normalize_resources(operation.resources.clone())? != operation.resources
        || !bindings::required_resources(&operation.snapshot.semantics.capture)
            .iter()
            .all(|r| operation.resources.contains(r))
        || semantic_plan_digest(&operation.snapshot.semantics)
            .map_err(|_| KernelFailure::CorruptJournal)?
            != operation.commit.plan_ref.review_digest
    {
        return Err(KernelFailure::CorruptJournal);
    }
    let mut probe = operation.snapshot.clone();
    probe.state = OperationState::RecoveryRequired {
        recovery: Box::new(operation.recovery.clone()),
        reason: RecoveryReason::InterruptedTransaction,
    };
    validate_command_output(CommandResult::Commit(probe))?;
    let correct_phase = match (&operation.phase, &operation.snapshot.state) {
        (DurablePhase::Admitted, OperationState::Admitted) => {
            !operation.cancellation_requested
                && operation.cancellable
                && !operation.safe_owner_boundary
                && operation.session_custody.is_none()
        }
        (DurablePhase::Executing, OperationState::Running { .. }) => {
            !operation.cancellation_requested
                && operation.session_custody.is_none()
                && !operation.safe_owner_boundary
        }
        (DurablePhase::Executing, OperationState::CancellationRequested { .. }) => {
            operation.cancellation_requested
                && operation.session_custody.is_none()
                && !operation.safe_owner_boundary
        }
        (DurablePhase::Terminal, OperationState::Completed { outcome }) => operation.safe_owner_boundary
            && session_custody_matches_capture(operation)
            && operation.session_custody.as_ref().is_none_or(|custody| {
                matches!(outcome, CompletionOutcome::Changed { receipt, .. }
                    if receipt.as_ref().is_none_or(|receipt| {
                        matches!(receipt.as_ref(), EffectReceipt::SessionSpawned { session } if &session.binding == custody)
                    }))
            }),
        (DurablePhase::Recovery, OperationState::RecoveryRequired { recovery, .. }) => {
            recovery.as_ref() == &operation.recovery
                && session_custody_matches_capture(operation)
        }
        _ => false,
    };
    if !correct_phase {
        return Err(KernelFailure::CorruptJournal);
    }
    Ok(())
}
fn session_custody_matches_capture(operation: &DurableOperation) -> bool {
    let Some(session) = &operation.session_custody else {
        return true;
    };
    match &operation.snapshot.semantics.capture {
        PreparedCapture::LaunchOrdinary { target, .. }
        | PreparedCapture::LaunchIsolated { target, .. } => {
            &session.process.installation_physical_id == target.installation.physical_id()
        }
        _ => false,
    }
}
fn validate_transition(
    previous: &DurableOperation,
    next: &DurableOperation,
) -> Result<(), KernelFailure> {
    if previous.commit != next.commit
        || previous.resources != next.resources
        || previous.recovery != next.recovery
        || previous.snapshot.operation_id != next.snapshot.operation_id
        || previous.snapshot.semantics != next.snapshot.semantics
        || previous.snapshot.operation_revision.get().checked_add(1)
            != Some(next.snapshot.operation_revision.get())
    {
        return Err(KernelFailure::CorruptJournal);
    }
    if previous.phase == DurablePhase::Terminal
        && (next.phase != DurablePhase::Terminal
            || previous.snapshot.state != next.snapshot.state
            || previous.session_custody.is_none()
            || next.session_custody.is_some())
    {
        return Err(KernelFailure::CorruptJournal);
    }
    if previous.phase != DurablePhase::Admitted && next.phase == DurablePhase::Admitted {
        return Err(KernelFailure::CorruptJournal);
    }
    Ok(())
}
