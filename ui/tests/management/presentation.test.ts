import { expect, test } from 'vitest';
import { availabilityReason, backupMatches, captureSummary, inventoryStatus, operationStatus } from '../../src/views/management';
import type { ActionAvailability } from '../../src/generated/protocol';
import { operation, output, plan, reply } from './helpers';
test('all terminal outcomes have distinct honest presentation', () => {
    const fixtures = ['sc15-terminal-no-change-reply', 'sc15-terminal-failed-reply', 'sc15-terminal-rolled-back-reply'];
    const values = fixtures.map(id => operationStatus(operation(id)));
    expect(new Set(values).size).toBe(fixtures.length);
    expect(values).toEqual(['Already satisfied', 'Failed; review recovery choices', 'Rolled back']);
    expect(operationStatus(operation('sc15-post-restart-recovery-reply'))).toBe('Recovery required');
});
test('captured source-switch summary preserves runtime and configuration participants with explicit warning', () => {
    const captured = plan('sc11-runtime-switch-source-reply'), summary = captureSummary(captured);
    if (captured.semantics.capture.kind !== 'runtime_switch_source')
        throw new Error('source_fixture');
    expect(summary.warning).toMatch(/runtime source.*configuration/);
    expect(summary.lines).toContain(`Current provider: ${captured.semantics.capture.input.current.binding.providerId}`);
    expect(summary.lines).toContain(`Selected provider: ${captured.semantics.capture.input.selectedRelease.providerId}`);
    expect(summary.lines.join(' ')).not.toContain(captured.semantics.capture.input.selectedRelease.authority);
});
test('configuration review excludes protected reference custody and public value payloads', () => {
    const captured = plan('sc10-save-reviewed-draft-reply');
    if (captured.semantics.capture.kind !== 'save_configuration')
        throw new Error('save_fixture');
    const privateReply = reply('sc09-protected-private-entry-reply'), secretReply = reply('sc09-protected-secret-entry-reply');
    if (privateReply.body.type !== 'result' || privateReply.body.result.type !== 'command' || privateReply.body.result.command.name !== 'request_sensitive_input' || privateReply.body.result.command.output.outcome.status !== 'captured_private' || secretReply.body.type !== 'result' || secretReply.body.result.type !== 'command' || secretReply.body.result.command.name !== 'request_sensitive_input' || secretReply.body.result.command.output.outcome.status !== 'captured_secret')
        throw new Error('protected_fixture');
    const privateReference = { ...privateReply.body.result.command.output.outcome.reference, capturedFor: captured.semantics.capture.input.draft.draft }, secretReference = { ...secretReply.body.result.command.output.outcome.reference, draft: captured.semantics.capture.input.draft.draft };
    captured.semantics.capture.input.draft.edits.push({ kind: 'set_private', fieldId: privateReference.fieldId, reference: privateReference }, { kind: 'replace_secret', fieldId: secretReference.fieldId, reference: secretReference });
    const summary = JSON.stringify(captureSummary(captured));
    expect(summary).not.toContain(privateReference.valueId);
    expect(summary).not.toContain(secretReference.secretId);
    expect(summary).not.toContain('synthetic-native-target-1');
    expect(summary).toContain(captured.semantics.capture.input.draft.draft.document.documentId);
});
test('backup route distinguishes exact target, document and producer while allowing historical baseline', () => {
    const history = output('sc10-configuration-history-reply', 'configuration_history');
    if (history.status !== 'observed')
        throw new Error('history_fixture');
    const backup = history.value.items[0], document = { ...backup.document, revision: 'new-document-baseline' };
    expect(backupMatches(document, backup)).toBe(true);
    expect(backupMatches({ ...document, documentId: '000007d0-3333-4333-8333-333333333333' }, backup)).toBe(false);
    expect(backupMatches({ ...document, schema: { ...document.schema, providerId: 'other-producer' } }, backup)).toBe(false);
    expect(backupMatches({ ...document, target: { ...document.target, installation: { ...document.target.installation, physicalId: 'foreign-physical' } } }, backup)).toBe(false);
});
test('Bridge review shows offered release and actual package identities without inventing current version', () => {
    const captured = plan('sc13-bridge-update-reply'), summary = captureSummary(captured);
    if (captured.semantics.capture.kind !== 'bridge_update')
        throw new Error('bridge_fixture');
    expect(summary.lines).toContain(`Offered release: ${captured.semantics.capture.input.selectedRelease.releaseVersion}`);
    expect(summary.lines).toContain(`Current package: ${captured.semantics.capture.input.selectedRelease.current.packageDigest}`);
    expect(summary.lines.join(' ')).not.toContain('undefined');
});
test('unavailable and partial inventories never read as a complete empty catalog', () => { expect(inventoryStatus({ status: 'unavailable' })).toMatch(/unavailable/); expect(inventoryStatus({ status: 'observed', value: { completeness: 'partial' } })).toMatch(/Some entries/); expect(inventoryStatus(undefined)).toMatch(/Not yet/); });

const availabilityCases: readonly { name: string; availability: ActionAvailability; expected: string }[] = [
    { name: 'blocked active session', availability: { status: 'blocked', reasons: [{ code: 'active_session', resource: { kind: 'session', id: '00000d00-1111-4111-8111-111111111111' } }] }, expected: 'Blocked. An active session affects this action.' },
    { name: 'blocked interrupted transaction', availability: { status: 'blocked', reasons: [{ code: 'active_session' }, { code: 'interrupted_transaction', remediation: 'recover_game_update', resource: { kind: 'operation', id: '00000d00-1111-4111-8111-111111111111' } }] }, expected: 'Blocked. An active session affects this action. An interrupted operation needs recovery.' },
    { name: 'unknown identity', availability: { status: 'unknown', reason: { code: 'unknown_identity', resource: { kind: 'installation', id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' } } }, expected: 'Unknown. The target identity is not confirmed.' },
    { name: 'unavailable native route', availability: { status: 'unavailable', reason: { code: 'unavailable_native_route', resource: { kind: 'installation', id: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' } } }, expected: 'Unavailable. This action is currently unavailable.' },
];
test.each(availabilityCases)('management availability preserves $name public feedback', ({ availability, expected }) => {
    const text = availabilityReason({ action: 'game_update', availability });
    expect(text).toBe(expected);
    expect(text).not.toContain('00000d00-1111-4111-8111-111111111111');
    expect(text).not.toContain('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
    expect(text).not.toContain('recover_game_update');
});
