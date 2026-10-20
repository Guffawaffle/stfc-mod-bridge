import { canonicalData, type DeepReadonly } from '../../client';
import type { ConfigurationEdit, InheritedBoolean, ProxyChoice, SyncMode } from '../../generated/protocol';
import { SettingsController } from '../settings/controller';
import { destinationRows, type DestinationRow } from './presentation';

/** Typed Data Sync edits share the Settings draft and exact full-set staging path. */
export class SyncController {
  constructor(readonly settings: SettingsController, private readonly destinationId: () => string = () => `destination.${crypto.randomUUID()}`) {}
  get facade() { return this.settings.facade; }
  private row(id: string): DestinationRow | undefined { const rows = destinationRows(this.facade.work.state, this.settings.state.document).filter(row => row.destination.id === id); return rows.length === 1 ? rows[0] : undefined; }
  private replaceScoped(test: (edit: DeepReadonly<ConfigurationEdit>) => boolean, edit?: DeepReadonly<ConfigurationEdit>): boolean {
    return this.settings.stageAll([...this.facade.work.state.edits.filter(value => !test(value)), ...(edit ? [edit] : [])]);
  }
  setFeed(id: string, feedId: string, value: InheritedBoolean): boolean {
    const row = this.row(id); if (!row?.definition || row.removed || row.definition.exposure === 'hidden' || !row.definition.feeds.includes(feedId) || !['inherit', 'on', 'off'].includes(value)) return false;
    return this.replaceScoped(edit => edit.kind === 'set_sync_feed' && edit.destinationId === id && edit.feedId === feedId, { kind: 'set_sync_feed', destinationId: id, feedId, value });
  }
  setProxy(id: string, value: DeepReadonly<ProxyChoice>): boolean {
    const row = this.row(id); if (!row?.definition || row.removed || row.definition.exposure === 'hidden' || value.kind === 'global' && !row.definition.inheritsGlobalProxy) return false;
    if (value.kind === 'custom') {
      const fieldId = row.definition.proxyFieldId, draft = this.facade.work.state.draft;
      if (!fieldId || value.reference.fieldId !== fieldId || !draft || canonicalData(value.reference.capturedFor) !== canonicalData(draft.draft)) return false;
    }
    // Move a captured proxy into its destination participant; never alias one protected handle across edits.
    return this.replaceScoped(edit => edit.kind === 'set_sync_proxy' && edit.destinationId === id
      || value.kind === 'custom' && edit.kind === 'set_private' && edit.fieldId === row.definition!.proxyFieldId && canonicalData(edit.reference) === canonicalData(value.reference),
      { kind: 'set_sync_proxy', destinationId: id, value });
  }
  remove(id: string): boolean {
    const row = this.row(id); if (!row || row.removed || row.definition?.exposure === 'hidden') return false;
    return this.replaceScoped(edit => edit.kind === 'add_sync_destination' ? edit.destination.id === id : 'destinationId' in edit && edit.destinationId === id,
      row.added ? undefined : { kind: 'remove_sync_destination', destinationId: id });
  }
  undoRemoval(id: string): boolean { return this.replaceScoped(edit => edit.kind === 'remove_sync_destination' && edit.destinationId === id); }
  create(mode: SyncMode): boolean {
    const work = this.facade.work.state, draft = work.draft;
    const definitions = draft?.schema.sync.filter(type => type.mode === mode) ?? [];
    if (!draft || definitions.length !== 1 || definitions[0].exposure !== 'creatable') return false;
    const definition = definitions[0];
    const endpoint = work.edits.filter(edit => edit.kind === 'set_private' && edit.fieldId === definition.endpointFieldId);
    const secret = work.edits.filter(edit => edit.kind === 'replace_secret' && edit.fieldId === definition.secretFieldId);
    if (endpoint.length !== 1 || secret.length !== 1 || endpoint[0].kind !== 'set_private' || secret[0].kind !== 'replace_secret'
      || canonicalData(endpoint[0].reference.capturedFor) !== canonicalData(draft.draft) || canonicalData(secret[0].reference.draft) !== canonicalData(draft.draft)) return false;
    const id = this.destinationId();
    if (destinationRows(work, this.settings.state.document).some(row => row.destination.id === id)) return false;
    return this.settings.stageAll([...work.edits.filter(edit => !('fieldId' in edit) || ![definition.endpointFieldId, definition.secretFieldId].includes(edit.fieldId)),
      { kind: 'add_sync_destination', destination: { id, mode, endpoint: endpoint[0].reference, secret: secret[0].reference,
        feeds: definition.feeds.map(feedId => ({ feedId, desired: 'inherit' })), proxy: definition.inheritsGlobalProxy ? { kind: 'global' } : { kind: 'none' } } }]);
  }
  capturedProxy(mode: SyncMode): DeepReadonly<ProxyChoice> | undefined {
    const definition = this.facade.work.state.draft?.schema.sync.find(type => type.mode === mode), draft = this.facade.work.state.draft;
    const edit = this.facade.work.state.edits.find(value => value.kind === 'set_private' && value.fieldId === definition?.proxyFieldId);
    return edit?.kind === 'set_private' && draft && canonicalData(edit.reference.capturedFor) === canonicalData(draft.draft) ? { kind: 'custom', reference: edit.reference } : undefined;
  }
}
