use super::{
    configuration::*, distribution::*, management::*, primitives::*, projections::*, targets::*,
    workflow::*,
};
use schemars::{JsonSchema, Schema, SchemaGenerator};
use serde::{Deserialize, Serialize};
use std::borrow::Cow;

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct EmptyInput {}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResolveTargetInput {
    pub target: TargetSelector,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GetOperationInput {
    pub operation_id: OperationId,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "name",
    content = "input",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum Query {
    Hello(EmptyInput),
    ResolveTarget(ResolveTargetInput),
    GetOperation(GetOperationInput),
    Snapshot(EmptyInput),
    ListProfiles(ListProfilesInput),
    ListInstallations(EmptyInput),
    ListSessions(EmptyInput),
    ListImportSources(EmptyInput),
    GetActions(GetActionsInput),
    ReadConfiguration(ReadConfigurationInput),
    ConfigurationHistory(ConfigurationHistoryInput),
    CheckRuntimeRelease(CheckRuntimeReleaseInput),
    CheckGameUpdate(CheckGameUpdateInput),
    CheckBridgeUpdate(CheckBridgeUpdateInput),
    ResumeEvents(ResumeEventsInput),
    DiagnosticPreview(DiagnosticPreviewInput),
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ProfileFilter {
    Active,
    Archived,
    All,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ListProfilesInput {
    pub state: ProfileFilter,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GetActionsInput {
    pub scope: ActionScope,
    pub actions: BoundedList<ActionId, 64>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ReadConfigurationInput {
    pub target: TargetSelector,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ConfigurationHistoryInput {
    pub document: DocumentBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CheckRuntimeReleaseInput {
    pub target: ResolvedTarget,
    pub provider_id: ProviderId,
    pub channel_id: ChannelId,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CheckGameUpdateInput {
    pub installation: InstallationSelector,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CheckBridgeUpdateInput {
    pub application: BridgeApplicationBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResumeEventsInput {
    pub after: Cursor,
    pub maximum_events: ProgressCount,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DiagnosticPreviewInput {
    pub target: TargetSelector,
    pub disclosure: DiagnosticDisclosure,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PrepareInput {
    pub intent: MutationIntent,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CommitInput {
    pub plan_ref: PlanRef,
    pub idempotency_key: IdempotencyKey,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CancelOperationInput {
    pub operation_id: OperationId,
    pub expected_operation_revision: RevisionCounter,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RequestHostCloseInput {
    pub expected_cursor: Cursor,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "name",
    content = "input",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum Command {
    Prepare(Box<PrepareInput>),
    Commit(CommitInput),
    CancelOperation(CancelOperationInput),
    RequestHostClose(RequestHostCloseInput),
    OpenDraft(OpenDraftInput),
    SetDraftChanges(SetDraftChangesInput),
    DiscardDraft(DiscardDraftInput),
    RequestImportDiscovery(RequestImportDiscoveryInput),
    RequestSensitiveInput(RequestSensitiveInputInput),
    RequestExportDestination(RequestExportDestinationInput),
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct OpenDraftInput {
    pub document: DocumentBinding,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SetDraftChangesInput {
    pub draft: DraftRef,
    pub edits: BoundedList<ConfigurationEdit, 256>,
}
impl SetDraftChangesInput {
    pub(crate) fn valid(&self) -> bool {
        self.edits
            .as_slice()
            .iter()
            .all(|edit| edit.valid(&self.draft))
            && super::unique_by(self.edits.as_slice(), ConfigurationEdit::key)
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DiscardDraftInput {
    pub draft: DraftRef,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RequestImportDiscoveryInput {
    pub expected_destination_owner: OwnerScope,
    pub approval: NativeApprovalChoice,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DiscardedDraft {
    pub draft_id: DraftId,
    pub host_epoch: HostEpoch,
    pub previous_revision: RevisionCounter,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum RequestBody {
    Query { query: Query },
    Command { command: Command },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Request {
    pub protocol_version: ProtocolVersion,
    pub request_id: RequestId,
    pub body: RequestBody,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum HostKind {
    WindowsX64,
    MacosArm64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum CommandId {
    Prepare,
    Commit,
    CancelOperation,
    RequestHostClose,
    OpenDraft,
    SetDraftChanges,
    DiscardDraft,
    RequestImportDiscovery,
    RequestSensitiveInput,
    RequestExportDestination,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct HelloResult {
    pub supported_versions: [ProtocolVersion; 1],
    pub host_epoch: HostEpoch,
    pub host_kind: HostKind,
    pub implemented_commands: BoundedList<CommandId, 32>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ResolveTargetResult {
    pub target: Observation<ResolvedTarget>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct GetOperationResult {
    pub operation: Observation<OperationSnapshot>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "name",
    content = "output",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum QueryResult {
    Hello(HelloResult),
    ResolveTarget(ResolveTargetResult),
    GetOperation(Box<GetOperationResult>),
    Snapshot(Snapshot),
    ListProfiles(Observation<Inventory<ProfileProjection>>),
    ListInstallations(Observation<Inventory<InstallationProjection>>),
    ListSessions(Observation<Inventory<SessionProjection>>),
    ListImportSources(Observation<Inventory<ImportSourceProjection>>),
    GetActions(BoundedList<ActionProjection, 64>),
    ReadConfiguration(Observation<DocumentSnapshot>),
    ConfigurationHistory(Observation<Inventory<BackupReceiptRef>>),
    CheckRuntimeRelease(Observation<RuntimeReleaseSelectionRef>),
    CheckGameUpdate(Observation<CheckedGameUpdateRef>),
    CheckBridgeUpdate(Observation<BridgeReleaseSelectionRef>),
    ResumeEvents(EventBatch),
    DiagnosticPreview(DiagnosticPreview),
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "name",
    content = "output",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum CommandResult {
    Prepare(PreparedPlan),
    Commit(OperationSnapshot),
    CancelOperation(CancelDisposition),
    RequestHostClose(CloseDisposition),
    OpenDraft(DraftSnapshot),
    SetDraftChanges(Box<SetDraftChangesResult>),
    DiscardDraft(DiscardedDraft),
    RequestImportDiscovery(Observation<Inventory<ImportSourceProjection>>),
    RequestSensitiveInput(SensitiveInputResult),
    RequestExportDestination(ExportDestinationResult),
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum ResultPayload {
    Query { query: Box<QueryResult> },
    Command { command: Box<CommandResult> },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum ReplyBody {
    Result { result: ResultPayload },
    Rejected { error: Box<BridgeError> },
}

// The envelope must contain requestId even when its value is null. An Option
// struct field would make it omittable in the derived deserialize schema.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(transparent)]
pub struct ReplyRequestId(Option<RequestId>);
impl ReplyRequestId {
    pub fn new(value: Option<RequestId>) -> Self {
        Self(value)
    }
    pub fn as_ref(&self) -> Option<&RequestId> {
        self.0.as_ref()
    }
}
impl JsonSchema for ReplyRequestId {
    fn schema_name() -> Cow<'static, str> {
        "ReplyRequestId".into()
    }
    fn json_schema(g: &mut SchemaGenerator) -> Schema {
        serde_json::json!({"anyOf":[g.subschema_for::<RequestId>(),{"type":"null"}]})
            .try_into()
            .expect("static schema")
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Reply {
    pub protocol_version: ProtocolVersion,
    pub request_id: ReplyRequestId,
    pub body: ReplyBody,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SnapshotInvalidationReason {
    CatalogChanged,
    SessionChanged,
    OperationChanged,
    HostRestarted,
    RetentionGap,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "type", rename_all = "snake_case", deny_unknown_fields)]
pub enum EventBody {
    SnapshotInvalidated {
        reason: SnapshotInvalidationReason,
    },
    OperationChanged {
        operation: Box<OperationSnapshot>,
    },
    HostCloseDeferred {
        obligations: BoundedList<CloseObligation, 128>,
    },
    DraftChanged {
        draft: Box<DraftSnapshot>,
    },
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Event {
    pub protocol_version: ProtocolVersion,
    pub cursor: Cursor,
    pub body: EventBody,
}

impl Request {
    pub(crate) fn valid(&self) -> bool {
        match &self.body {
            RequestBody::Command { command } => match command {
                Command::Prepare(input) => input.intent.valid(),
                Command::SetDraftChanges(input) => input.valid(),
                _ => true,
            },
            RequestBody::Query { query } => match query {
                Query::GetActions(input) => {
                    !input.actions.as_slice().is_empty()
                        && super::unique(input.actions.as_slice())
                        && match &input.scope {
                            ActionScope::Application { application } => application.valid(),
                            _ => true,
                        }
                }
                Query::CheckBridgeUpdate(input) => input.application.valid(),
                Query::ResumeEvents(input) => {
                    input.maximum_events.get() > 0 && input.maximum_events.get() <= 128
                }
                _ => true,
            },
        }
    }
}
impl Reply {
    pub(crate) fn valid(&self) -> bool {
        if self.request_id.as_ref().is_none()
            && !matches!(&self.body,ReplyBody::Rejected{error} if matches!(error.code,ErrorCode::InvalidRequest|ErrorCode::UnsupportedProtocol))
        {
            return false;
        }
        match &self.body {
            ReplyBody::Rejected { error } => error.valid(),
            ReplyBody::Result { result } => match result {
                ResultPayload::Query { query } => match query.as_ref() {
                    QueryResult::Hello(output) => {
                        let commands = output.implemented_commands.as_slice();
                        commands
                            .iter()
                            .enumerate()
                            .all(|(i, c)| !commands[..i].contains(c))
                    }
                    QueryResult::ResolveTarget(_) => true,
                    QueryResult::GetOperation(output) => match &output.operation {
                        Observation::Observed { value, .. } => value.valid(),
                        _ => true,
                    },
                    QueryResult::Snapshot(output) => output.valid(),
                    QueryResult::ListProfiles(output) => {
                        observation_valid(output, Inventory::valid)
                    }
                    QueryResult::ListInstallations(output) => {
                        observation_valid(output, |inventory| {
                            inventory.valid()
                                && inventory
                                    .items
                                    .as_slice()
                                    .iter()
                                    .all(InstallationProjection::valid)
                        })
                    }
                    QueryResult::ListSessions(output) => observation_valid(output, |inventory| {
                        inventory.valid()
                            && inventory
                                .items
                                .as_slice()
                                .iter()
                                .all(SessionProjection::valid)
                    }),
                    QueryResult::ListImportSources(output) => {
                        observation_valid(output, Inventory::valid)
                    }
                    QueryResult::GetActions(output) => {
                        super::unique_by(output.as_slice(), |a| a.action)
                            && output.as_slice().iter().all(|a| a.availability.valid())
                    }
                    QueryResult::ReadConfiguration(output) => {
                        observation_valid(output, DocumentSnapshot::valid)
                    }
                    QueryResult::ConfigurationHistory(output) => {
                        observation_valid(output, |inventory| {
                            inventory.valid()
                                && inventory
                                    .items
                                    .as_slice()
                                    .iter()
                                    .all(BackupReceiptRef::valid)
                        })
                    }
                    QueryResult::CheckRuntimeRelease(output) => {
                        observation_valid(output, RuntimeReleaseSelectionRef::valid)
                    }
                    QueryResult::CheckGameUpdate(_) => true,
                    QueryResult::CheckBridgeUpdate(output) => {
                        observation_valid(output, BridgeReleaseSelectionRef::valid)
                    }
                    QueryResult::ResumeEvents(output) => output.valid(),
                    QueryResult::DiagnosticPreview(output) => output.valid(),
                },
                ResultPayload::Command { command } => match command.as_ref() {
                    CommandResult::Prepare(plan) => {
                        plan.semantics.valid()
                            && plan
                                .semantics
                                .capture
                                .host_matches(&plan.plan_ref.host_epoch)
                            && super::semantic_plan_digest(&plan.semantics)
                                .is_ok_and(|d| d == plan.plan_ref.review_digest)
                    }
                    CommandResult::Commit(operation) => operation.valid(),
                    CommandResult::CancelOperation(disposition) => disposition.valid(),
                    CommandResult::RequestHostClose(disposition) => disposition.valid(),
                    CommandResult::OpenDraft(output) => output.valid(),
                    CommandResult::SetDraftChanges(output) => output.valid(),
                    CommandResult::DiscardDraft(_) => true,
                    CommandResult::RequestImportDiscovery(output) => {
                        observation_valid(output, Inventory::valid)
                    }
                    CommandResult::RequestSensitiveInput(output) => output.valid(),
                    CommandResult::RequestExportDestination(output) => output.valid(),
                },
            },
        }
    }
}
impl Event {
    pub(crate) fn valid(&self) -> bool {
        match &self.body {
            EventBody::OperationChanged { operation } => operation.valid(),
            EventBody::HostCloseDeferred { obligations } => !obligations.as_slice().is_empty(),
            EventBody::SnapshotInvalidated { .. } => true,
            EventBody::DraftChanged { draft } => {
                draft.valid() && draft.draft.host_epoch == self.cursor.host_epoch
            }
        }
    }
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct EventBatch {
    pub after: Cursor,
    pub events: BoundedList<Event, 128>,
    pub next: Cursor,
}
impl EventBatch {
    pub(crate) fn valid(&self) -> bool {
        let mut cursor = &self.after;
        for event in self.events.as_slice() {
            if !event.valid()
                || event.cursor.host_epoch != cursor.host_epoch
                || event.cursor.stream_id != cursor.stream_id
                || cursor.sequence.get().checked_add(1) != Some(event.cursor.sequence.get())
            {
                return false;
            }
            cursor = &event.cursor;
        }
        cursor == &self.next
    }
}
