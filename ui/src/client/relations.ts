import type { BridgeApplicationBinding, BridgeRecoveryRef, CancelDisposition, CancelOperationInput, DiagnosticContent, DiagnosticPreview, DiagnosticPreviewInput, DocumentBinding, DraftRef, DraftSnapshot, InstallationBinding, InstallationSelector, IsolatedProfileRef, MutationIntent, OperationSnapshot, OperationState, OrdinaryProfileRef, PlanSemantics, PrivateValueRef, ProfileBinding, ProfileSelector, RecoveryRef, RegisteredInstallationBinding, RequestExportDestinationInput, RequestSensitiveInputInput, ResolvedTarget, SecretRef } from '../generated/protocol';
import { canonicalData, captureData, type DeepReadonly } from './wire';

// In the generated contract's semantic/input closure, nullable object fields
// are optional Rust Option values serialized with skip_serializing_if=None.
// The schema drift regression refuses required nulls, nullable array elements,
// open objects and non-ASCII property names before that assumption can change.
function omitNone(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(omitNone);
  if (value !== null && typeof value === 'object') {
    const result: Record<string, unknown> = Object.create(null);
    for (const [key, entry] of Object.entries(value)) if (entry !== null) result[key] = omitNone(entry);
    return result;
  }
  return value;
}
const equalDto = (left: unknown, right: unknown) => canonicalData(omitNone(left)) === canonicalData(omitNone(right));

/** Validated binding DTOs only; envelopes and exact command/edit intent are excluded. */
export type ComparableBinding = DocumentBinding | DraftRef | DraftSnapshot | PrivateValueRef | SecretRef | RequestSensitiveInputInput | RequestExportDestinationInput | OperationState | RecoveryRef
  | ResolvedTarget | BridgeApplicationBinding | IsolatedProfileRef | OrdinaryProfileRef | RegisteredInstallationBinding;
/** Known opaque-reference DTO closure only; public edits retain exact encoding. */
export function protectedReferenceKey(reference: PrivateValueRef | SecretRef | DeepReadonly<PrivateValueRef> | DeepReadonly<SecretRef>): string {
  return canonicalData(omitNone(captureData(reference)));
}
export function bindingEquivalent<T extends ComparableBinding>(left: T | DeepReadonly<T>, right: T | DeepReadonly<T>): boolean {
  return equalDto(captureData(left), captureData(right));
}

function installationMatches(selector: DeepReadonly<InstallationSelector>, binding: DeepReadonly<InstallationBinding>): boolean {
  if (selector.kind === 'registered') {
    return binding.kind === 'registered' && selector.id === binding.registrationId
      && (selector.revisionAssertion == null || selector.revisionAssertion === binding.registrationRevision);
  }
  // A directory path/assertion cannot establish the physical identity of an
  // opaque native binding. The native owner resolves and revalidates it.
  return true;
}
function profileMatches(selector: DeepReadonly<ProfileSelector>, binding: DeepReadonly<ProfileBinding>): boolean {
  if (selector.kind === 'ordinary' && binding.kind === 'ordinary') return selector.catalogIdAssertion == null || selector.catalogIdAssertion === binding.ordinaryId;
  return selector.kind === 'isolated' && binding.kind === 'isolated' && selector.id === binding.id
    && (selector.revisionAssertion == null || selector.revisionAssertion === binding.revision);
}
function bridgeApplicationValid(application: DeepReadonly<BridgeApplicationBinding>): boolean {
  return application.payloads.length > 0 && application.payloads.some(payload => payload.role === 'application')
    && new Set(application.payloads.map(payload => payload.role)).size === application.payloads.length
    && application.payloads.every(payload => BigInt(payload.size) > 0n)
    && (application.platform === 'windows' && application.architecture === 'x86_64'
      || application.platform === 'macos' && application.architecture === 'arm64');
}
function bridgeRecoveryValid(recovery: DeepReadonly<BridgeRecoveryRef>): boolean {
  const a = recovery.application, b = recovery.expectedApplication;
  return bridgeApplicationValid(a) && bridgeApplicationValid(b) && a.applicationId === b.applicationId
    && a.installationRef === b.installationRef && a.channelId === b.channelId && a.platform === b.platform && a.architecture === b.architecture;
}

/** Diagnostic request/content correlation; physical directory resolution remains native. */
export function diagnosticPreviewMatches(input: DeepReadonly<DiagnosticPreviewInput>, preview: DeepReadonly<DiagnosticPreview>): boolean {
  const { reference, content } = preview;
  if (input.disclosure !== reference.disclosure || reference.disclosure !== content.disclosure
    || !equalDto(reference.target, content.target)
    || !installationMatches(input.target.installation, content.target.installation) || !profileMatches(input.target.profile, content.target.profile)
    || content.disclosure === 'redacted' && content.paths != null) return false;
  return content.facts.every(fact => fact.kind === 'target' ? equalDto(fact.value, content.target)
    : fact.kind === 'session' && fact.value.status === 'observed' ? fact.value.value.process.installationPhysicalId === content.target.installation.physicalId
    : fact.kind === 'runtime' && fact.value.status === 'observed' && fact.value.value.kind === 'managed' ? equalDto(fact.value.value.reference.target, content.target)
    : fact.kind === 'bridge' && fact.value.status === 'observed' ? bridgeApplicationValid(fact.value.value)
    : true);
}

/** Rust's recorded recovery/capture relationship, over already validated DTOs. */
export function operationRecoveryMatches(operation: DeepReadonly<OperationSnapshot>): boolean {
  if (operation.state.status !== 'recovery_required') return true;
  const recovery = operation.state.recovery, target = recovery.target, capture = operation.semantics.capture;
  if (recovery.operationId !== operation.operationId) return false;
  switch (target.kind) {
    case 'launch': return (capture.kind === 'launch_ordinary' || capture.kind === 'launch_isolated') && equalDto(target.target, capture.target);
    case 'session': return capture.kind === 'focus_session' && equalDto(target.session, capture.session) && equalDto(target.target, capture.target);
    case 'profile': return ['edit_isolated_profile', 'archive_profile', 'restore_profile', 'delete_profile'].includes(capture.kind)
      && (capture.kind === 'edit_isolated_profile' || capture.kind === 'archive_profile' || capture.kind === 'restore_profile' || capture.kind === 'delete_profile')
      && equalDto(target.profile, capture.input.profile);
    case 'ordinary_profile': return capture.kind === 'edit_ordinary_profile' && equalDto(target.profile, capture.input.profile);
    case 'profile_creation': return capture.kind === 'create_profile' && target.owner === capture.input.destinationOwner && target.nativePreparationRef === capture.input.nativePreparationRef;
    case 'installation': return capture.kind === 'edit_installation' && equalDto(target.installation, capture.input.installation);
    case 'installation_registration': return capture.kind === 'register_installation' && target.physicalId === capture.input.physicalId && target.nativeTargetRef === capture.input.nativeTargetRef;
    case 'configuration': return capture.kind === 'save_configuration' ? equalDto(target.document, capture.input.draft.draft.document)
      : capture.kind === 'restore_configuration' && equalDto(target.document, capture.input.document);
    case 'runtime': return capture.kind === 'runtime_install' || capture.kind === 'runtime_update' || capture.kind === 'runtime_repair' || capture.kind === 'runtime_adopt'
      ? equalDto(target.target, capture.input.target)
      : capture.kind === 'runtime_remove' || capture.kind === 'runtime_stop_managing' ? equalDto(target.target, capture.input.reference.target)
      : capture.kind === 'runtime_switch_source' && equalDto(target.target, capture.input.current.target);
    case 'game': return capture.kind === 'game_update' ? equalDto(target.recovery.installation, capture.input.checkedUpdate.installation)
      && capture.input.checkedUpdate.route === 'canonical_native_direct' && equalDto(target.recovery.expectedClient, capture.input.checkedUpdate.currentClient)
      : capture.kind === 'recover_game_update' && equalDto(target.recovery, capture.input.recovery);
    case 'bridge': return bridgeRecoveryValid(target.recovery) && (capture.kind === 'bridge_update'
      ? equalDto(target.recovery.application, capture.input.selectedRelease.current)
        && (equalDto(target.recovery.expectedApplication, capture.input.selectedRelease.current) || equalDto(target.recovery.expectedApplication, capture.input.selectedRelease.offered))
      : capture.kind === 'recover_bridge_update' && equalDto(target.recovery, capture.input.recovery));
    case 'diagnostics': return capture.kind === 'export_diagnostics' && equalDto(target.preview, capture.input.preview) && equalDto(target.destination, capture.input.destination);
    case 'application_preferences': return capture.kind === 'save_application_preferences' && target.revision === capture.input.expectedRevision;
  }
}
export async function diagnosticPreviewDigest(content: DeepReadonly<DiagnosticContent>): Promise<string> {
  const bytes = new TextEncoder().encode('bridge-diagnostic-preview-json-v1\0' + canonicalData(omitNone(captureData(content))));
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return 'sha256:' + Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}

/** A cancellation disposition describes observed state, never promised cancellation. */
export function cancellationMatches(input: DeepReadonly<CancelOperationInput>, result: DeepReadonly<CancelDisposition>): boolean {
  const operation = result.operation, state = operation.state;
  if (operation.operationId !== input.operationId || BigInt(operation.operationRevision) < BigInt(input.expectedOperationRevision) || !operationRecoveryMatches(operation)) return false;
  switch (result.kind) {
    case 'requested': return state.status === 'cancellation_requested';
    case 'cancelled_before_commit': return state.status === 'completed' && state.outcome.kind === 'cancelled_before_commit';
    case 'too_late': return state.status === 'running' || state.status === 'completed' && ['changed', 'no_change'].includes(state.outcome.kind);
    case 'already_terminal': return state.status === 'completed';
    case 'recovery_required': return state.status === 'recovery_required';
  }
}

/** Check only relationships derivable from the request and captured DTOs. */
export function preparationMatches(intent: DeepReadonly<MutationIntent>, semantics: DeepReadonly<PlanSemantics>): boolean {
  const capture = semantics.capture;
  if (semantics.action !== intent.kind || capture.kind !== intent.kind) return false;
  if (intent.kind === 'launch_ordinary' && capture.kind === 'launch_ordinary'
    || intent.kind === 'launch_isolated' && capture.kind === 'launch_isolated') {
    const requestedChoice = intent.input.unrecognizedRuntimeChoice ?? 'reject';
    return installationMatches(intent.input.target.installation, capture.target.installation)
      && profileMatches(intent.input.target.profile, capture.target.profile)
      && requestedChoice === (capture.unrecognizedRuntimeChoice ?? 'reject')
      && (capture.runtime.kind !== 'unrecognized' || capture.runtime.choice === requestedChoice)
      && (intent.kind !== 'launch_isolated' || capture.kind !== 'launch_isolated' || intent.input.storeMode === capture.storeMode);
  }
  if (intent.kind === 'focus_session' && capture.kind === 'focus_session') return equalDto(intent.input.session, capture.session);
  if (intent.kind === 'create_profile' && capture.kind === 'create_profile') {
    return intent.input.name === capture.input.name && equalDto(intent.input.setup, capture.input.setup)
      && intent.input.expectedCatalogRevision === capture.input.catalogRevision
      && installationMatches(intent.input.preferredInstallation, capture.input.preferredInstallation);
  }
  if (intent.kind === 'register_installation' && capture.kind === 'register_installation') {
    return intent.input.name === capture.input.name && intent.input.expectedCatalogRevision === capture.input.catalogRevision;
  }
  if (intent.kind === 'save_configuration' && capture.kind === 'save_configuration') return equalDto(intent.input.draft, capture.input.draft.draft);
  return 'input' in capture && equalDto(intent.input, capture.input);
}

/** Rust semantic_plan_digest serialization profile; review identity only. */
export function semanticPlanKey(input: DeepReadonly<PlanSemantics>): string {
  const semantics = captureData(input);
  const capture = semantics.capture;
  let normalizedCapture: unknown = capture;
  if (capture.kind === 'launch_ordinary' || capture.kind === 'launch_isolated') {
    const { unrecognizedRuntimeChoice, ...remaining } = capture;
    normalizedCapture = unrecognizedRuntimeChoice === undefined || unrecognizedRuntimeChoice === 'reject' ? remaining : capture;
  } else if (capture.kind === 'restore_configuration') {
    const { createdAt: _descriptiveTime, ...backup } = capture.input.backup;
    normalizedCapture = { ...capture, input: { ...capture.input, backup } };
  }
  const effects = [...semantics.effects].sort((left, right) => {
    const a = JSON.stringify(left), b = JSON.stringify(right);
    return a < b ? -1 : a > b ? 1 : 0;
  });
  return canonicalData(omitNone({ ...semantics, capture: normalizedCapture, effects }));
}

/** Uses browser WebCrypto; unavailable/failed hashing remains a local fault. */
export async function semanticPlanDigest(semantics: DeepReadonly<PlanSemantics>): Promise<string> {
  const bytes = new TextEncoder().encode('bridge-plan-semantic-json-v1\0' + semanticPlanKey(semantics));
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return 'sha256:' + Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}
