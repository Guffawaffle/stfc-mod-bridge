import type {
  BackupReceiptRef, ConfigurationSchema, DiagnosticContent, DiagnosticPreview,
  DocumentBinding, DocumentSnapshot, DraftSnapshot, FieldDefinition, FieldType,
  PublicConfigValue, Request, Reply, SaveApplicationPreferencesInput, SchemaBinding, PlanSemantics, DraftRef, SetDraftChangesResult, PrivateValueRef, SecretRef
} from '../../ui/src/generated/protocol.js';
import { commandReply, commandRequest, ordinaryBinding, ordinarySelector, protocolEvent, queryReply, queryRequest, rejectedReply, syntheticId } from './helpers.ts';
import { cursor, digest, eventFixture, evidence, hostEpoch, inventory, observed, operation, prepared, refusalFixture, replyFixture, requestFixture, unavailable } from './authoring.ts';
import type { FixtureHooks, GoldenCatalog, GoldenFixture, GoldenTranscript, ScenarioId } from './model.ts';

export const schemaBinding: SchemaBinding = {
  providerId: 'guffawaffle', schemaId: 'stfc-community-mod.config-schema', schemaVersion: '1.0.0',
  digest: digest('a'), runtimeArtifactDigest: digest('b')
};
function field(id: string, valueType: FieldType, defaultValue?: PublicConfigValue): FieldDefinition {
  return {
    fieldId: id, path: id.split('.'), valueType, sensitivity: 'public', category: 'settings',
    searchTerms: [id], apply: id === 'setting.boolean' ? 'immediate' : id === 'setting.number' ? 'restart_required' : 'next_launch',
    platforms: ['windows', 'macos'], aliases: [], deprecated: false, ...(defaultValue ? { defaultValue } : {})
  };
}
export const configurationSchema: ConfigurationSchema = {
  binding: schemaBinding,
  fields: [
    field('setting.boolean', { kind: 'boolean' }, { kind: 'boolean', value: false }),
    field('setting.integer', { kind: 'integer', minimum: '-9223372036854775808', maximum: '9223372036854775807' }, { kind: 'integer', value: '1' }),
    field('setting.number', { kind: 'number', minimum: '-2.5', maximum: '5.25' }, { kind: 'number', value: '1.5' }),
    field('setting.string', { kind: 'string', maximumLength: '64' }, { kind: 'string', value: 'Synthetic long display label for text scaling' }),
    field('setting.enum', { kind: 'enum', values: ['off', 'summary'] }, { kind: 'enum', value: 'off' }),
    field('setting.keys', { kind: 'keybinding', multiple: true, keys: ['SPACE', 'K'] }, { kind: 'keybinding', value: [{ key: 'SPACE', modifiers: [] }] }),
    field('setting.notification', { kind: 'notification_policy', sounds: ['none', 'warning'] }, { kind: 'notification_policy', value: { kind: 'disabled' } }),
    { ...field('sync.endpoint', { kind: 'string', maximumLength: '4096' }), sensitivity: 'private' },
    { ...field('sync.token', { kind: 'string', maximumLength: '4096' }), sensitivity: 'secret' }
  ],
  sync: [
    { mode: 'legacy', exposure: 'creatable', feeds: ['battlelog', 'missions'], fields: ['sync.endpoint', 'sync.token'], endpointFieldId: 'sync.endpoint', secretFieldId: 'sync.token', inheritsGlobalProxy: true },
    { mode: 'sidecar', exposure: 'existing_configuration_only', feeds: ['battlelog'], fields: ['sync.endpoint', 'sync.token'], endpointFieldId: 'sync.endpoint', secretFieldId: 'sync.token', inheritsGlobalProxy: false },
    { mode: 'majel', exposure: 'hidden', feeds: ['missions'], fields: ['sync.endpoint', 'sync.token'], endpointFieldId: 'sync.endpoint', secretFieldId: 'sync.token', inheritsGlobalProxy: true }
  ]
};
export const missingDocument: DocumentBinding = { documentId: syntheticId(2000), target: ordinaryBinding, revision: 'synthetic-document-1', baseline: { kind: 'missing' }, schema: schemaBinding };
export const existingDocument: DocumentBinding = { ...missingDocument, baseline: { kind: 'existing', fileIdentity: 'synthetic-file-1', contentDigest: digest('c') } };
export const cleanDraft: DraftSnapshot = { draft: { draftId: syntheticId(2001), hostEpoch, revision: '1', document: missingDocument }, schema: configurationSchema, edits: [], apply: [], state: 'clean', validation: [] };
export const dirtyDraft: DraftSnapshot = { ...cleanDraft, draft: { ...cleanDraft.draft, revision: '2' }, edits: [{ kind: 'set_public', fieldId: 'setting.boolean', value: { kind: 'boolean', value: true } }], apply: ['immediate'], state: 'dirty' };
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));
/** Golden custody transfer only; this fixture never possesses protected payloads. */
export function stageAcknowledgement(previous: DraftRef, candidate: DraftSnapshot): SetDraftChangesResult {
  const accepted = { draft: clone(previous), edits: clone(candidate.edits) };
  const snapshot = clone(candidate);
  snapshot.draft = { ...clone(previous), revision: (BigInt(previous.revision) + 1n).toString() };
  const protectedTransfers: SetDraftChangesResult['protectedTransfers'] = [];
  const privateRef = (from: PrivateValueRef): PrivateValueRef => {
    if (from.capturedFor == null) return from;
    const prior = protectedTransfers.find(value => value.kind === 'private' && JSON.stringify(value.from) === JSON.stringify(from));
    if (prior?.kind === 'private') return clone(prior.to);
    const to = { ...clone(from), valueId: syntheticId(2800 + protectedTransfers.length), capturedFor: snapshot.draft };
    protectedTransfers.push({ kind: 'private', from: clone(from), to }); return to;
  };
  const secretRef = (from: SecretRef): SecretRef => {
    const prior = protectedTransfers.find(value => value.kind === 'secret' && JSON.stringify(value.from) === JSON.stringify(from));
    if (prior?.kind === 'secret') return clone(prior.to);
    const to = { ...clone(from), secretId: syntheticId(2800 + protectedTransfers.length), draft: snapshot.draft };
    protectedTransfers.push({ kind: 'secret', from: clone(from), to }); return to;
  };
  for (const edit of snapshot.edits) {
    if (edit.kind === 'set_private') edit.reference = privateRef(edit.reference);
    else if (edit.kind === 'replace_secret') edit.reference = secretRef(edit.reference);
    else if (edit.kind === 'set_sync_proxy' && edit.value.kind === 'custom') edit.value.reference = privateRef(edit.value.reference);
    else if (edit.kind === 'add_sync_destination') {
      edit.destination.endpoint = privateRef(edit.destination.endpoint);
      edit.destination.secret = secretRef(edit.destination.secret);
      if (edit.destination.proxy.kind === 'custom') edit.destination.proxy.reference = privateRef(edit.destination.proxy.reference);
    }
  }
  return { accepted, snapshot, protectedTransfers };
}

export function buildCatalog(hooks: FixtureHooks): GoldenCatalog {
  const fixtures: GoldenFixture[] = [], transcripts: GoldenTranscript[] = [];
  let requestNumber = 2100;
  function pair(id: string, scenario: ScenarioId, case_: string, request: Request, reply: Reply, tags: string[] = []): void {
    const requestId = `${id}-request`, replyId = `${id}-reply`;
    fixtures.push(requestFixture(requestId, scenario, case_, request, tags), replyFixture(replyId, scenario, case_, reply, tags));
    transcripts.push({ id: `${id}-journey`, scenario, case: case_, steps: [{ type: 'boundary', reason: 'initial', cursor }, { type: 'exchange', request: requestId, reply: replyId }], expected: { accepted: true } });
  }
  const id = () => syntheticId(requestNumber++);
  let requestId = id();
  pair('sc08-open-clean-draft', 'SC-08', 'Open preserves a missing document without creating a file',
    commandRequest(requestId, { name: 'open_draft', input: { document: missingDocument } }),
    commandReply(requestId, { name: 'open_draft', output: cleanDraft }), ['clean', 'missing-file']);
  requestId = id();
  pair('sc08-stage-dirty-draft', 'SC-08', 'One scoped draft retains an edit while navigation changes views',
    commandRequest(requestId, { name: 'set_draft_changes', input: { draft: cleanDraft.draft, edits: dirtyDraft.edits } }),
    commandReply(requestId, { name: 'set_draft_changes', output: stageAcknowledgement(cleanDraft.draft, dirtyDraft) }), ['dirty']);
  requestId = id();
  pair('sc08-discard-draft', 'SC-08', 'Discard acknowledges the exact prior draft revision',
    commandRequest(requestId, { name: 'discard_draft', input: { draft: dirtyDraft.draft } }),
    commandReply(requestId, { name: 'discard_draft', output: { draftId: dirtyDraft.draft.draftId, hostEpoch, previousRevision: '2' } }), ['discard']);
  const invalidDraft: DraftSnapshot = { ...dirtyDraft, edits: [{ kind: 'set_public', fieldId: 'setting.integer', value: { kind: 'string', value: 'still editable' } }], apply: ['next_launch'], state: 'invalid', validation: [{ fieldId: 'setting.integer', code: 'invalid_type' }] };
  const staleDraft: DraftSnapshot = { ...dirtyDraft, state: 'stale' };
  const draftReadCursor = { ...cursor, sequence: '7' };
  for (const [index, draft] of [cleanDraft, dirtyDraft, invalidDraft, staleDraft].entries()) {
    const lookupId = syntheticId(2950 + index);
    pair(`sc08-get-current-${draft.state}-draft`, 'SC-08', 'Read the current scoped successor without opening or restaging a draft',
      queryRequest(lookupId, { name: 'get_draft', input: { hostEpoch, draftId: draft.draft.draftId } }),
      queryReply(lookupId, { name: 'get_draft', output: { cursor: draftReadCursor, draft: observed(draft) } }), [draft.state, 'reconcile']);
  }
  pair('sc08-get-missing-draft', 'SC-08', 'A same-host discarded draft is missing without creating a replacement',
    queryRequest(syntheticId(2954), { name: 'get_draft', input: { hostEpoch, draftId: dirtyDraft.draft.draftId } }),
    queryReply(syntheticId(2954), { name: 'get_draft', output: { cursor: draftReadCursor, draft: { status: 'missing', evidence } } }), ['missing', 'reconcile']);
  pair('sc08-get-foreign-host-draft', 'SC-08', 'A foreign host lookup refuses before consulting draft custody',
    queryRequest(syntheticId(2955), { name: 'get_draft', input: { hostEpoch: syntheticId(2990), draftId: dirtyDraft.draft.draftId } }),
    rejectedReply(syntheticId(2955), { code: 'plan_host_mismatch', retryDisposition: 'after_resnapshot', violations: [] }), ['foreign-host']);
  fixtures.push(refusalFixture('sc08-get-draft-cursor-epoch-mismatch', 'SC-08', 'A draft observation cannot carry a cursor from a different host', 'reply',
    queryReply(syntheticId(2956), { name: 'get_draft', output: { cursor: { ...draftReadCursor, hostEpoch: syntheticId(2990) }, draft: observed(dirtyDraft) } })));
  const extraLookup = queryRequest(syntheticId(2957), { name: 'get_draft', input: { hostEpoch, draftId: dirtyDraft.draft.draftId } });
  fixtures.push(refusalFixture('sc08-get-draft-extra-selector', 'SC-08', 'The immutable lookup has no caller-selected revision or target substitution', 'request',
    { ...extraLookup, body: { type: 'query', query: { name: 'get_draft', input: { hostEpoch, draftId: dirtyDraft.draft.draftId, revision: '2' } } } }, false));
  const mismatchedRequest = requestFixture('sc08-get-draft-mismatch-request', 'SC-08', 'An individually valid read cannot substitute another draft',
    queryRequest(syntheticId(2958), { name: 'get_draft', input: { hostEpoch, draftId: syntheticId(2991) } }));
  const mismatchedReply = replyFixture('sc08-get-draft-mismatch-reply', 'SC-08', 'An individually valid read cannot substitute another draft',
    queryReply(syntheticId(2958), { name: 'get_draft', output: { cursor: draftReadCursor, draft: observed(dirtyDraft) } }));
  fixtures.push(mismatchedRequest, mismatchedReply);
  transcripts.push({ id: 'sc08-get-draft-mismatch-journey', scenario: 'SC-08', case: 'Cross-message draft identity is exact',
    steps: [{ type: 'boundary', reason: 'initial', cursor }, { type: 'exchange', request: mismatchedRequest.id, reply: mismatchedReply.id }],
    expected: { accepted: false, code: 'transcript_draft_binding' } });
  for (const draft of [cleanDraft, dirtyDraft, invalidDraft, staleDraft]) fixtures.push(eventFixture(`sc08-${draft.state}-event`, 'SC-08', `Retain ${draft.state} draft state explicitly`, protocolEvent({ ...cursor, sequence: '1' }, { type: 'draft_changed', draft }), [draft.state]));
  const foreignEvent = protocolEvent({ ...cursor, hostEpoch: syntheticId(2200), sequence: '1' }, { type: 'draft_changed', draft: dirtyDraft });
  fixtures.push(refusalFixture('sc08-foreign-host-draft-event', 'SC-08', 'Draft events cannot cross their captured host epoch', 'event', foreignEvent));
  // Associate the semantic refusal with a domain command as well as its event.
  const inconsistentClean = { ...dirtyDraft, state: 'clean' as const };
  fixtures.push(refusalFixture('sc08-clean-draft-with-edit', 'SC-08', 'A clean result cannot carry unapplied edits', 'reply', commandReply(id(), { name: 'open_draft', output: inconsistentClean })));

  const snapshot: DocumentSnapshot = {
    binding: missingDocument, schema: configurationSchema,
    fields: configurationSchema.fields.map(definition => ({ fieldId: definition.fieldId, overridden: false, value: definition.sensitivity === 'public'
      ? { kind: 'public' as const, value: definition.defaultValue! } : definition.sensitivity === 'secret' ? { kind: 'secret' as const, configured: false } : { kind: 'absent' as const } })),
    preservation: 'supported', sync: []
  };
  requestId = id();
  pair('sc09-schema-all-field-types', 'SC-09', 'All seven semantic field types are schema driven; sensitive values remain references',
    queryRequest(requestId, { name: 'read_configuration', input: { target: ordinarySelector } }),
    queryReply(requestId, { name: 'read_configuration', output: observed(snapshot) }), ['schema', 'all-field-types']);
  const edits: DraftSnapshot['edits'] = [
    { kind: 'set_public', fieldId: 'setting.integer', value: { kind: 'integer', value: '9223372036854775807' } },
    { kind: 'set_public', fieldId: 'setting.number', value: { kind: 'number', value: '2.5' } },
    { kind: 'set_public', fieldId: 'setting.enum', value: { kind: 'enum', value: 'summary' } },
    { kind: 'set_public', fieldId: 'setting.keys', value: { kind: 'keybinding', value: [{ key: 'K', modifiers: ['control'] }] } },
    { kind: 'set_public', fieldId: 'setting.notification', value: { kind: 'notification_policy', value: { kind: 'channels', system: true, audio: true, sound: 'warning' } } }
  ];
  const mixedDraft: DraftSnapshot = { ...dirtyDraft, edits, apply: ['next_launch', 'restart_required'] };
  requestId = id();
  pair('sc09-mixed-apply-timing', 'SC-09', 'Whole-draft timing includes every applicable field timing',
    commandRequest(requestId, { name: 'set_draft_changes', input: { draft: cleanDraft.draft, edits } }),
    commandReply(requestId, { name: 'set_draft_changes', output: stageAcknowledgement(cleanDraft.draft, mixedDraft) }), ['mixed-timing']);
  const sensitiveLeak: DraftSnapshot = { ...invalidDraft, edits: [{ kind: 'set_public', fieldId: 'sync.token', value: { kind: 'string', value: 'synthetic-leak-marker' } }], validation: [{ fieldId: 'sync.token', code: 'secret_reference_required' }] };
  fixtures.push(refusalFixture('sc09-get-draft-secret-echo', 'SC-09', 'A current-draft read cannot expose protected payloads as public edits', 'reply',
    queryReply(syntheticId(2959), { name: 'get_draft', output: { cursor: draftReadCursor, draft: observed(sensitiveLeak) } })));
  fixtures.push(refusalFixture('sc09-invalid-draft-secret-echo', 'SC-09', 'Even invalid draft projections cannot expose a secret field as a public value', 'reply', commandReply(id(), { name: 'set_draft_changes', output: stageAcknowledgement(cleanDraft.draft, sensitiveLeak) })));
  const aliasCollision = clone(snapshot);
  aliasCollision.schema.fields[1].aliases = [aliasCollision.schema.fields[0].path];
  fixtures.push(refusalFixture('sc09-schema-alias-collision', 'SC-09', 'Field aliases cannot overlap another canonical TOML path', 'reply', queryReply(id(), { name: 'read_configuration', output: observed(aliasCollision) })));
  for (const sensitivity of ['private', 'secret'] as const) {
    const binding = { draft: cleanDraft.draft, fieldId: sensitivity === 'private' ? 'sync.endpoint' : 'sync.token', sensitivity };
    const outcome = sensitivity === 'private'
      ? { status: 'captured_private' as const, reference: { valueId: syntheticId(2210), document: missingDocument, fieldId: binding.fieldId, revision: 'synthetic-private-1', capturedFor: cleanDraft.draft } }
      : { status: 'captured_secret' as const, reference: { secretId: syntheticId(2211), draft: cleanDraft.draft, fieldId: binding.fieldId } };
    requestId = id();
    pair(`sc09-protected-${sensitivity}-entry`, 'SC-09', `Native protected ${sensitivity} entry returns scoped custody without wire plaintext`,
      commandRequest(requestId, { name: 'request_sensitive_input', input: binding }),
      commandReply(requestId, { name: 'request_sensitive_input', output: { binding, outcome } }), ['protected-entry']);
    requestId = id();
    pair(`sc09-protected-${sensitivity}-cancelled`, 'SC-09', 'Cancelled native entry captures no value',
      commandRequest(requestId, { name: 'request_sensitive_input', input: binding }),
      commandReply(requestId, { name: 'request_sensitive_input', output: { binding, outcome: { status: 'cancelled' } } }), ['cancelled']);
  }
  const newSync: DraftSnapshot = {
    ...dirtyDraft, edits: [{ kind: 'add_sync_destination', destination: {
      id: 'synthetic-destination-1', mode: 'legacy', endpoint: { valueId: syntheticId(2220), document: missingDocument, fieldId: 'sync.endpoint', revision: 'synthetic-endpoint-1', capturedFor: dirtyDraft.draft },
      secret: { secretId: syntheticId(2221), draft: dirtyDraft.draft, fieldId: 'sync.token' },
      proxy: { kind: 'global' }, feeds: [{ feedId: 'battlelog', desired: 'inherit' }, { feedId: 'missions', desired: 'off' }]
    } }], apply: ['next_launch']
  };
  requestId = id();
  pair('sc09-schema-supported-sync-draft', 'SC-09', 'A creatable sync destination binds supported feeds and protected endpoint/token fields',
    commandRequest(requestId, { name: 'set_draft_changes', input: { draft: dirtyDraft.draft, edits: newSync.edits } }),
    commandReply(requestId, { name: 'set_draft_changes', output: stageAcknowledgement(dirtyDraft.draft, newSync) }), ['data-sync']);
  const unsupportedSync = clone(newSync);
  unsupportedSync.schema.sync[0]!.exposure = 'hidden';
  fixtures.push(refusalFixture('sc09-sync-hidden-creation', 'SC-09', 'A schema-bearing valid draft cannot create a hidden sync mode', 'reply', commandReply(id(), { name: 'set_draft_changes', output: stageAcknowledgement(dirtyDraft.draft, unsupportedSync) })));
  const unsupportedFeed = clone(newSync);
  if (unsupportedFeed.edits[0].kind === 'add_sync_destination') unsupportedFeed.edits[0].destination.feeds[0].feedId = 'unsupported-feed';
  fixtures.push(refusalFixture('sc09-sync-unsupported-feed', 'SC-09', 'A schema-bearing valid draft cannot advertise an unsupported feed', 'reply', commandReply(id(), { name: 'set_draft_changes', output: stageAcknowledgement(dirtyDraft.draft, unsupportedFeed) })));

  const saveSemantics: PlanSemantics = {
    hashProfile: 'bridge-plan-semantic-json-v1' as const, action: 'save_configuration' as const,
    capture: { kind: 'save_configuration' as const, input: { draft: dirtyDraft, candidateDigest: digest('d') } },
    trustDomain: 'configuration' as const, effects: ['write_configuration' as const]
  };
  const save = prepared(hooks, saveSemantics, 2300);
  const restaged = stageAcknowledgement(dirtyDraft.draft, dirtyDraft);
  requestId = id();
  pair('sc10-restage-dirty-draft', 'SC-10', 'Save of an already dirty backend draft acknowledges the exact revision-2 intent with one revision-3 successor',
    commandRequest(requestId, { name: 'set_draft_changes', input: restaged.accepted }),
    commandReply(requestId, { name: 'set_draft_changes', output: restaged }), ['dirty', 'restage']);
  const restagedSave = prepared(hooks, { ...saveSemantics,
    capture: { kind: 'save_configuration', input: { draft: restaged.snapshot, candidateDigest: digest('d') } } }, 2350);
  requestId = id();
  pair('sc10-save-restaged-draft', 'SC-10', 'Preparation captures the acknowledged revision-3 draft rather than reusing its old references',
    commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'save_configuration', input: { draft: restaged.snapshot.draft } } } }),
    commandReply(requestId, { name: 'prepare', output: restagedSave }), ['save', 'restage']);
  requestId = id();
  pair('sc10-save-reviewed-draft', 'SC-10', 'Save binds exact draft, document, schema and candidate bytes',
    commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'save_configuration', input: { draft: dirtyDraft.draft } } } }),
    commandReply(requestId, { name: 'prepare', output: save }), ['save']);
  const savedDocument: DocumentBinding = { ...missingDocument, revision: 'synthetic-document-2', baseline: { kind: 'existing', fileIdentity: 'synthetic-file-2', contentDigest: digest('d') } };
  const saved = operation(save, 2301, '3', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'configuration_written', document: savedDocument } } });
  const restagedSaved = operation(restagedSave, 2351, '3', saved.state);
  requestId = id();
  pair('sc10-save-restaged-result', 'SC-10', 'Completed restaged Save retains exactly its revision-3 captured semantics',
    queryRequest(requestId, { name: 'get_operation', input: { operationId: restagedSaved.operationId } }),
    queryReply(requestId, { name: 'get_operation', output: { operation: observed(restagedSaved) } }), ['completed', 'restage']);
  requestId = id();
  pair('sc10-save-verified-result', 'SC-10', 'First meaningful write accounts for newly created document without inventing a prior backup',
    queryRequest(requestId, { name: 'get_operation', input: { operationId: saved.operationId } }),
    queryReply(requestId, { name: 'get_operation', output: { operation: observed(saved) } }), ['completed']);
  const backup: BackupReceiptRef = { backupId: syntheticId(2302), document: existingDocument, retainedDigest: digest('c'), nativeBackupRef: 'synthetic-native-backup-1', createdAt: '2026-10-03T10:00:00Z' };
  requestId = id();
  pair('sc10-configuration-history', 'SC-10', 'History identifies retained prior bytes and original target',
    queryRequest(requestId, { name: 'configuration_history', input: { document: existingDocument } }),
    queryReply(requestId, { name: 'configuration_history', output: observed(inventory([backup])) }), ['history']);
  const restoreInput = { document: savedDocument, backup };
  const restore = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'restore_configuration', capture: { kind: 'restore_configuration', input: restoreInput }, trustDomain: 'configuration', effects: ['write_configuration'] }, 2303);
  requestId = id();
  pair('sc10-restore-reviewed-backup', 'SC-10', 'Restore is an explicit reviewed mutation, preserving backup identity and retained bytes',
    commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'restore_configuration', input: restoreInput } } }),
    commandReply(requestId, { name: 'prepare', output: restore }), ['restore']);
  const badBackup = { ...backup, retainedDigest: digest('e') };
  fixtures.push(refusalFixture('sc10-backup-digest-mismatch', 'SC-10', 'Backup digest must describe the captured retained baseline bytes', 'request', commandRequest(id(), { name: 'prepare', input: { intent: { kind: 'restore_configuration', input: { document: savedDocument, backup: badBackup } } } })));
  for (const code of ['stale_revision', 'unsupported_preservation_syntax', 'backup_unavailable', 'operation_busy'] as const) {
    requestId = id();
    pair(`sc10-save-${code.replaceAll('_', '-')}`, 'SC-10', `Save retains scoped draft on ${code}`,
      commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'save_configuration', input: { draft: dirtyDraft.draft } } } }),
      rejectedReply(requestId, { code, retryDisposition: 'after_resnapshot', violations: [] }), ['blocked']);
  }

  const content: DiagnosticContent = { target: ordinaryBinding, disclosure: 'redacted', facts: [{ kind: 'target', value: ordinaryBinding }, { kind: 'issue', value: { code: 'native_unavailable' } }] };
  const preview: DiagnosticPreview = { content, reference: { previewId: syntheticId(2400), hostEpoch, revision: 'synthetic-preview-1', target: ordinaryBinding, disclosure: 'redacted', digest: hooks.diagnosticDigest(content) } };
  requestId = id();
  pair('sc16-redacted-preview', 'SC-16', 'Preview binds exact reviewed redacted content',
    queryRequest(requestId, { name: 'diagnostic_preview', input: { target: ordinarySelector, disclosure: 'redacted' } }),
    queryReply(requestId, { name: 'diagnostic_preview', output: preview }), ['redacted']);
  const exportInput = { preview: preview.reference, destination: { destinationId: syntheticId(2401), hostEpoch, nativeTargetRef: 'synthetic-export-destination-1', revision: 'synthetic-destination-1' } };
  requestId = id();
  pair('sc16-capture-export-destination', 'SC-16', 'Shared native or headless selection returns opaque destination custody for the exact reviewed preview',
    commandRequest(requestId, { name: 'request_export_destination', input: { preview: preview.reference } }),
    commandReply(requestId, { name: 'request_export_destination', output: { binding: { preview: preview.reference }, outcome: { status: 'captured', destination: exportInput.destination } } }), ['destination-selection']);
  for (const status of ['cancelled', 'unavailable'] as const) {
    requestId = id();
    pair(`sc16-destination-${status}`, 'SC-16', `Destination selection ${status} produces no fake native reference`,
      commandRequest(requestId, { name: 'request_export_destination', input: { preview: preview.reference } }),
      commandReply(requestId, { name: 'request_export_destination', output: { binding: { preview: preview.reference }, outcome: status === 'cancelled' ? { status } : { status, reason: 'selection_unavailable' } } }), [status]);
  }
  const exportPlan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'export_diagnostics', capture: { kind: 'export_diagnostics', input: exportInput }, trustDomain: 'diagnostics', effects: ['export_diagnostics'] }, 2402);
  requestId = id();
  pair('sc16-export-reviewed-preview', 'SC-16', 'Export refers to the reviewed preview and chosen destination',
    commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'export_diagnostics', input: exportInput } } }),
    commandReply(requestId, { name: 'prepare', output: exportPlan }), ['export']);
  transcripts.push({ id: 'sc16-preview-destination-reviewed-export-journey', scenario: 'SC-16', case: 'Preview then select destination then review export through the shared protocol', steps: [
    { type: 'boundary', reason: 'initial', cursor },
    { type: 'exchange', request: 'sc16-redacted-preview-request', reply: 'sc16-redacted-preview-reply' },
    { type: 'exchange', request: 'sc16-capture-export-destination-request', reply: 'sc16-capture-export-destination-reply' },
    { type: 'exchange', request: 'sc16-export-reviewed-preview-request', reply: 'sc16-export-reviewed-preview-reply' }
  ], expected: { accepted: true } });
  const leakedPreview: DiagnosticPreview = { ...preview, content: { ...content, paths: { installation: { platform: 'windows', value: 'C:\\Synthetic\\Game' } } } };
  fixtures.push(refusalFixture('sc16-redacted-path-disclosure', 'SC-16', 'Redacted preview cannot carry undisclosed filesystem paths', 'reply', queryReply(id(), { name: 'diagnostic_preview', output: leakedPreview })));
  const disclosedContent: DiagnosticContent = { ...content, disclosure: 'include_paths', paths: { installation: { platform: 'windows', value: 'C:\\Synthetic\\Game' } } };
  const disclosedPreview: DiagnosticPreview = { content: disclosedContent, reference: { ...preview.reference, disclosure: 'include_paths', digest: hooks.diagnosticDigest(disclosedContent) } };
  requestId = id();
  pair('sc16-explicit-path-disclosure', 'SC-16', 'Optional synthetic path disclosure is separately represented',
    queryRequest(requestId, { name: 'diagnostic_preview', input: { target: ordinarySelector, disclosure: 'include_paths' } }),
    queryReply(requestId, { name: 'diagnostic_preview', output: disclosedPreview }), ['disclosure']);
  requestId = id();
  pair('sc16-unavailable-support', 'SC-16', 'Read-only support capability remains explicitly unavailable',
    queryRequest(requestId, { name: 'read_configuration', input: { target: ordinarySelector } }),
    queryReply(requestId, { name: 'read_configuration', output: unavailable() }), ['unavailable']);

  for (const [index, theme] of ['system', 'light', 'dark'].entries()) {
    const preferences: SaveApplicationPreferencesInput = { expectedRevision: 'synthetic-preferences-1', values: { theme: theme as 'system'|'light'|'dark', motion: 'reduced', lastTarget: { installationId: 'a'.repeat(32), profile: { kind: 'ordinary' } } } };
    const plan = prepared(hooks, { hashProfile: 'bridge-plan-semantic-json-v1', action: 'save_application_preferences', capture: { kind: 'save_application_preferences', input: preferences }, trustDomain: 'application_state', effects: ['save_application_preferences'] }, 2500 + index);
    requestId = id();
    pair(`sc17-theme-${theme}`, 'SC-17', `Presentation preference ${theme} retains reduced-motion intent without claiming native accessibility QA`,
      commandRequest(requestId, { name: 'prepare', input: { intent: { kind: 'save_application_preferences', input: preferences } } }),
      commandReply(requestId, { name: 'prepare', output: plan }), ['presentation']);
  }
  fixtures.push(refusalFixture('sc17-unknown-theme', 'SC-17', 'Presentation preference enums are closed', 'request', { protocolVersion: 1, requestId: id(), body: { type: 'command', command: { name: 'prepare', input: { intent: { kind: 'save_application_preferences', input: { expectedRevision: 'synthetic-preferences-1', values: { theme: 'unknown', motion: 'system' } } } } } } }, false));
  return { fixtures, transcripts };
}
