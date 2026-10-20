use super::*;
use bridge_contracts::v1::*;

/// These traits intentionally have no Send/Sync bounds. Native leases and
/// protected handles are constructed and retained inside one engine actor.
pub trait DocumentOwner {
    type Lease;
    type Transaction;
    /// Availability of this injected composition, not platform qualification.
    /// Production owners keep the default until their writer and recovery
    /// composition is implemented and independently qualified on that host.
    fn operations_available(&self) -> bool {
        false
    }
    /// Durable reservation owners opt in; availability is a separate native
    /// qualification boundary. Captured by the operation kernel at admission.
    fn requires_terminal_settlement(&self) -> bool {
        false
    }
    /// Idempotent post-terminal reservation settlement under exact canonical
    /// exclusions. This never repeats begin/advance or local draft publication.
    /// A cancelled-before-begin operation still has an admission reservation.
    fn settle_terminal(
        &mut self,
        _captured: &RecoveryConfiguration,
        _recovery: &RecoveryRef,
        _outcome: &CompletionOutcome,
        _lease: &Self::Lease,
    ) -> ConfigurationResult<crate::operations::SettlementStep> {
        Err(ConfigurationFailure::NativeUnavailable)
    }
    fn resolve(&mut self, target: &TargetSelector) -> ConfigurationResult<DocumentRead>;
    fn read(&mut self, expected: &DocumentBinding) -> ConfigurationResult<DocumentRead>;
    /// All-or-none nonblocking canonical document, installation and profile
    /// custody. A losing caller creates no stage, journal or backup.
    fn acquire(&mut self, expected: &DocumentBinding) -> ConfigurationResult<Self::Lease>;
    fn revalidate(
        &mut self,
        expected: &DocumentBinding,
        lease: &Self::Lease,
    ) -> ConfigurationResult<()>;
    /// No effects: the kernel records this reference durably before begin.
    fn recovery_binding(
        &mut self,
        operation: &OperationId,
        prepared: &PreparedConfiguration,
        lease: &Self::Lease,
    ) -> ConfigurationResult<RecoveryRef>;
    /// Called only after the kernel has durably admitted execution. Retain the
    /// supplied lease externally through every step and terminal inspection.
    fn begin(
        &mut self,
        prepared: &PreparedConfiguration,
        recovery: &RecoveryRef,
        lease: &Self::Lease,
    ) -> ConfigurationResult<Self::Transaction>;
    fn advance(
        &mut self,
        transaction: &mut Self::Transaction,
        lease: &Self::Lease,
        cancellation_requested: bool,
    ) -> ConfigurationResult<OwnerOutcome>;
    /// Reconcile exact deterministic owned subjects. Never overwrite a foreign
    /// destination or recreate missing protected entry data after host death.
    fn recover(
        &mut self,
        captured: &RecoveryConfiguration,
        recovery: &RecoveryRef,
        lease: &Self::Lease,
    ) -> ConfigurationResult<OwnerOutcome>;
    fn history(
        &mut self,
        document: &DocumentBinding,
    ) -> ConfigurationResult<Inventory<BackupReceiptRef>>;
    fn backup(&mut self, receipt: &BackupReceiptRef) -> ConfigurationResult<Vec<u8>>;
    /// Verify the exact retained owner subject under admission custody, and
    /// keep its native identity through Restore. A prep-time hash is not a lock.
    fn revalidate_backup(
        &mut self,
        receipt: &BackupReceiptRef,
        lease: &Self::Lease,
    ) -> ConfigurationResult<()>;
}

pub enum ProtectedEntryOutcome {
    Captured(ProtectedValue),
    Cancelled,
    Unavailable(SensitiveInputUnavailableReason),
}
pub trait SensitiveEntry {
    /// Capability of this injected entry port. Unimplemented/headless ports
    /// must keep the default; an outcome still reports per-request refusal.
    fn is_available(&self) -> bool {
        false
    }
    /// Native/headless protected entry. No renderer string is accepted here.
    fn capture(
        &mut self,
        request: &RequestSensitiveInputInput,
    ) -> ConfigurationResult<ProtectedEntryOutcome>;
}
