import { expect, test } from 'vitest';
import { render } from 'svelte/server';
import { BridgeClient, ObservationStore, decodeEvent } from '../../src/client';
import { BridgeFacade } from '../../src/state';
import { Home, availabilityText, inventoryText, profileLabel, selectorFor, sessionStatus, targetLabels } from '../../src/views/home';
import { ActionReview, UnsavedChanges, WorkspaceNavigation } from '../../src/navigation';
import { actionProgress, editSummary } from '../../src/navigation/action-presentation';
import { frame, snapshot } from './helpers';

function facade() { const client = new BridgeClient({ subscribe: () => () => {}, exchange: () => Promise.reject({ code: 'unavailable_binding', delivery: 'not_sent' }) }, { requestId: () => '00000001-2222-4222-8222-222222222222' }); return new BridgeFacade(client, { idempotencyKey: () => '00000001-1111-4111-8111-111111111111' }); }

test('Home uninitialized view exposes explicit native selectors and unavailable action without auto selection', () => {
  const api = facade(), body = render(Home, { props: { facade: api } }).body;
  expect(body).toMatch(/<h1[^>]*>Shuttle Bay<\/h1>/); expect(body).toContain('<label for="home-installation"'); expect(body).toContain('<label for="home-profile"');
  expect(body).toMatch(/<button[^>]+disabled[^>]*>[^]*?Review launch/); expect(body).toContain('Not selected'); expect(body).toContain('Waiting for observations');
  expect(api.work.state.selector).toBeUndefined(); expect(api.client.pendingCount).toBe(0); api.dispose();
});

test('Home duplicate profile names remain distinct options and captured summaries preserve the ordinary mode', () => {
  const observed = snapshot(), store = new ObservationStore(); store.acceptSnapshot(observed);
  const catalog = observed.profiles; if (catalog.status !== 'observed') throw new Error('profiles_fixture');
  expect(profileLabel(catalog.value.items[1], 1)).not.toBe(profileLabel(catalog.value.items[2], 2));
  const one = selectorFor('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', store.state)!;
  const two = selectorFor('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', store.state)!;
  expect(one.profile).not.toEqual(two.profile); expect(one.installation).toEqual(two.installation);
  const ordinary = selectorFor('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'ordinary', store.state)!; expect(ordinary.profile).toEqual({ kind: 'ordinary' });
  expect(targetLabels(undefined, ordinary, store.state).profile).toBe('Ordinary · current user');
});

test('Home partial missing unknown and unavailable inventories keep distinct truthful copy', () => {
  const base = frame('sc-01-registered-installations-list-reply').body.result.query.output;
  expect(inventoryText({ ...base, value: { ...base.value, completeness: 'partial' } }, 'Installations')).toContain('incomplete');
  expect(inventoryText({ status: 'unknown', reason: 'access_denied' }, 'Sessions')).toContain('unknown');
  expect(inventoryText({ status: 'unavailable', reason: 'native_unavailable' }, 'Sessions')).toContain('unavailable');
  expect(inventoryText({ status: 'missing' }, 'Sessions')).toContain('observation'); expect(inventoryText(undefined, 'Sessions')).toContain('not been observed');
});

test('Home backend blocked unknown offline and recovery reasons are shown without granting authority', () => {
  const available = frame('sc-02-ordinary-ready-action-reply').body.result.query.output[0].availability;
  expect(availabilityText(available)).toContain('Confirmation is still required'); expect(available.grantsPermission).toBe(false); expect(available.grantsLock).toBe(false);
  expect(availabilityText({ status: 'blocked', reasons: [{ code: 'interrupted_transaction' }] })).toContain('recovery');
  expect(availabilityText({ status: 'unknown', reason: { code: 'unknown_identity' } })).toContain('Unknown.');
  expect(availabilityText({ status: 'unavailable', reason: { code: 'offline' } })).toContain('connection');
  expect(availabilityText({ status: 'blocked', reasons: [{ code: 'busy' }] })).toContain('holds this resource');
});

test('Home recycled PID live identity and isolation readiness remain separate and never imply account sign in', () => {
  const observed = snapshot().sessions; if (observed.status !== 'observed') throw new Error('sessions_fixture');
  expect(observed.value.items[0].binding.process.pid).toBe(observed.value.items[1].binding.process.pid);
  expect(sessionStatus(observed.value.items[0])).toContain('Live identity not matched'); expect(sessionStatus(observed.value.items[0])).toContain('Isolation ready');
  const unknown = { ...observed.value.items[0], liveIdentity: { status: 'unknown' as const, reason: 'access_denied' as const, evidence: observed.evidence } };
  expect(sessionStatus(unknown)).toContain('unknown'); expect(sessionStatus(unknown)).not.toMatch(/stopped|signed.in|logged.in/i);
});

test('Home normal render hides private paths and escapes long labels while separating maintenance destinations', () => {
  const api = facade(), observed = snapshot();
  if (observed.installations.status !== 'observed') throw new Error('installations_fixture');
  observed.installations.value.items[0].name = '<img src=x> A long installation label '.repeat(4);
  observed.installations.value.items[0].binding.nativeTargetRef = 'C:\\Private\\Commander\\game';
  api.work.observations.acceptSnapshot(observed); api.requestTarget({ installation: { kind: 'directory', directory: { platform: 'windows', value: 'C:\\Private\\Commander\\game' } }, profile: { kind: 'ordinary' } });
  const body = render(Home, { props: { facade: api } }).body;
  expect(body).not.toContain('C:\\Private\\Commander'); expect(body).toContain('&lt;img src=x>'); expect(body).not.toContain('<img src=x>');
  expect(body).toContain('Game and recovery'); expect(body).toContain('Community Mod'); expect(body).toContain('Bridge and preferences'); api.dispose();
});

test('Home workspace navigation marks the current view and view changes preserve the same dirty draft', () => {
  const api = facade(), draft = frame('sc08-open-clean-draft-reply').body.result.command.output, edits = frame('sc08-stage-dirty-draft-request').body.command.input.edits;
  api.work.openDraft(draft); api.stage(edits); api.navigate('engineering');
  const body = render(WorkspaceNavigation, { props: { facade: api } }).body; expect(body).toContain('aria-current="page"'); expect(body).toContain('Bridge workspace');
  api.navigate('home'); expect(api.work.state.draft).toEqual(draft); expect(api.work.state.edits).toEqual(edits); expect(api.work.state.view).toBe('home'); api.dispose();
});

test('Home queued dirty target change renders one semantic Save Discard Stay decision without claiming admission success', () => {
  const api = facade(), observed = snapshot(); api.work.observations.acceptSnapshot(observed);
  api.work.openDraft(frame('sc08-open-clean-draft-reply').body.result.command.output); api.stage(frame('sc08-stage-dirty-draft-request').body.command.input.edits);
  const old = api.work.state.selector; api.requestTarget(frame('sc-03-profile-two-prepare-request').body.command.input.intent.input.target, 'home-target');
  const body = render(UnsavedChanges, { props: { facade: api } }).body; expect(body.match(/<dialog/g)).toHaveLength(1); expect(body).toContain('Save'); expect(body).toContain('Discard'); expect(body).toContain('Stay');
  expect(api.work.state.selector).toEqual(old); expect(api.stay()).toBe(true); expect(api.work.state.pendingNavigation).toBeUndefined(); expect(api.work.state.dirty).toBe(true);
  const review = render(ActionReview, { props: { facade: api } }).body; expect(review).toContain('Confirm action'); expect(api.client.replayCount).toBe(0); api.dispose();
});

test('Home operation progress preserves exact observed counts and refuses unknown stale or unsafe numeric percentages', () => {
  const event = frame('sc-03-working-readiness-event'), operation = event.body.operation;
  operation.state.progress = { phase: 'copy', measurement: { unit: 'files', completed: '5', total: '10' } }; decodeEvent(JSON.stringify(event));
  expect(actionProgress(operation, 'authoritative')).toMatchObject({ confidence: 'current', value: 5, max: 10, detail: '5 of 10 files observed.' });
  expect(actionProgress(operation, 'stale')).toMatchObject({ confidence: 'stale' }); expect(actionProgress(operation, 'stale').value).toBeUndefined();
  delete operation.state.progress.measurement.total; decodeEvent(JSON.stringify(event)); expect(actionProgress(operation, 'authoritative').value).toBeUndefined();
  operation.state.progress.measurement = { unit: 'bytes', completed: '9007199254740993', total: '9007199254740994' }; decodeEvent(JSON.stringify(event));
  expect(actionProgress(operation, 'authoritative')).toMatchObject({ confidence: 'unknown', detail: '9007199254740993 of 9007199254740994 bytes observed.' });
  expect(actionProgress(operation, 'authoritative').value).toBeUndefined(); expect(actionProgress(undefined, 'authoritative').confidence).toBe('unknown');
});

test('Home Save review copy describes protected edits without exposing references or private public-string paths', () => {
  const edits = frame('sc09-mixed-apply-timing-request').body.command.input.edits;
  for (const edit of edits) expect(editSummary(edit)).not.toMatch(/synthetic-(private|secret)|C:\\|\/Users\//);
  const publicEdit = frame('sc08-stage-dirty-draft-request').body.command.input.edits[0]; expect(editSummary(publicEdit)).toBe('On');
  expect(editSummary({ ...publicEdit, value: { kind: 'string', value: 'C:\\Private\\Commander\\game' } })).toBe('Public value updated');
});
