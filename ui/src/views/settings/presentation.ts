import { canonicalData, type DeepReadonly } from '../../client';
import { bindingEquivalent } from '../../client/relations';
import type { WorkState } from '../../client/work-context';
import type { ApplyTiming, ConfigurationEdit, DocumentSnapshot, FieldDefinition, FieldState, PublicConfigValue, SupportedPlatform } from '../../generated/protocol';

export const label = (value: string): string => value.split(/[._-]+/).filter(Boolean).map(part => part[0]?.toUpperCase() + part.slice(1)).join(' ');
export const fieldLabel = (field: DeepReadonly<FieldDefinition>): string => label(field.path.join('.')) || label(field.fieldId);
export const applyLabel = (value: ApplyTiming): string => ({ immediate: 'Applies immediately', next_launch: 'Applies on the next launch', restart_required: 'Requires a game restart' })[value];
export const violationLabel = (code: string): string => ({ unknown_field: 'This field is no longer in the schema.', invalid_type: 'Choose the required value type.',
  constraint_violation: 'This value is outside the schema constraints.', private_value_required: 'Capture a protected private value.', secret_reference_required: 'Capture a protected secret.',
  unsupported_sync_field: 'This Data Sync field is unavailable for this mode.', stale_schema: 'The configuration schema changed. Changes are retained.' })[code as 'unknown_field'] ?? 'The backend could not validate this field.';

/** One replacement preserves every unrelated field and Data Sync change. */
export function replaceField(edits: readonly DeepReadonly<ConfigurationEdit>[], fieldId: string, edit?: DeepReadonly<ConfigurationEdit>): readonly DeepReadonly<ConfigurationEdit>[] {
  return Object.freeze([...edits.filter(value => !('fieldId' in value) || value.fieldId !== fieldId), ...(edit ? [edit] : [])]);
}
export function documentMatches(work: WorkState, document?: DeepReadonly<DocumentSnapshot>): boolean {
  return !!work.draft && !!document && bindingEquivalent(work.draft.draft.document, document.binding)
    && canonicalData(work.draft.schema.binding) === canonicalData(document.schema.binding);
}
export function draftTargetDrift(work: WorkState): boolean { return !!work.draft && !!work.binding && !bindingEquivalent({ ...work.draft.draft.document, target: work.binding }, work.draft.draft.document); }
export function draftHostDrift(work: WorkState, observedEpoch?: string): boolean {
  return !!work.draft && (work.draftHostChanged || observedEpoch !== undefined && work.draft.draft.hostEpoch !== observedEpoch);
}
export function baselineField(work: WorkState, document: DeepReadonly<DocumentSnapshot> | undefined, fieldId: string): DeepReadonly<FieldState> | undefined {
  if (!documentMatches(work, document)) return undefined;
  const rows = document!.fields.filter(value => value.fieldId === fieldId); return rows.length === 1 ? rows[0] : undefined;
}
export interface FieldPresentation { readonly value?: DeepReadonly<PublicConfigValue>; readonly override: 'staged' | 'saved' | 'default' | 'unknown';
  readonly state: string; readonly protectedConfigured: boolean; readonly error: string; readonly supported: boolean; readonly numericText?: string; }
export function presentField(field: DeepReadonly<FieldDefinition>, work: WorkState, document?: DeepReadonly<DocumentSnapshot>, platform?: SupportedPlatform): FieldPresentation {
  const edits = work.edits.filter(edit => 'fieldId' in edit && edit.fieldId === field.fieldId);
  const edit = edits.length === 1 ? edits[0] : undefined, saved = baselineField(work, document, field.fieldId);
  const defaults = field.defaultValue ?? undefined;
  const value = edit?.kind === 'set_public' ? edit.value : edit?.kind === 'remove_override' ? defaults
    : saved?.value.kind === 'public' ? saved.value.value : saved?.value.kind === 'absent' ? defaults : documentMatches(work, document) && !saved ? defaults : undefined;
  const source = edit?.kind === 'remove_override' ? 'default' : edit ? 'staged' : saved?.overridden ? 'saved' : documentMatches(work, document) ? 'default' : 'unknown';
  const protectedConfigured = edit?.kind === 'replace_secret' || edit?.kind === 'set_private' || !edit && (saved?.value.kind === 'private' || saved?.value.kind === 'secret' && saved.value.configured);
  const violations = work.draft?.validation.filter(value => value.fieldId === field.fieldId) ?? [];
  return Object.freeze({ ...(value ? { value } : {}), override: source, protectedConfigured,
    state: source === 'staged' ? 'Override staged' : source === 'saved' ? 'Saved override' : source === 'default' ? 'Using the provider default' : 'Saved value is not observed',
    error: edits.length > 1 ? 'Conflicting staged edits require reconciliation.' : violations.map(row => violationLabel(row.code)).join(' '),
    supported: platform ? field.platforms.some(value => value === platform) : field.platforms.length === 2,
    ...(work.publicInputs?.find(row => row.binding.field.fieldId === field.fieldId) ? { numericText: work.publicInputs!.find(row => row.binding.field.fieldId === field.fieldId)!.text } : {}) });
}
export function fieldText(value?: DeepReadonly<PublicConfigValue>): string {
  return value && ['integer', 'number', 'string', 'enum'].includes(value.kind) ? String(value.value) : '';
}
export function categories(fields: readonly DeepReadonly<FieldDefinition>[]): readonly string[] { return [...new Set(fields.map(field => field.category))]; }
export function filterFields(fields: readonly DeepReadonly<FieldDefinition>[], search: string, category = ''): readonly DeepReadonly<FieldDefinition>[] {
  const terms = search.toLocaleLowerCase().trim().split(/\s+/).filter(Boolean);
  return fields.filter(field => (!category || field.category === category) && terms.every(term => [fieldLabel(field), field.fieldId, field.category, ...field.searchTerms, ...field.aliases.map(value => value.join(' '))].join(' ').toLocaleLowerCase().includes(term)));
}
export function stagedApply(work: WorkState): readonly ApplyTiming[] {
  if (!work.draft) return [];
  // The versioned DraftSnapshot contract applies structural Data Sync edits on the next launch.
  const timings = work.edits.flatMap(edit => 'fieldId' in edit
    ? work.draft!.schema.fields.filter(field => field.fieldId === edit.fieldId).map(field => field.apply)
    : ['next_launch' as const]);
  return ['immediate', 'next_launch', 'restart_required'].filter(value => timings.includes(value as ApplyTiming)) as ApplyTiming[];
}
