import { readFileSync } from 'node:fs';
import { expect, test } from 'vitest';
import { draftAcknowledgementMatches } from '../src/client/draft-acknowledgement';
import { canonicalData, decodeReply, decodeRequest } from '../src/client/wire';
import type { ConfigurationEdit, DraftRef, PrivateValueRef, ProtectedReferenceTransfer, SetDraftChangesInput, SetDraftChangesResult } from '../src/generated/protocol';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
type Pair = { input: SetDraftChangesInput; result: SetDraftChangesResult };
const raw = (name: string) => readFileSync(new URL(name + '.json', fixtures), 'utf8');

function pair(name = 'sc09-schema-supported-sync-draft'): Pair {
  const request = decodeRequest(raw(name + '-request'));
  const reply = decodeReply(raw(name + '-reply'));
  if (request.body.type !== 'command' || request.body.command.name !== 'set_draft_changes'
    || reply.body.type !== 'result' || reply.body.result.type !== 'command'
    || reply.body.result.command.name !== 'set_draft_changes') throw new Error('fixture_method');
  return { input: clone(request.body.command.input) as SetDraftChangesInput, result: clone(reply.body.result.command.output) as SetDraftChangesResult };
}

/** Mutations retain generated wire shape, so a refusal exercises relationships. */
function matching(value: Pair): boolean {
  const request = JSON.parse(raw('sc09-schema-supported-sync-draft-request'));
  request.body.command.input = value.input;
  const reply = JSON.parse(raw('sc09-schema-supported-sync-draft-reply'));
  reply.body.result.command.output = value.result;
  const input = decodeRequest(JSON.stringify(request));
  const output = decodeReply(JSON.stringify(reply));
  if (input.body.type !== 'command' || input.body.command.name !== 'set_draft_changes'
    || output.body.type !== 'result' || output.body.result.type !== 'command'
    || output.body.result.command.name !== 'set_draft_changes') throw new Error('fixture_method');
  return draftAcknowledgementMatches(input.body.command.input, output.body.result.command.output);
}

function privateTransfer(value: Pair): Extract<ProtectedReferenceTransfer, { kind: 'private' }> {
  const transfer = value.result.protectedTransfers.find(entry => entry.kind === 'private');
  if (!transfer || transfer.kind !== 'private') throw new Error('fixture_private_transfer');
  return transfer;
}
function secretTransfer(value: Pair): Extract<ProtectedReferenceTransfer, { kind: 'secret' }> {
  const transfer = value.result.protectedTransfers.find(entry => entry.kind === 'secret');
  if (!transfer || transfer.kind !== 'secret') throw new Error('fixture_secret_transfer');
  return transfer;
}
function destination(edits: ConfigurationEdit[]) {
  const edit = edits.find(entry => entry.kind === 'add_sync_destination');
  if (!edit || edit.kind !== 'add_sync_destination') throw new Error('fixture_destination');
  return edit.destination;
}

function mixed(): Pair {
  const value = pair();
  const edit = pair('sc08-stage-dirty-draft').input.edits[0];
  value.input.edits.unshift(clone(edit));
  value.result.accepted = clone(value.input);
  value.result.snapshot.edits.unshift(clone(edit));
  value.result.snapshot.apply = ['immediate', 'next_launch'];
  return value;
}

function savedPrivate(): Pair {
  const value = pair();
  delete destination(value.input.edits).endpoint.capturedFor;
  value.result.accepted = clone(value.input);
  destination(value.result.snapshot.edits).endpoint = clone(destination(value.input.edits).endpoint);
  value.result.protectedTransfers = value.result.protectedTransfers.filter(entry => entry.kind !== 'private');
  return value;
}

function secondPrivate(): Pair {
  const value = pair();
  const first = privateTransfer(value);
  const from = { ...clone(first.from), valueId: '22222222-2222-4222-8222-222222222222' };
  const to = { ...clone(first.to), valueId: '33333333-3333-4333-8333-333333333333' };
  value.input.edits.push({ kind: 'set_private', fieldId: from.fieldId, reference: from });
  value.result.accepted = clone(value.input);
  value.result.snapshot.edits.push({ kind: 'set_private', fieldId: to.fieldId, reference: to });
  value.result.protectedTransfers.push({ kind: 'private', from: clone(from), to: clone(to) });
  return value;
}

function ordinaryNone(draft: DraftRef): void {
  if (draft.document.target.profile.kind !== 'ordinary') throw new Error('fixture_profile');
  draft.document.target.profile.ordinaryId = null;
}
function documentNone(reference: PrivateValueRef): void {
  if (reference.document.target.profile.kind !== 'ordinary') throw new Error('fixture_profile');
  reference.document.target.profile.ordinaryId = null;
}

test.each(['sc08-stage-dirty-draft', 'sc09-schema-supported-sync-draft'])('accepts actual generated acknowledgement: %s', name => {
  const value = pair(name);
  const before = canonicalData(value);
  expect(matching(value)).toBe(true);
  expect(canonicalData(value)).toBe(before);
});

test('protected golden uses explicit private/secret transfers without plaintext or input mutation', () => {
  const value = pair();
  expect(value.result.protectedTransfers.map(entry => entry.kind)).toEqual(['private', 'secret']);
  expect(Object.hasOwn(privateTransfer(value).from, 'value')).toBe(false);
  expect(Object.hasOwn(secretTransfer(value).from, 'value')).toBe(false);
  const before = canonicalData(value);
  expect(matching(value)).toBe(true);
  expect(canonicalData(value)).toBe(before);
});

test('closed transfers are order independent while accepted edit order stays exact', () => {
  const value = mixed();
  value.result.protectedTransfers.reverse();
  expect(matching(value)).toBe(true);
  value.result.snapshot.edits.reverse();
  expect(matching(value)).toBe(false);
});

test('saved private document reference is preserved without transfer', () => expect(matching(savedPrivate())).toBe(true));

test('distinct private sources and fresh destinations each consume their own transfer', () => expect(matching(secondPrivate())).toBe(true));

test('a fresh private destination cannot alias a saved handle elsewhere in the accepted edits', () => {
  const value = savedPrivate();
  const saved = clone(destination(value.input.edits).endpoint);
  const from = { ...clone(privateTransfer(pair()).from), valueId: '22222222-2222-4222-8222-222222222222' };
  const to = { ...clone(from), valueId: saved.valueId, capturedFor: clone(value.result.snapshot.draft) };
  value.input.edits.push({ kind: 'set_private', fieldId: from.fieldId, reference: from });
  value.result.accepted = clone(value.input);
  value.result.snapshot.edits.push({ kind: 'set_private', fieldId: to.fieldId, reference: to });
  value.result.protectedTransfers.push({ kind: 'private', from: clone(from), to: clone(to) });
  expect(matching(value)).toBe(false);
});

test.each(['document_revision', 'installation', 'profile'])('refuses a saved private handle outside the captured document: %s', variant => {
  const value = savedPrivate();
  const reference = destination(value.input.edits).endpoint;
  if (variant === 'document_revision') reference.document.revision = 'foreign-saved-document';
  if (variant === 'installation') reference.document.target.installation.physicalId = 'foreign-saved-installation';
  if (variant === 'profile' && reference.document.target.profile.kind === 'ordinary') reference.document.target.profile.ownerScope = 'foreign-saved-owner';
  value.result.accepted = clone(value.input);
  destination(value.result.snapshot.edits).endpoint = clone(reference);
  expect(matching(value)).toBe(false);
});

test.each(['private', 'secret'])('refuses %s handle field scope inconsistent with its outer edit', kind => {
  const value = pair();
  const from = destination(value.input.edits), to = destination(value.result.snapshot.edits);
  value.input.edits = kind === 'private'
    ? [{ kind: 'set_private', fieldId: 'sync.other', reference: from.endpoint }]
    : [{ kind: 'replace_secret', fieldId: 'sync.other', reference: from.secret }];
  value.result.accepted = clone(value.input);
  value.result.snapshot.edits = kind === 'private'
    ? [{ kind: 'set_private', fieldId: 'sync.other', reference: to.endpoint }]
    : [{ kind: 'replace_secret', fieldId: 'sync.other', reference: to.secret }];
  value.result.protectedTransfers = value.result.protectedTransfers.filter(entry => entry.kind === kind);
  expect(matching(value)).toBe(false);
});

test('separate set_private and replace_secret references use the same closed transfer rules', () => {
  const value = pair();
  const original = destination(value.input.edits);
  const next = destination(value.result.snapshot.edits);
  value.input.edits = [
    { kind: 'set_private', fieldId: original.endpoint.fieldId, reference: original.endpoint },
    { kind: 'replace_secret', fieldId: original.secret.fieldId, reference: original.secret },
  ];
  value.result.accepted = clone(value.input);
  value.result.snapshot.edits = [
    { kind: 'set_private', fieldId: next.endpoint.fieldId, reference: next.endpoint },
    { kind: 'replace_secret', fieldId: next.secret.fieldId, reference: next.secret },
  ];
  expect(matching(value)).toBe(true);
});

test('one protected private handle reused in two proxy positions needs one used transfer', () => {
  const value = pair();
  const from = clone(privateTransfer(value).from), to = clone(privateTransfer(value).to);
  value.input.edits.push({ kind: 'set_sync_proxy', destinationId: 'synthetic-existing-destination', value: { kind: 'custom', reference: from } });
  value.result.accepted = clone(value.input);
  value.result.snapshot.edits.push({ kind: 'set_sync_proxy', destinationId: 'synthetic-existing-destination', value: { kind: 'custom', reference: to } });
  expect(matching(value)).toBe(true);
});

test('a custom proxy inside add_sync_destination consumes its explicit private transfer', () => {
  const value = pair();
  destination(value.input.edits).proxy = { kind: 'custom', reference: clone(privateTransfer(value).from) };
  value.result.accepted = clone(value.input);
  destination(value.result.snapshot.edits).proxy = { kind: 'custom', reference: clone(privateTransfer(value).to) };
  expect(matching(value)).toBe(true);
});

test('a saved private custom proxy is preserved without an additional transfer', () => {
  const value = pair();
  const saved = { ...clone(privateTransfer(value).from), valueId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' };
  delete saved.capturedFor;
  destination(value.input.edits).proxy = { kind: 'custom', reference: saved };
  value.result.accepted = clone(value.input);
  destination(value.result.snapshot.edits).proxy = { kind: 'custom', reference: clone(saved) };
  expect(matching(value)).toBe(true);
});

test.each(['foreign_document', 'changed_saved_id', 'captured_as_saved'])('refuses custom proxy reference drift: %s', variant => {
  const value = pair();
  const saved = { ...clone(privateTransfer(value).from), valueId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' };
  delete saved.capturedFor;
  if (variant === 'foreign_document') saved.document.revision = 'foreign-proxy-document';
  destination(value.input.edits).proxy = { kind: 'custom', reference: saved };
  value.result.accepted = clone(value.input);
  const next = clone(saved);
  if (variant === 'changed_saved_id') next.valueId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
  if (variant === 'captured_as_saved') next.capturedFor = clone(value.result.snapshot.draft);
  destination(value.result.snapshot.edits).proxy = { kind: 'custom', reference: next };
  expect(matching(value)).toBe(false);
});

test('a private successor cannot alias another source that is also transferred', () => {
  const value = secondPrivate();
  const second = value.result.protectedTransfers[2];
  if (second.kind !== 'private') throw new Error('fixture_private_transfer');
  privateTransfer(value).to.valueId = second.from.valueId;
  destination(value.result.snapshot.edits).endpoint = clone(privateTransfer(value).to);
  expect(matching(value)).toBe(false);
});

test.each(['revision', 'host', 'document', 'draft_id'])('accepted old generation stays correlated with the exact request: %s', variant => {
  const value = pair('sc08-stage-dirty-draft');
  const accepted = value.result.accepted.draft;
  if (variant === 'revision') accepted.revision = '0';
  if (variant === 'host') accepted.hostEpoch = '77777777-7777-4777-8777-777777777777';
  if (variant === 'document') accepted.document.revision = 'foreign-accepted-document';
  if (variant === 'draft_id') accepted.draftId = '88888888-8888-4888-8888-888888888888';
  expect(matching(value)).toBe(false);
});

test('successor generation above the JavaScript integer boundary uses exact decimal arithmetic', () => {
  const value = pair('sc08-stage-dirty-draft');
  value.input.draft.revision = '9007199254740992';
  value.result.accepted = clone(value.input);
  value.result.snapshot.draft.revision = '9007199254740993';
  expect(matching(value)).toBe(true);
  value.result.snapshot.draft.revision = '9007199254740992';
  expect(matching(value)).toBe(false);
});

test('the final representable successor succeeds but a saturated generation cannot wrap', () => {
  const value = pair('sc08-stage-dirty-draft');
  value.input.draft.revision = '18446744073709551614';
  value.result.accepted = clone(value.input);
  value.result.snapshot.draft.revision = '18446744073709551615';
  expect(matching(value)).toBe(true);
  value.input.draft.revision = '18446744073709551615';
  value.result.accepted = clone(value.input);
  value.result.snapshot.draft.revision = '0';
  expect(matching(value)).toBe(false);
});

test.each(['private', 'secret', 'all'])('refuses missing %s transfer', kind => {
  const value = pair();
  value.result.protectedTransfers = value.result.protectedTransfers.filter(entry => kind !== 'all' && entry.kind !== kind);
  expect(matching(value)).toBe(false);
});

test('refuses a well-scoped but unused transfer', () => {
  const value = pair();
  const unused = clone(privateTransfer(value));
  unused.from.valueId = '44444444-4444-4444-8444-444444444444';
  unused.to.valueId = '55555555-5555-4555-8555-555555555555';
  value.result.protectedTransfers.push(unused);
  expect(matching(value)).toBe(false);
});

test('refuses duplicate source handles', () => {
  const value = pair();
  const duplicate = clone(privateTransfer(value));
  duplicate.to.valueId = '66666666-6666-4666-8666-666666666666';
  value.result.protectedTransfers.push(duplicate);
  expect(matching(value)).toBe(false);
});

test('refuses duplicate destination handles even when both transfers are used', () => {
  const value = secondPrivate();
  const first = privateTransfer(value);
  const second = value.result.protectedTransfers[2];
  if (second.kind !== 'private') throw new Error('fixture_private_transfer');
  second.to.valueId = first.to.valueId;
  const edit = value.result.snapshot.edits[1];
  if (edit.kind !== 'set_private') throw new Error('fixture_private_edit');
  edit.reference = clone(second.to);
  expect(matching(value)).toBe(false);
});

test.each(['private', 'secret'])('refuses %s destination that aliases its prior accepted handle', kind => {
  const value = pair();
  if (kind === 'private') {
    const transfer = privateTransfer(value);
    transfer.to.valueId = transfer.from.valueId;
    destination(value.result.snapshot.edits).endpoint = clone(transfer.to);
  } else {
    const transfer = secretTransfer(value);
    transfer.to.secretId = transfer.from.secretId;
    destination(value.result.snapshot.edits).secret = clone(transfer.to);
  }
  expect(matching(value)).toBe(false);
});

test.each(['old_revision', 'next_revision', 'field', 'document', 'opaque_revision'])('refuses private transfer drift: %s', variant => {
  const value = pair();
  const transfer = privateTransfer(value);
  if (variant === 'old_revision') transfer.from.capturedFor!.revision = '1';
  if (variant === 'next_revision') transfer.to.capturedFor!.revision = '2';
  if (variant === 'field') transfer.to.fieldId = 'sync.other';
  if (variant === 'document') transfer.to.document.revision = 'foreign-document-revision';
  if (variant === 'opaque_revision') transfer.to.revision = 'foreign-value-revision';
  destination(value.result.snapshot.edits).endpoint = clone(transfer.to);
  expect(matching(value)).toBe(false);
});

test.each(['old_revision', 'next_revision', 'field', 'host'])('refuses secret transfer drift: %s', variant => {
  const value = pair();
  const transfer = secretTransfer(value);
  if (variant === 'old_revision') transfer.from.draft.revision = '1';
  if (variant === 'next_revision') transfer.to.draft.revision = '2';
  if (variant === 'field') transfer.to.fieldId = 'sync.other';
  if (variant === 'host') transfer.to.draft.hostEpoch = '77777777-7777-4777-8777-777777777777';
  destination(value.result.snapshot.edits).secret = clone(transfer.to);
  expect(matching(value)).toBe(false);
});

test.each(['draft', 'host', 'same_revision', 'skipped_revision', 'document', 'physical_installation', 'schema', 'profile'])('refuses successor retargeting: %s', variant => {
  const value = pair('sc08-stage-dirty-draft');
  const next = value.result.snapshot.draft;
  if (variant === 'draft') next.draftId = '88888888-8888-4888-8888-888888888888';
  if (variant === 'host') next.hostEpoch = '99999999-9999-4999-8999-999999999999';
  if (variant === 'same_revision') next.revision = '1';
  if (variant === 'skipped_revision') next.revision = '3';
  if (variant === 'document') next.document.revision = 'foreign-document-revision';
  if (variant === 'physical_installation') next.document.target.installation.physicalId = 'foreign-physical-installation';
  if (variant === 'schema') next.document.schema.digest = 'sha256:' + 'c'.repeat(64);
  if (variant === 'profile' && next.document.target.profile.kind === 'ordinary') next.document.target.profile.ownerScope = 'foreign-owner';
  expect(matching(value)).toBe(false);
});

test.each(['accepted_public', 'snapshot_public', 'destination_id', 'mode', 'feed_value', 'feed_order', 'proxy'])('preserves exact non-reference intent: %s', variant => {
  const value = mixed();
  if (variant === 'accepted_public') {
    const edit = value.result.accepted.edits[0];
    if (edit.kind === 'set_public') edit.value = { kind: 'boolean', value: false };
  }
  if (variant === 'snapshot_public') {
    const edit = value.result.snapshot.edits[0];
    if (edit.kind === 'set_public') edit.value = { kind: 'boolean', value: false };
  }
  const next = destination(value.result.snapshot.edits);
  if (variant === 'destination_id') next.id = 'foreign-destination';
  if (variant === 'mode') next.mode = 'sidecar';
  if (variant === 'feed_value') next.feeds[0].desired = 'on';
  if (variant === 'feed_order') next.feeds.reverse();
  if (variant === 'proxy') next.proxy = { kind: 'none' };
  expect(matching(value)).toBe(false);
});

test.each(['value_id', 'revision', 'document'])('refuses rewriting saved private intent: %s', variant => {
  const value = savedPrivate();
  const endpoint = destination(value.result.snapshot.edits).endpoint;
  if (variant === 'value_id') endpoint.valueId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
  if (variant === 'revision') endpoint.revision = 'rewritten-saved-revision';
  if (variant === 'document') endpoint.document.revision = 'rewritten-document-revision';
  expect(matching(value)).toBe(false);
});

test('generated wire boundary refuses plaintext smuggled into a reference', () => {
  const value = pair();
  Object.assign(privateTransfer(value).to, { plaintext: 'private-test-canary' });
  expect(() => matching(value)).toThrow('schema');
});

// Rust deserializes absent and null binding Option fields as the same None.
// These are deliberately shape-valid DTOs, not a blanket null normalization
// of envelopes, arrays or scalar/public edit intent.
test('accepts Rust None equivalence in a private transfer document binding', () => {
  const value = pair();
  documentNone(privateTransfer(value).to);
  destination(value.result.snapshot.edits).endpoint = clone(privateTransfer(value).to);
  expect(matching(value)).toBe(true);
});

test('accepts Rust None equivalence in a secret successor draft binding', () => {
  const value = pair();
  ordinaryNone(secretTransfer(value).to.draft);
  destination(value.result.snapshot.edits).secret = clone(secretTransfer(value).to);
  expect(matching(value)).toBe(true);
});

test('accepts Rust None equivalence when Rust echoes accepted protected reference bindings', () => {
  const value = pair();
  documentNone(destination(value.input.edits).endpoint);
  ordinaryNone(destination(value.input.edits).endpoint.capturedFor!);
  ordinaryNone(destination(value.input.edits).secret.draft);
  expect(matching(value)).toBe(true);
});

test('saved private capturedFor:null remains the same Rust None as its absent echo', () => {
  const value = savedPrivate();
  destination(value.input.edits).endpoint.capturedFor = null;
  expect(matching(value)).toBe(true);
});

test('successor saved private binding may serialize a Rust None explicitly', () => {
  const value = savedPrivate();
  documentNone(destination(value.result.snapshot.edits).endpoint);
  expect(matching(value)).toBe(true);
});

test('Rust None equivalence never erases a changed non-null binding identity', () => {
  const value = pair();
  const transfer = privateTransfer(value);
  if (transfer.to.document.target.profile.kind !== 'ordinary') throw new Error('fixture_profile');
  transfer.to.document.target.profile.ordinaryId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  destination(value.result.snapshot.edits).endpoint = clone(transfer.to);
  expect(matching(value)).toBe(false);
});
