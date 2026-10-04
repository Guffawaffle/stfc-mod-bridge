use super::*;
use bridge_contracts::v1::*;

/// These traits intentionally have no Send/Sync bounds. Native leases and
/// protected handles are constructed and retained inside one engine actor.
pub trait DocumentOwner {
    type Lease;
    type Transaction;
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
    /// Native/headless protected entry. No renderer string is accepted here.
    fn capture(
        &mut self,
        request: &RequestSensitiveInputInput,
    ) -> ConfigurationResult<ProtectedEntryOutcome>;
}
