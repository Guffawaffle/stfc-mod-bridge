//! Actor-local application read/stage ports. The engine owns all envelopes,
//! host identities and event cursors; a service cannot invoke kernel controls.
use crate::operations::error;
use bridge_contracts::v1::*;

pub type ApplicationResult<T> = Result<T, Box<BridgeError>>;
pub type Preflight<'a, T> = dyn FnMut(&T) -> ApplicationResult<()> + 'a;
pub type ReadPreflight<'a> =
    dyn FnMut(&DocumentSnapshot, &[DraftSnapshot]) -> ApplicationResult<()> + 'a;
pub type StagePreflight<'a> = dyn FnMut(&SetDraftChangesResult, bool) -> ApplicationResult<()> + 'a;

/// Implement on the same object that supplies OperationPorts to Engine.
/// Mutating methods must invoke preflight exactly once before publishing local
/// state, then return that exact result. Refusal preserves draft/vault state;
/// identity attempts and owner reads need not be undone. Stage's changed flag
/// distinguishes a fresh successor from an exact lost-ack replay.
pub trait ApplicationServices {
    fn configuration_host_epoch(&self) -> Option<&HostEpoch> {
        None
    }
    fn implemented_commands(&self) -> Vec<CommandId> {
        vec![]
    }
    fn read_configuration(
        &mut self,
        _input: &ReadConfigurationInput,
        _preflight: &mut ReadPreflight<'_>,
    ) -> ApplicationResult<DocumentSnapshot> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn configuration_history(
        &mut self,
        _input: &ConfigurationHistoryInput,
    ) -> ApplicationResult<Inventory<BackupReceiptRef>> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn get_draft(&self, _input: &GetDraftInput) -> ApplicationResult<Option<DraftSnapshot>> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn open_draft(
        &mut self,
        _input: &OpenDraftInput,
        _preflight: &mut Preflight<'_, DraftSnapshot>,
    ) -> ApplicationResult<DraftSnapshot> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn set_draft_changes(
        &mut self,
        _input: SetDraftChangesInput,
        _preflight: &mut StagePreflight<'_>,
    ) -> ApplicationResult<SetDraftChangesResult> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn discard_draft(
        &mut self,
        _input: &DiscardDraftInput,
        _preflight: &mut Preflight<'_, DiscardedDraft>,
    ) -> ApplicationResult<DiscardedDraft> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
    fn request_sensitive_input(
        &mut self,
        _input: &RequestSensitiveInputInput,
        _preflight: &mut Preflight<'_, SensitiveInputResult>,
    ) -> ApplicationResult<SensitiveInputResult> {
        Err(error(ErrorCode::UnsupportedCapability))
    }
}
