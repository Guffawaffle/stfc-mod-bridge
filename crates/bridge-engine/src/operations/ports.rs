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
    type Lease: ResourceLease;

    /// Read-only capture. Preparation must not reserve native resources or create
    /// stores, staging, downloads, backups or transaction journals.
    fn capture(
        &mut self,
        intent: &MutationIntent,
        host: &HostEpoch,
    ) -> Result<CapturedOperation, Box<BridgeError>>;
    /// All-or-none nonblocking acquisition through the canonical native owner.
    /// A refusal must have no mutation side effects.
    fn acquire(&mut self, resources: &[ResourceKey]) -> Result<Self::Lease, Box<BridgeError>>;
    /// Recheck physical identity, revisions and complete captured authority while
    /// every required owner exclusion is retained. Never silently substitute.
    fn revalidate(
        &mut self,
        semantics: &PlanSemantics,
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
        lease: &Self::Lease,
    ) -> Result<RecoveryRef, Box<BridgeError>>;
    /// Called only after durable executing state. It must not acknowledge a
    /// committed/rolled-back result until the native owner's state is durable.
    /// Every call remains under the same retained worker lease.
    fn advance(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &Self::Lease,
        cancellation_requested: bool,
    ) -> Result<TransactionStep, Box<BridgeError>>;
    /// Inspect and reconcile exact owned work after restart, under reacquired
    /// owner exclusions. Foreign replacements must stay explicitly unresolved.
    fn recover(
        &mut self,
        operation: &OperationSnapshot,
        recovery: &RecoveryRef,
        lease: &Self::Lease,
    ) -> Result<TransactionStep, Box<BridgeError>>;
    /// A session lease can leave host custody only after an actual canonical
    /// native/runtime handoff. UI/window closure alone cannot perform a handoff.
    fn handoff_session(
        &mut self,
        session: &SessionBinding,
        lease: &Self::Lease,
    ) -> Result<bool, Box<BridgeError>>;
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
