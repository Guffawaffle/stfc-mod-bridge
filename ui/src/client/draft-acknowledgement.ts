import type { ConfigurationEdit, DraftRef, PrivateValueRef, SecretRef, SetDraftChangesInput, SetDraftChangesResult } from '../generated/protocol';
import { canonicalData, captureData, type DeepReadonly } from './wire';
import { counter } from './observation';
import { bindingEquivalent, protectedReferenceKey } from './relations';

function editKey(edit: DeepReadonly<ConfigurationEdit>): string {
  const proxy = (value: DeepReadonly<Extract<ConfigurationEdit, { kind: 'set_sync_proxy' }>['value']>) =>
    value.kind === 'custom' ? { ...value, reference: protectedReferenceKey(value.reference) } : value;
  if (edit.kind === 'set_private' || edit.kind === 'replace_secret') return canonicalData({ ...edit, reference: protectedReferenceKey(edit.reference) });
  if (edit.kind === 'set_sync_proxy') return canonicalData({ ...edit, value: proxy(edit.value) });
  if (edit.kind === 'add_sync_destination') return canonicalData({ ...edit, destination: { ...edit.destination,
    endpoint: protectedReferenceKey(edit.destination.endpoint), secret: protectedReferenceKey(edit.destination.secret), proxy: proxy(edit.destination.proxy) } });
  return canonicalData(edit);
}
const editsEqual = (left: readonly DeepReadonly<ConfigurationEdit>[], right: readonly DeepReadonly<ConfigurationEdit>[]) =>
  left.length === right.length && left.every((edit, index) => editKey(edit) === editKey(right[index]));

/** Verify the closed reference substitution; a wire reference proves no native value. */
export function draftAcknowledgementMatches(input: DeepReadonly<SetDraftChangesInput>, result: DeepReadonly<SetDraftChangesResult>): boolean {
  try {
    if (!bindingEquivalent(input.draft, result.accepted.draft) || !editsEqual(input.edits, result.accepted.edits)) return false;
    const old = input.draft, next = result.snapshot.draft;
    if (old.draftId !== next.draftId || old.hostEpoch !== next.hostEpoch
      || !bindingEquivalent(old.document, next.document)
      || counter(next.revision) !== counter(old.revision) + 1n) return false;
    const transfers = result.protectedTransfers;
    if (transfers.length > 256) return false;
    const sources = new Set<string>(), destinations = new Set<string>(), used = new Set<number>();
    const privateBound = (ref: DeepReadonly<PrivateValueRef>, draft: DeepReadonly<DraftRef>) =>
      bindingEquivalent(ref.document, draft.document) && ref.capturedFor != null && bindingEquivalent(ref.capturedFor, draft);
    for (const transfer of transfers) {
      const { from, to } = transfer;
      if (from.fieldId !== to.fieldId) return false;
      if (transfer.kind === 'private') {
        if (!privateBound(transfer.from, old) || !privateBound(transfer.to, next)
          || transfer.from.revision !== transfer.to.revision || transfer.from.valueId === transfer.to.valueId) return false;
      } else if (!bindingEquivalent(transfer.from.draft, old) || !bindingEquivalent(transfer.to.draft, next) || transfer.from.secretId === transfer.to.secretId) return false;
      const source = `${transfer.kind}:${transfer.kind === 'private' ? transfer.from.valueId : transfer.from.secretId}`;
      const destination = `${transfer.kind}:${transfer.kind === 'private' ? transfer.to.valueId : transfer.to.secretId}`;
      if (sources.has(source) || destinations.has(destination)) return false;
      sources.add(source); destinations.add(destination);
    }
    const privateRef = (ref: DeepReadonly<PrivateValueRef>): PrivateValueRef => {
      if (!bindingEquivalent(ref.document, old.document)) throw new Error('Foreign saved reference');
      if (destinations.has(`private:${ref.valueId}`)) throw new Error('Aliased reference');
      if (ref.capturedFor == null) return JSON.parse(JSON.stringify(ref));
      const index = transfers.findIndex(value => value.kind === 'private' && bindingEquivalent(value.from, ref));
      const transfer = transfers[index];
      if (index < 0 || transfer.kind !== 'private') throw new Error('Missing private transfer');
      used.add(index); return JSON.parse(JSON.stringify(transfer.to));
    };
    const secretRef = (ref: DeepReadonly<SecretRef>): SecretRef => {
      if (destinations.has(`secret:${ref.secretId}`)) throw new Error('Aliased reference');
      const index = transfers.findIndex(value => value.kind === 'secret' && bindingEquivalent(value.from, ref));
      const transfer = transfers[index];
      if (index < 0 || transfer.kind !== 'secret') throw new Error('Missing secret transfer');
      used.add(index); return JSON.parse(JSON.stringify(transfer.to));
    };
    const edits: ConfigurationEdit[] = JSON.parse(JSON.stringify(captureData(input.edits)));
    for (const edit of edits) {
      if (edit.kind === 'set_private') {
        if (edit.fieldId !== edit.reference.fieldId) return false;
        edit.reference = privateRef(edit.reference);
      } else if (edit.kind === 'replace_secret') {
        if (edit.fieldId !== edit.reference.fieldId) return false;
        edit.reference = secretRef(edit.reference);
      }
      else if (edit.kind === 'set_sync_proxy' && edit.value.kind === 'custom') edit.value.reference = privateRef(edit.value.reference);
      else if (edit.kind === 'add_sync_destination') {
        edit.destination.endpoint = privateRef(edit.destination.endpoint);
        edit.destination.secret = secretRef(edit.destination.secret);
        if (edit.destination.proxy.kind === 'custom') edit.destination.proxy.reference = privateRef(edit.destination.proxy.reference);
      }
    }
    return used.size === transfers.length && editsEqual(edits, result.snapshot.edits);
  } catch { return false; }
}
