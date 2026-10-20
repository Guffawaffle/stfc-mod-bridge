import type { DeepReadonly, ObservationState, WorkState } from '../../client';
import type { ActionAvailability, AvailabilityReasonCode, InstallationProjection, ProfileProjection, ResolvedTarget, SessionProjection, TargetSelector } from '../../generated/protocol';

export const availabilityCopy: Readonly<Record<AvailabilityReasonCode, string>> = Object.freeze({
  missing_target: 'Choose an installation and profile.', unknown_identity: 'The target identity is not confirmed.',
  wrong_profile_kind: 'This action needs a different profile mode.', archived_profile: 'This profile is archived.',
  wrong_owner: 'The current user does not own this target.', active_session: 'An active session affects this action.',
  busy: 'Another operation holds this resource.', stale_revision: 'The target changed. Refresh before reviewing.',
  unrecognized_runtime: 'The installed runtime is not recognized.', unsupported_schema: 'The configuration schema is unsupported.',
  unavailable_native_route: 'This action is currently unavailable.', unqualified_isolation: 'Isolation is not available for this target.',
  offline: 'This action needs a connection.', interrupted_transaction: 'An interrupted operation needs recovery.',
  dirty_draft: 'Review the unsaved changes first.', unsupported_platform: 'This action is unsupported on this platform.',
});
export function availabilityText(value?: DeepReadonly<ActionAvailability>): string {
  if (!value) return 'Availability has not been checked.';
  if (value.status === 'available') return 'Available for review. Confirmation is still required.';
  const reasons = value.status === 'blocked' ? value.reasons : [value.reason];
  return (value.status === 'blocked' ? 'Blocked. ' : value.status === 'unknown' ? 'Unknown. ' : 'Unavailable. ')
    + (reasons.map(reason => availabilityCopy[reason.code]).join(' ') || 'Refresh for the current reason.');
}
export function inventoryText(value: { readonly status: string; readonly reason?: string; readonly value?: { readonly completeness: string; readonly items: readonly unknown[] } } | undefined, name: string): string {
  if (!value) return `${name} have not been observed.`;
  if (value.status === 'observed') return value.value?.completeness === 'partial' ? `Some ${name.toLowerCase()} are unavailable. This list is incomplete.`
    : value.value?.items.length ? `${name} observed.` : `No ${name.toLowerCase()} were reported.`;
  return value.status === 'missing' ? `No ${name.toLowerCase()} observation was reported.`
    : `${name} ${value.status === 'unknown' ? 'are unknown' : 'are unavailable'}. ${observationReason(value.reason)}`;
}
export function observationReason(reason?: string): string {
  switch (reason) {
    case 'access_denied': return 'Access was denied.';
    case 'incomplete_inventory': return 'The observation is incomplete.';
    case 'unrecognized_identity': return 'The identity is not recognized.';
    case 'conflicting_evidence': return 'The observations conflict.';
    case 'unsupported_platform': return 'This platform is unsupported.';
    default: return 'Refresh when Bridge is available.';
  }
}
export function installations(observations: ObservationState): readonly DeepReadonly<InstallationProjection>[] {
  const inventory = observations.snapshot?.installations; return inventory?.status === 'observed' ? inventory.value.items : [];
}
export function profiles(observations: ObservationState): readonly DeepReadonly<ProfileProjection>[] {
  const inventory = observations.snapshot?.profiles; return inventory?.status === 'observed' ? inventory.value.items : [];
}
export function sessions(observations: ObservationState): readonly DeepReadonly<SessionProjection>[] {
  const inventory = observations.snapshot?.sessions; return inventory?.status === 'observed' ? inventory.value.items : [];
}
/** Public catalog identity, never the private native directory reference. */
export function installationLabel(installation: DeepReadonly<InstallationProjection>): string {
  return `${installation.name} · ${installation.binding.kind === 'registered' ? installation.binding.registrationId : installation.binding.physicalId}`;
}
export const profileKey = (profile: DeepReadonly<ProfileProjection>): string => profile.kind === 'ordinary' ? 'ordinary' : profile.reference.id;
export function profileLabel(profile: DeepReadonly<ProfileProjection>, index = 0): string {
  return profile.kind === 'ordinary' ? 'Ordinary · current user' : `${profile.name} · isolated ${index + 1}${profile.reference.state === 'archived' ? ' · archived' : ''}`;
}
export function selectorFor(installationId: string, profileId: string, observations: ObservationState): DeepReadonly<TargetSelector> | undefined {
  const installation = installations(observations).find(row => row.binding.kind === 'registered' && row.binding.registrationId === installationId);
  const profile = profiles(observations).find(row => profileKey(row) === profileId);
  if (!installation || installation.binding.kind !== 'registered' || !profile) return undefined;
  return Object.freeze({ installation: Object.freeze({ kind: 'registered', id: installation.binding.registrationId, revisionAssertion: installation.binding.registrationRevision }),
    profile: profile.kind === 'ordinary' ? Object.freeze({ kind: 'ordinary' }) : Object.freeze({ kind: 'isolated', id: profile.reference.id, revisionAssertion: profile.reference.revision }) });
}
/** Request-derivable identity assertions only; the native owner resolves directories. */
export function targetMatches(selector: DeepReadonly<TargetSelector>, target: DeepReadonly<ResolvedTarget>): boolean {
  if (selector.installation.kind === 'registered' && (target.installation.kind !== 'registered' || selector.installation.id !== target.installation.registrationId
    || selector.installation.revisionAssertion != null && selector.installation.revisionAssertion !== target.installation.registrationRevision)) return false;
  if (selector.profile.kind === 'ordinary') return target.profile.kind === 'ordinary' && (selector.profile.catalogIdAssertion == null || selector.profile.catalogIdAssertion === target.profile.ordinaryId);
  return target.profile.kind === 'isolated' && selector.profile.id === target.profile.id
    && (selector.profile.revisionAssertion == null || selector.profile.revisionAssertion === target.profile.revision);
}
export function targetLabels(target: DeepReadonly<ResolvedTarget> | undefined, selector: WorkState['selector'], observations: ObservationState): { installation: string; profile: string } {
  const installation = installations(observations).find(row => target ? row.binding.physicalId === target.installation.physicalId
    && (target.installation.kind !== 'registered' || row.binding.kind === 'registered' && row.binding.registrationId === target.installation.registrationId)
    : selector?.installation.kind === 'registered' && row.binding.kind === 'registered' && row.binding.registrationId === selector.installation.id);
  const profile = profiles(observations).find(row => target ? target.profile.kind === row.kind && (target.profile.kind === 'ordinary' || row.kind === 'isolated' && target.profile.id === row.reference.id)
    : selector?.profile.kind === row.kind && (selector.profile.kind === 'ordinary' || row.kind === 'isolated' && selector.profile.id === row.reference.id));
  const installationId = target?.installation.kind === 'registered' ? target.installation.registrationId
    : target?.installation.physicalId ?? (selector?.installation.kind === 'registered' ? selector.installation.id : undefined);
  const profileId = target?.profile.kind === 'isolated' ? target.profile.id : selector?.profile.kind === 'isolated' ? selector.profile.id : undefined;
  return { installation: installation ? `${installation.name} · ${installationId}` : installationId ? `Installation ${installationId} · observation unavailable`
      : selector || target ? 'Explicit directory installation · observation unavailable' : 'Not selected',
    profile: profile ? `${profileLabel(profile, profiles(observations).indexOf(profile))}${profileId ? ` · ${profileId}` : ''}`
      : selector?.profile.kind === 'ordinary' || target?.profile.kind === 'ordinary' ? 'Ordinary · current user'
      : profileId ? `Isolated profile ${profileId} · observation unavailable` : 'Not selected' };
}
export function sessionStatus(session: DeepReadonly<SessionProjection>): string {
  const live = session.liveIdentity;
  const identity = live.status === 'observed' ? live.value ? 'Live identity matched' : 'Live identity not matched'
    : live.status === 'unknown' ? 'Live identity unknown' : live.status === 'unavailable' ? 'Live identity unavailable' : 'Live identity not observed';
  const readiness = session.readiness;
  const status = readiness.status !== 'observed' ? 'Readiness not confirmed' : readiness.value === 'isolated_ready' ? 'Isolation ready'
    : readiness.value === 'isolated_initializing' ? 'Isolation initializing' : readiness.value === 'isolation_failed' ? 'Isolation failed' : 'Ordinary session spawned';
  return `${identity}. ${status}.`;
}
