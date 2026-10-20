//! Canonical owners implement these ports. The engine owns scheduling and
//! observation, not a second catalog, native writer or account authority.
use bridge_contracts::v1::*;
use serde::{Deserialize, Serialize};

/// Resources are semantic exclusion keys, never paths or serialized handles.
/// A native adapter must map them to the canonical owner's lifetime exclusions.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ResourceKey {
    Installation {
        physical_id: PhysicalInstallationId,
    },
    IsolatedProfile {
        id: ProfileId,
    },
    OrdinaryProfile {
        owner: OwnerScope,
    },
    Catalog {
        owner: OwnerScope,
    },
    Document {
        id: DocumentId,
        target: ResolvedTarget,
    },
    Application {
        identity: ApplicationIdentity,
    },
    Export {
        destination: ExportDestinationId,
    },
}

#[derive(Clone, Debug)]
pub struct CapturedOperation {
    pub semantics: PlanSemantics,
    pub resources: Vec<ResourceKey>,
}

/// Dropping a client does not drop this lease: it is stored by the worker.
pub trait ResourceLease {
    fn resources(&self) -> &[ResourceKey];
}

#[derive(Clone, Debug)]
pub struct ClockReading {
    /// Host-local elapsed milliseconds, including suspension. This has no UTC
    /// meaning and is never supplied by a renderer.
    pub monotonic_millis: u64,
    /// Wall-clock evidence only; wall adjustments do not change plan expiry.
    pub utc: UtcTimestamp,
}

/// Provider failures carry no native paths, diagnostic strings or entropy.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ProviderFailure {
    ClockUnavailable,
    ClockRange,
    MonotonicRegression,
    DeadlineOverflow,
    EntropyUnavailable,
    InvalidIdentity,
}

pub trait HostClock: Send {
    fn now(&self) -> Result<ClockReading, ProviderFailure>;
    /// Derive both projections from this supplied sample without resampling.
    /// UTC addition and representability are the provider's obligations.
    fn deadline(
        &self,
        sampled: &ClockReading,
        lifetime_millis: u64,
    ) -> Result<ClockReading, ProviderFailure>;
}

pub trait IdentitySource: Send {
    fn plan_id(&mut self) -> Result<PlanId, ProviderFailure>;
    fn operation_id(&mut self) -> Result<OperationId, ProviderFailure>;
}

/// One bounded advancement of a native transaction. Unknown progress stays
/// unknown. A complete receipt must describe the exact captured transaction.
#[derive(Clone, Debug)]
pub enum TransactionStep {
    Progress {
        progress: Progress,
        cancellable: bool,
    },
    /// A spawned session remains in host custody until a qualified native/runtime
    /// handoff, represented here by its exact binding. Returning no custody for
    /// a spawned session requires that the canonical owner already retains that
    /// lifetime obligation; neither UI closure nor a completed receipt is proof.
    Complete {
        outcome: CompletionOutcome,
        session_custody: Option<SessionBinding>,
    },
    RecoveryRequired {
        reason: RecoveryReason,
        safe_owner_boundary: bool,
    },
}

pub trait OperationPorts {
    /// Local owner state, moved from preparation to the admitted worker exactly
    /// once. It is never a DTO, digest input or journal record. No cloning,
    /// serialization, debugging or cross-thread transfer is required.
    type Custody;
    type Lease: ResourceLease;

    /// Read-only capture. Preparation must not reserve native resources or create
    /// stores, staging, downloads, backups or transaction journals.
    fn capture(
        &mut self,
        intent: &MutationIntent,
        host: &HostEpoch,
    ) -> Result<(CapturedOperation, Self::Custody), Box<BridgeError>>;
    /// All-or-none nonblocking acquisition through the canonical native owner.
    /// A refusal must have no mutation side effects.
    fn acquire(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        resources: &[ResourceKey],
    ) -> Result<Self::Lease, Box<BridgeError>>;
    /// Reconstruct recovery-only custody from exact durable identities; never
    /// recapture a draft or recreate old protected preparation bytes. Return
    /// custody before lease so the engine can destroy it while exclusions live.
    fn acquire_recovery(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        resources: &[ResourceKey],
    ) -> Result<(Self::Custody, Self::Lease), Box<BridgeError>>;
    /// Recheck physical identity, revisions and complete captured authority while
    /// every required owner exclusion is retained. Never silently substitute.
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        lease: &Self::Lease,
    ) -> Result<(), Box<BridgeError>>;
    /// Supply an exact owner transaction binding, without mutation side effects.
    /// The owner must be able to inspect this reference after forced process death
    /// and distinguish never-started, committed, rolled-back and unresolved work.
    /// The engine durably records it before admitting or advancing any effect.
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        semantics: &PlanSemantics,
        custody: &Self::Custody,
        lease: &Self::Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>>;
    /// Called only after durable executing state. It must not acknowledge a
    /// committed/rolled-back result until the native owner's state is durable.
    /// Every call remains under the same retained worker lease.
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
        cancellation_requested: bool,
    ) -> Result<TransactionStep, Box<BridgeError>>;
    /// Inspect and reconcile exact owned work after restart, under reacquired
    /// owner exclusions. Foreign replacements must stay explicitly unresolved.
    fn recover(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>>;
    /// Local successors still need publication even if the native writer is
    /// already at a safe durable boundary. Such custody cannot be released.
    fn completion_pending(&self, _custody: &Self::Custody) -> bool {
        false
    }
    /// Supply every local draft successor to the kernel's durable commit
    /// callback exactly once, then publish locally with no fallible work.
    /// A refused callback must leave local state and pending custody intact.
    /// The port receives no cursor, sequence, journal or kernel controls.
    fn publish_completion(
        &mut self,
        _operation: &OperationSnapshot,
        _custody: &mut Self::Custody,
        _lease: &Self::Lease,
        commit: &mut CompletionCommit<'_>,
    ) -> Result<(), Box<BridgeError>> {
        commit(&[])
    }
    /// Pure admission policy, captured once in the WAL before execution. Owners
    /// which publish durable reservations must opt in. The default is only for
    /// compositions with no such reservation, not evidence of native safety.
    fn requires_terminal_settlement(
        &self,
        _semantics: &PlanSemantics,
        _custody: &Self::Custody,
    ) -> bool {
        false
    }
    /// Called under retained or exactly reacquired exclusions after terminal
    /// WAL and local completion publication. Never begin/advance/recover a
    /// mutation here. Settle the captured reservation durably and idempotently,
    /// including cancellation before begin and a restart after physical
    /// settlement but before the kernel acknowledgment. Errors retain custody.
    fn settle_terminal(
        &mut self,
        _operation: &OperationSnapshot,
        _recovery: &RecoveryRef,
        _custody: &mut Self::Custody,
        _lease: &Self::Lease,
    ) -> Result<SettlementStep, Box<BridgeError>> {
        Err(super::error(ErrorCode::UnsupportedCapability))
    }
    /// A session lease can leave host custody only after an actual canonical
    /// native/runtime handoff. UI/window closure alone cannot perform a handoff.
    fn handoff_session(
        &mut self,
        session: &SessionBinding,
        custody: &mut Self::Custody,
        lease: &Self::Lease,
    ) -> Result<bool, Box<BridgeError>>;
}

pub type CompletionCommit<'a> = dyn FnMut(&[DraftSnapshot]) -> Result<(), Box<BridgeError>> + 'a;

/// Reservation settlement is independent of the already committed outcome.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SettlementStep {
    Pending,
    Settled,
}

/// Bounded storage interface. Success means the record and all prior records
/// are durable. Errors have unknown persistence disposition; poison the host.
pub trait DurableJournal {
    fn records(&self) -> &[super::journal::JournalRecord];
    fn append(
        &mut self,
        record: &super::journal::JournalRecord,
    ) -> Result<(), super::KernelFailure>;
}
