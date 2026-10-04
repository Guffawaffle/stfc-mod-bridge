import { type DeepReadonly, type ObservationState } from '../../client';
import {bindingEquivalent} from '../../client/relations';
import { availabilityText } from '../home/presentation';
import type { ActionProjection, BackupReceiptRef, DocumentBinding, InstallationBinding, InstallationProjection, MutationIntent, OperationSnapshot, PreparedPlan, ProfileProjection, ResolvedTarget } from '../../generated/protocol';
export type ManagementSection = 'profiles' | 'installations' | 'runtime' | 'game' | 'bridge' | 'history';
export const sections: readonly {
    id: ManagementSection;
    label: string;
}[] = [{ id: 'profiles', label: 'Profiles' }, { id: 'installations', label: 'Installations' }, { id: 'runtime', label: 'Community Mod' }, { id: 'game', label: 'Game update' }, { id: 'bridge', label: 'Bridge update' }, { id: 'history', label: 'History' }];
export function profileLabel(profile: DeepReadonly<ProfileProjection>): string { return profile.kind === 'ordinary' ? 'Ordinary profile' : profile.name; }
export function profileIdentity(profile: DeepReadonly<ProfileProjection>): string { return profile.kind === 'ordinary' ? profile.reference.catalogId : profile.reference.id; }
export function observedProfiles(state: ObservationState): readonly DeepReadonly<ProfileProjection>[] { const value = state.snapshot?.profiles; return value?.status === 'observed' ? value.value.items : []; }
export function observedInstallations(state: ObservationState): readonly DeepReadonly<InstallationProjection>[] { const value = state.snapshot?.installations; return value?.status === 'observed' ? value.value.items : []; }
export function inventoryStatus(observation: {
    readonly status: string;
    readonly value?: {
        readonly completeness: string;
    };
} | undefined): string { return !observation ? 'Not yet observed.' : observation.status === 'observed' ? observation.value?.completeness === 'partial' ? 'Some entries are unavailable. Refresh before relying on a complete list.' : 'Observed inventory.' : observation.status === 'missing' ? 'The backend reports no catalog.' : observation.status === 'unknown' ? 'Catalog state is unknown.' : 'Catalog is unavailable.'; }
export function available(projection: DeepReadonly<ActionProjection> | undefined): boolean { return projection?.availability.status === 'available'; }
export function availabilityReason(projection: DeepReadonly<ActionProjection> | undefined): string {
    if (!projection)
        return 'Action availability has not been checked.';
    return projection.availability.status === 'available' ? 'Available for review.' : availabilityText(projection.availability);
}
const titles: Record<MutationIntent['kind'], string> = { launch_ordinary: 'Launch ordinary game', launch_isolated: 'Launch isolated game', focus_session: 'Focus captured session', create_profile: 'Create profile', edit_ordinary_profile: 'Change ordinary profile preference', edit_isolated_profile: 'Edit profile', archive_profile: 'Archive profile', restore_profile: 'Restore archived profile', delete_profile: 'Delete entire owned profile', register_installation: 'Register installation', edit_installation: 'Rename installation', save_configuration: 'Save configuration', restore_configuration: 'Restore configuration backup', runtime_install: 'Install Community Mod', runtime_update: 'Update Community Mod', runtime_repair: 'Repair Community Mod', runtime_adopt: 'Adopt exact recognized runtime', runtime_remove: 'Remove managed Community Mod', runtime_stop_managing: 'Stop managing Community Mod', runtime_switch_source: 'Switch runtime and configuration source', game_update: 'Update game client', recover_game_update: 'Recover recorded game update', bridge_update: 'Update Bridge', recover_bridge_update: 'Recover recorded Bridge update', export_diagnostics: 'Export reviewed diagnostics', save_application_preferences: 'Save application preferences' };
export function actionTitle(kind: MutationIntent['kind']): string { return titles[kind]; }
export interface ReviewSummary {
    title: string;
    lines: readonly string[];
    warning?: string;
}
function installationLines(installation: DeepReadonly<InstallationBinding>): string[] { return [installation.kind === 'registered' ? `Installation: ${installation.registrationId} · revision ${installation.registrationRevision}` : 'Explicit directory installation', `Physical installation: ${installation.physicalId}`]; }
function targetLines(target: DeepReadonly<ResolvedTarget>): string[] { return [...installationLines(target.installation), target.profile.kind === 'isolated' ? `Profile: ${target.profile.id} · revision ${target.profile.revision}` : target.profile.ordinaryId ? `Ordinary profile: ${target.profile.ordinaryId}` : 'Owner-scoped ordinary profile']; }
/** Root's shared ActionReview consumes this typed allowlist. No paths, secret
 * refs, runtime authority tokens or arbitrary input JSON are printed. */
export function captureSummary(plan: Pick<DeepReadonly<PreparedPlan>, 'semantics'>): ReviewSummary {
    const capture = plan.semantics.capture;
    const lines: string[] = [];
    switch (capture.kind) {
        case 'create_profile':
            lines.push(`Name: ${capture.input.name}`, `Setup: ${capture.input.setup.kind === 'new' ? 'New isolated store' : 'Reviewed native import'}`, `Preferred installation: ${capture.input.preferredInstallation.registrationId}`, `Catalog revision: ${capture.input.catalogRevision}`);
            if (capture.input.setup.kind === 'windows_user_import')
                lines.push(`Import source: ${capture.input.setup.source.sourceId} · revision ${capture.input.setup.source.sourceRevision}`, `Approval choice: ${capture.input.setup.approval.replaceAll('_', ' ')}`);
            break;
        case 'edit_ordinary_profile':
            lines.push(`Ordinary profile: ${capture.input.profile.catalogId}`, `Preference: ${capture.input.preferredInstallation.kind}`);
            break;
        case 'edit_isolated_profile':
            lines.push(`Profile: ${capture.input.profile.id}`, `Preference: ${capture.input.preferredInstallation.kind}`);
            if (capture.input.name)
                lines.push(`Name: ${capture.input.name}`);
            break;
        case 'archive_profile':
        case 'restore_profile':
        case 'delete_profile':
            lines.push(`Profile: ${capture.input.profile.id}`, `Captured revision: ${capture.input.profile.revision}`);
            break;
        case 'register_installation':
            lines.push(`Name: ${capture.input.name}`, `Physical installation: ${capture.input.physicalId}`, `Catalog revision: ${capture.input.catalogRevision}`);
            break;
        case 'edit_installation':
            lines.push(`Name: ${capture.input.name}`, ...installationLines(capture.input.installation));
            break;
        case 'save_configuration':
            lines.push(...targetLines(capture.input.draft.draft.document.target), `Document: ${capture.input.draft.draft.document.documentId}`, `${capture.input.draft.edits.length} configuration edits`, ...capture.input.draft.apply.map(value => `Apply: ${value.replaceAll('_', ' ')}`));
            break;
        case 'restore_configuration':
            lines.push(...targetLines(capture.input.document.target), `Document: ${capture.input.document.documentId}`, `Backup: ${capture.input.backup.backupId}`, `Backup created: ${capture.input.backup.createdAt}`, 'The current document will receive a recoverable backup before replacement.');
            break;
        case 'runtime_install':
        case 'runtime_update':
        case 'runtime_repair':
            lines.push(...targetLines(capture.input.target), `Provider: ${capture.input.selectedRelease.providerId}`, `Distribution: ${capture.input.selectedRelease.distributionId}`, `Release: ${capture.input.selectedRelease.releaseVersion}`, `Channel: ${capture.input.selectedRelease.channelId}`, `Configuration: ${capture.preparedConfiguration.kind}`);
            break;
        case 'runtime_switch_source':
            lines.push(...targetLines(capture.input.current.target), `Current provider: ${capture.input.current.binding.providerId}`, `Selected provider: ${capture.input.selectedRelease.providerId}`, `Selected distribution: ${capture.input.selectedRelease.distributionId}`, `Release: ${capture.input.selectedRelease.releaseVersion}`, `Configuration: ${capture.preparedConfiguration.kind}`);
            break;
        case 'runtime_adopt':
            lines.push(...targetLines(capture.input.target), `Provider: ${capture.input.recognizedBinding.providerId}`, `Artifact: ${capture.input.observedArtifactDigest}`, 'Only the exact recognized artifact is adopted.');
            break;
        case 'runtime_remove':
            lines.push(...targetLines(capture.input.reference.target), `Provider: ${capture.input.reference.binding.providerId}`, 'Managed runtime is removed through the captured ownership transaction.');
            break;
        case 'runtime_stop_managing':
            lines.push(...targetLines(capture.input.reference.target), `Provider: ${capture.input.reference.binding.providerId}`, 'Runtime ownership is released; reviewed runtime bytes remain installed.');
            break;
        case 'game_update':
            lines.push(...installationLines(capture.input.checkedUpdate.installation), `Current client: ${capture.input.checkedUpdate.currentClient.version}`, `Offered client: ${capture.input.checkedUpdate.offeredClient.version}`);
            break;
        case 'recover_game_update':
            lines.push(...installationLines(capture.input.recovery.installation), 'Only the recorded native game-update transaction is recovered.');
            break;
        case 'bridge_update':
            lines.push(`Application: ${capture.input.selectedRelease.current.applicationId}`, `Current package: ${capture.input.selectedRelease.current.packageDigest}`, `Offered package: ${capture.input.selectedRelease.offered.packageDigest}`, `Offered release: ${capture.input.selectedRelease.releaseVersion}`, `Channel: ${capture.input.selectedRelease.offered.channelId}`);
            break;
        case 'recover_bridge_update':
            lines.push(`Application: ${capture.input.recovery.application.applicationId}`, 'Only the recorded Bridge package transaction is recovered.');
            break;
        case 'export_diagnostics':
            lines.push(...targetLines(capture.input.preview.target), capture.input.preview.disclosure === 'include_paths' ? 'Reviewed export includes private paths.' : 'Reviewed export excludes private paths.', 'Account contents and protected values are excluded.');
            break;
        case 'save_application_preferences':
            lines.push(`Theme: ${capture.input.values.theme}`, `Motion: ${capture.input.values.motion}`, 'Application preferences are separate from runtime TOML.');
            break;
        default: lines.push(...targetLines(capture.target), ...(capture.kind === 'focus_session' ? [`Session: ${capture.session.sessionId} · PID ${capture.session.process.pid}`, `Process creation: ${capture.session.process.startIdentity.value}`] : []));
    }
    if (capture.kind === 'edit_ordinary_profile' || capture.kind === 'edit_isolated_profile') {
        const preference = capture.input.preferredInstallation;
        if (preference.kind === 'set')
            lines.push(`Preferred installation: ${preference.installation.registrationId} · revision ${preference.installation.registrationRevision}`);
        else if (preference.kind === 'keep')
            lines.push(preference.expected.kind === 'registered' ? `Keep expected installation: ${preference.expected.id}` : 'Keep expected empty preference');
    }
    return { title: actionTitle(capture.kind), lines, ...(capture.kind === 'delete_profile' ? { warning: 'Deletes the entire owned profile after explicit confirmation. This includes its owned data.' } : capture.kind === 'runtime_switch_source' ? { warning: 'Changes runtime source and the reviewed configuration participant together.' } : {}) };
}
export function operationStatus(operation: DeepReadonly<OperationSnapshot>): string { const state = operation.state; if (state.status === 'completed') {
    switch (state.outcome.kind) {
        case 'changed': return 'Completed with changes';
        case 'no_change': return 'Already satisfied';
        case 'cancelled_before_commit': return 'Cancelled before commit';
        case 'rolled_back': return 'Rolled back';
        case 'failed': return 'Failed; review recovery choices';
    }
} return state.status === 'recovery_required' ? 'Recovery required' : state.status === 'cancellation_requested' ? 'Cancellation requested' : state.status === 'running' ? 'Running' : 'Admitted'; }
export function recoveryLabel(operation: DeepReadonly<OperationSnapshot>): string { if (operation.state.status !== 'recovery_required')
    return ''; const kind = operation.state.recovery.target.kind; return kind === 'game' || kind === 'bridge' ? 'The recorded updater has a modeled recovery route. Review that exact transaction.' : 'Recorded ownership is unresolved. Refresh observations; this operation has no generic recovery command.'; }
export function backupMatches(document: DeepReadonly<DocumentBinding>, backup: DeepReadonly<BackupReceiptRef>): boolean { return document.documentId === backup.document.documentId && bindingEquivalent(document.target, backup.document.target) && document.schema.providerId === backup.document.schema.providerId; }
