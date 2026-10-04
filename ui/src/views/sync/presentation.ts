import { canonicalData, type DeepReadonly, type WorkState } from '../../client';
import type { ConfigurationEdit, DocumentSnapshot, InheritedBoolean, ProxyChoice, SyncDestination, SyncFeed, SyncTypeDefinition } from '../../generated/protocol';
import { documentMatches } from '../settings/presentation';

export interface DestinationRow { readonly destination: DeepReadonly<SyncDestination>; readonly added: boolean; readonly removed: boolean; readonly definition?: DeepReadonly<SyncTypeDefinition>; }
export function destinationRows(work: WorkState, document?: DeepReadonly<DocumentSnapshot>): readonly DestinationRow[] {
  if (!work.draft) return [];
  const baseline = documentMatches(work, document) ? document!.sync : [];
  const added = work.edits.filter((edit): edit is DeepReadonly<Extract<ConfigurationEdit, { kind: 'add_sync_destination' }>> => edit.kind === 'add_sync_destination');
  return [...baseline.map(destination => ({ destination, added: false })), ...added.map(edit => {
    const definition = work.draft!.schema.sync.find(type => type.mode === edit.destination.mode);
    return { added: true, destination: { id: edit.destination.id, mode: edit.destination.mode, exposure: definition?.exposure ?? 'hidden', endpoint: edit.destination.endpoint,
      secretConfigured: true, desiredProxy: edit.destination.proxy, feeds: edit.destination.feeds.map(feed => ({ feedId: feed.feedId, desired: feed.desired,
        resolved: { status: 'unavailable', reason: 'native_unavailable' } })) as DeepReadonly<SyncFeed[]>, resolvedProxy: { status: 'unavailable', reason: 'native_unavailable' } } as DeepReadonly<SyncDestination> };
  })].map(row => ({ ...row, removed: work.edits.some(edit => edit.kind === 'remove_sync_destination' && edit.destinationId === row.destination.id),
    definition: work.draft!.schema.sync.find(type => type.mode === row.destination.mode) }));
}
export function desiredFeed(work: WorkState, row: DestinationRow, feedId: string): InheritedBoolean {
  const changes = work.edits.filter(edit => edit.kind === 'set_sync_feed' && edit.destinationId === row.destination.id && edit.feedId === feedId);
  const change = changes.at(-1); return change?.kind === 'set_sync_feed' ? change.value : row.destination.feeds.find(feed => feed.feedId === feedId)?.desired ?? 'inherit';
}
export function desiredProxy(work: WorkState, row: DestinationRow): DeepReadonly<ProxyChoice> {
  const changes = work.edits.filter(edit => edit.kind === 'set_sync_proxy' && edit.destinationId === row.destination.id);
  const change = changes.at(-1); return change?.kind === 'set_sync_proxy' ? change.value : row.destination.desiredProxy;
}
export function resolvedFeed(row: DestinationRow, feedId: string): string {
  const observation = row.destination.feeds.find(feed => feed.feedId === feedId)?.resolved;
  return observation?.status === 'observed' ? observation.value ? 'Observed enabled' : 'Observed disabled' : observation?.status === 'unknown' ? 'Effective state unknown' : 'Effective state unavailable';
}
export function syncCaptureKey(work: WorkState): string { return canonicalData([work.draft?.draft ?? null, work.draft?.schema ?? null]); }
