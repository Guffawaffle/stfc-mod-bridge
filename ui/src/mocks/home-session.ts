import { BridgeClient, canonicalData, captureData, decodeEvent, decodeReply, decodeRequest, type DeepReadonly } from '../client';
import type { ActionId, DraftSnapshot, MutationIntent, OperationSnapshot, PreparedPlan, Reply, Request, ResolvedTarget, SessionBinding, Snapshot, TargetSelector } from '../generated/protocol';
import { BridgeFacade } from '../state';
import { ManualClock } from './clock';
import { ScenarioTransport, type MockScript, type MockStep } from './scenario-transport';

export const homeModes = ['ordinary_ready', 'isolated_ready', 'focus_sessions', 'launch_uncertain', 'unknown', 'missing', 'offline', 'recovery', 'dirty_draft'] as const;
export type HomeMode = typeof homeModes[number];
export type DevelopmentDraftOutcome = 'save' | 'discard';
export interface HomeRequestRecord { readonly requestId: string; readonly method: string; readonly frame: string; }
export interface HomeSession {
  readonly mode: HomeMode; readonly draftOutcome: DevelopmentDraftOutcome;
  readonly facade: BridgeFacade; readonly client: BridgeClient; readonly transport: ScenarioTransport; readonly clock: ManualClock;
  readonly script: DeepReadonly<MockScript>; readonly provenance: readonly { readonly id: string; readonly sha256: string }[];
  readonly requests: readonly HomeRequestRecord[]; readonly instructions: string;
  tick(milliseconds?: number): Promise<void>;
  settle(maximumTurns?: number): Promise<void>;
  dispose(): void;
}

// Lazy raw imports preserve the unchanged fixture bytes for provenance. This
// module belongs exclusively to the separate development entry, never App.
const rawFixtures = import.meta.glob('../../../contracts/fixtures/*.json', { query: '?raw', import: 'default' });
class Sources {
  private values = new Map<string, string>();
  async load(id: string): Promise<string> {
    const cached = this.values.get(id); if (cached !== undefined) return cached;
    const loader = rawFixtures[`../../../contracts/fixtures/${id}.json`];
    if (!loader) throw new Error('home_missing_shared_fixture');
    const raw = await loader(); if (typeof raw !== 'string') throw new Error('home_fixture_not_raw');
    this.values.set(id, raw); return raw;
  }
  async request(id: string): Promise<Request> { const raw = await this.load(id); decodeRequest(raw); return JSON.parse(raw); }
  async reply(id: string): Promise<Reply> { const raw = await this.load(id); decodeReply(raw); return JSON.parse(raw); }
  async manifest() {
    return Promise.all([...this.values].sort(([a], [b]) => a.localeCompare(b)).map(async ([id, raw]) => {
      const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(raw));
      return Object.freeze({ id, sha256: [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('') });
    }));
  }
}
function snapshotValue(frame: Reply): Snapshot {
  if (frame.body.type !== 'result' || frame.body.result.type !== 'query' || frame.body.result.query.name !== 'snapshot') throw new Error('home_snapshot_fixture');
  return frame.body.result.query.output;
}
function planValue(frame: Reply): PreparedPlan {
  if (frame.body.type !== 'result' || frame.body.result.type !== 'command' || frame.body.result.command.name !== 'prepare') throw new Error('home_prepare_fixture');
  return frame.body.result.command.output;
}
function operationValue(frame: Reply): OperationSnapshot {
  if (frame.body.type !== 'result') throw new Error('home_operation_fixture');
  if (frame.body.result.type === 'command' && frame.body.result.command.name === 'commit') return frame.body.result.command.output;
  if (frame.body.result.type === 'query' && frame.body.result.query.name === 'get_operation' && frame.body.result.query.output.operation.status === 'observed') return frame.body.result.query.output.operation.value;
  throw new Error('home_operation_fixture');
}
function draftValue(frame: Reply): DraftSnapshot {
  if (frame.body.type !== 'result' || frame.body.result.type !== 'command' || frame.body.result.command.name !== 'set_draft_changes') throw new Error('home_draft_fixture');
  return frame.body.result.command.output.snapshot;
}
function preparedIntent(frame: Request): MutationIntent {
  if (frame.body.type !== 'command' || frame.body.command.name !== 'prepare') throw new Error('home_intent_fixture');
  return frame.body.command.input.intent;
}
function exchange(request: Request, reply: Reply, lost = false): MockStep {
  const paired = { ...reply, requestId: request.requestId };
  decodeRequest(JSON.stringify(request)); decodeReply(JSON.stringify(paired));
  return { type: 'exchange', request, reply: paired, ...(lost ? { delivery: 'lost' } : {}) };
}
function targetSelector(target: ResolvedTarget): TargetSelector {
  if (target.installation.kind !== 'registered') throw new Error('home_registered_fixture');
  return { installation: { kind: 'registered', id: target.installation.registrationId, revisionAssertion: target.installation.registrationRevision },
    profile: target.profile.kind === 'ordinary' ? { kind: 'ordinary' } : { kind: 'isolated', id: target.profile.id, revisionAssertion: target.profile.revision } };
}
function assertSelector(intent: MutationIntent, target: ResolvedTarget): void {
  if (intent.kind !== 'launch_ordinary' && intent.kind !== 'launch_isolated') throw new Error('home_launch_fixture');
  const asserted = targetSelector(target);
  // Only presentation's observed revision assertions are added. The accepted
  // semantic capture/review digest remains byte-for-byte the fixture's value.
  intent.input.target.installation = asserted.installation;
  if (intent.kind === 'launch_ordinary' && asserted.profile.kind === 'ordinary') intent.input.target.profile = asserted.profile;
  else if (intent.kind === 'launch_isolated' && asserted.profile.kind === 'isolated') intent.input.target.profile = asserted.profile;
  else throw new Error('home_profile_fixture');
}
async function targetReads(sources: Sources, target: ResolvedTarget, availability: 'available' | 'unknown' | 'offline' | 'recovery' = 'available'): Promise<MockStep[]> {
  const request = await sources.request('sc-01-resolve-explicit-ordinary-request'), reply = await sources.reply('sc-01-resolve-explicit-ordinary-reply');
  if (request.body.type !== 'query' || request.body.query.name !== 'resolve_target' || reply.body.type !== 'result'
    || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'resolve_target') throw new Error('home_resolve_fixture');
  request.body.query.input.target = targetSelector(target);
  reply.body.result.query.output.target = { ...reply.body.result.query.output.target, status: 'observed', value: target } as typeof reply.body.result.query.output.target;
  const query = await sources.request('sc-02-ordinary-ready-action-request');
  const action = await sources.reply(availability === 'unknown' ? 'sc-02-ordinary-unknown-action-reply' : 'sc-02-ordinary-ready-action-reply');
  if (query.body.type !== 'query' || query.body.query.name !== 'get_actions' || action.body.type !== 'result'
    || action.body.result.type !== 'query' || action.body.result.query.name !== 'get_actions') throw new Error('home_actions_fixture');
  const kind: ActionId = target.profile.kind === 'ordinary' ? 'launch_ordinary' : 'launch_isolated';
  query.body.query.input = { actions: [kind], scope: { kind: 'target', target } };
  action.body.result.query.output[0].action = kind;
  // Explicit development variants of generated availability reasons. They are
  // scripted browser facts, not a policy computation or native qualification.
  if (availability === 'offline') action.body.result.query.output[0].availability = { status: 'unavailable', reason: { code: 'offline' } };
  if (availability === 'recovery') action.body.result.query.output[0].availability = { status: 'blocked', reasons: [{ code: 'interrupted_transaction' }] };
  return [exchange(request, reply), exchange(query, action)];
}
async function observePair(sources: Sources, operation: OperationSnapshot): Promise<MockStep> {
  const request = await sources.request('sc14-retained-operation-request'), reply = await sources.reply('sc14-retained-operation-reply');
  if (request.body.type !== 'query' || request.body.query.name !== 'get_operation' || reply.body.type !== 'result'
    || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'get_operation' || reply.body.result.query.output.operation.status !== 'observed') throw new Error('home_observe_fixture');
  request.body.query.input.operationId = operation.operationId; reply.body.result.query.output.operation.value = operation;
  return exchange(request, reply);
}

/** Strict fixture composition for the separate Home development preview. */
export async function createHomeSession(mode: HomeMode, options: { draftOutcome?: DevelopmentDraftOutcome } = {}): Promise<HomeSession> {
  if (!homeModes.includes(mode)) throw new Error('home_invalid_mode');
  const draftOutcome = options.draftOutcome ?? 'save'; if (!['save', 'discard'].includes(draftOutcome)) throw new Error('home_invalid_draft_outcome');
  const sources = new Sources(), snapshotRequest = await sources.request('sc15-complete-empty-snapshot-request');
  const snapshotReply = await sources.reply('sc15-complete-empty-snapshot-reply'), snapshot = snapshotValue(snapshotReply);
  const installationReply = await sources.reply('sc-01-registered-installations-list-reply');
  const profilesReply = await sources.reply('sc-05-catalog-with-duplicate-display-names-reply');
  if (installationReply.body.type !== 'result' || installationReply.body.result.type !== 'query' || installationReply.body.result.query.name !== 'list_installations'
    || profilesReply.body.type !== 'result' || profilesReply.body.result.type !== 'query' || profilesReply.body.result.query.name !== 'list_profiles') throw new Error('home_inventory_fixture');
  snapshot.installations = installationReply.body.result.query.output; snapshot.profiles = profilesReply.body.result.query.output;
  const windows = await sources.reply('sc18-windows-x86-64-sessions-reply');
  const recycled = await sources.reply('sc-04-multiple-and-recycled-process-observations-reply');
  if (windows.body.type !== 'result' || windows.body.result.type !== 'query' || windows.body.result.query.name !== 'list_sessions' || windows.body.result.query.output.status !== 'observed'
    || recycled.body.type !== 'result' || recycled.body.result.type !== 'query' || recycled.body.result.query.name !== 'list_sessions' || recycled.body.result.query.output.status !== 'observed') throw new Error('home_sessions_fixture');
  snapshot.sessions = mode === 'focus_sessions' ? { ...windows.body.result.query.output, value: { ...windows.body.result.query.output.value,
    items: [...windows.body.result.query.output.value.items, ...recycled.body.result.query.output.value.items] as typeof windows.body.result.query.output.value.items } }
    : { ...windows.body.result.query.output, value: { ...windows.body.result.query.output.value, items: [] } };
  if (mode === 'unknown' && snapshot.installations.status === 'observed') { snapshot.installations.value.completeness = 'partial'; snapshot.installations.value.issues = [{ code: 'incomplete_catalog' }]; }
  if (mode === 'missing' && snapshot.installations.status === 'observed') snapshot.installations.value.items = [];
  if (mode === 'recovery') snapshot.operations.items = [operationValue(await sources.reply('sc15-post-restart-recovery-reply'))];
  decodeReply(JSON.stringify(snapshotReply));
  const steps: MockStep[] = [exchange(snapshotRequest, snapshotReply)]; let dirty: DraftSnapshot | undefined;
  let instructions = 'Choose Synthetic install A and Ordinary · current user, then Use target. Review launch and Confirm action are separate decisions.';
  if (mode === 'focus_sessions') {
    const binding: SessionBinding = snapshot.sessions.status === 'observed' ? snapshot.sessions.value.items[0].binding : (() => { throw new Error('home_windows_session'); })();
    const query = await sources.request('sc-02-ordinary-ready-action-request'), reply = await sources.reply('sc-02-ordinary-ready-action-reply');
    if (query.body.type !== 'query' || query.body.query.name !== 'get_actions' || reply.body.type !== 'result' || reply.body.result.type !== 'query' || reply.body.result.query.name !== 'get_actions') throw new Error('home_focus_actions');
    query.body.query.input = { actions: ['focus_session'], scope: { kind: 'session', session: binding } }; reply.body.result.query.output[0].action = 'focus_session';
    steps.push(exchange(query, reply), exchange(await sources.request('sc18-windows-x86-64-focus-prepare-request'), await sources.reply('sc18-windows-x86-64-focus-prepare-reply')),
      exchange(await sources.request('sc18-windows-x86-64-focus-commit-request'), await sources.reply('sc18-windows-x86-64-focus-commit-reply')));
    instructions = 'Check focus for session 1 (the shared Windows binding), then Review focus for session 1 and Confirm action. Other rows expose the distinct old/recycled PID observations; this bounded script submits only session 1.';
  } else if (mode === 'isolated_ready') {
    const prepare = await sources.request('sc-03-working-readiness-prepare-request'), prepared = await sources.reply('sc-03-working-readiness-prepare-reply');
    const capture = planValue(prepared).semantics.capture; if (capture.kind !== 'launch_isolated') throw new Error('home_isolated_capture');
    assertSelector(preparedIntent(prepare), capture.target);
    steps.push(...await targetReads(sources, capture.target), exchange(prepare, prepared), exchange(await sources.request('sc-03-working-readiness-commit-request'), await sources.reply('sc-03-working-readiness-commit-reply')));
    const event = decodeEvent(await sources.load('sc-03-completed-readiness-event')); if (event.body.type !== 'operation_changed') throw new Error('home_completed_fixture');
    steps.push(await observePair(sources, JSON.parse(JSON.stringify(event.body.operation))));
    instructions = 'Choose Synthetic install A and Synthetic profile · isolated 2, Use target, then Use existing isolated data. Review launch → Confirm action gives admission; Refresh action outcome observes the accepted completion.';
  } else if (mode === 'dirty_draft') {
    dirty = draftValue(await sources.reply('sc08-stage-dirty-draft-reply'));
    if (draftOutcome === 'discard') steps.push(exchange(await sources.request('sc08-discard-draft-request'), await sources.reply('sc08-discard-draft-reply')));
    else {
      const synchronize = await sources.request('sc10-restage-dirty-draft-request');
      if (synchronize.body.type !== 'command' || synchronize.body.command.name !== 'set_draft_changes') throw new Error('home_sync_fixture');
      if (canonicalData(synchronize.body.command.input.draft) !== canonicalData(dirty.draft)) throw new Error('home_sync_binding');
      steps.push(exchange(synchronize, await sources.reply('sc10-restage-dirty-draft-reply')),
        exchange(await sources.request('sc10-save-restaged-draft-request'), await sources.reply('sc10-save-restaged-draft-reply')));
      const plan = planValue(await sources.reply('sc10-save-restaged-draft-reply'));
      const completed = await sources.reply('sc10-save-restaged-result-reply'), operation = operationValue(completed);
      const commit: Request = { protocolVersion: 1, requestId: '00000845-1111-4111-8111-111111111111', body: { type: 'command', command: { name: 'commit', input: {
        idempotencyKey: '00000001-1111-4111-8111-111111111111', planRef: plan.planRef } } } };
      const admitted: Reply = { protocolVersion: 1, requestId: commit.requestId, body: { type: 'result', result: { type: 'command', command: {
        name: 'commit', output: { ...operation, operationRevision: '1', state: { status: 'admitted' } } } } } };
      steps.push(exchange(commit, admitted), exchange(await sources.request('sc10-save-restaged-result-request'), completed));
    }
    const targetCapture = planValue(await sources.reply('sc-03-profile-one-prepare-reply')).semantics.capture;
    if (targetCapture.kind !== 'launch_isolated') throw new Error('home_dirty_target');
    steps.push(...await targetReads(sources, targetCapture.target));
    instructions = `The exact shared revision-2 dirty draft is open. View navigation preserves its edit. Choose Synthetic install A and Synthetic profile · isolated 2, then Use target to queue one decision. This development script accepts ${draftOutcome === 'save' ? 'Save → Confirm Save → Refresh Save outcome' : 'Discard'}; Stay submits nothing. Change Development draft outcome and reset for the other script.`;
  } else if (mode !== 'missing') {
    const prepare = await sources.request('sc14-prepare-request'), prepared = await sources.reply('sc14-prepare-reply');
    const capture = planValue(prepared).semantics.capture; if (capture.kind !== 'launch_ordinary') throw new Error('home_ordinary_capture');
    assertSelector(preparedIntent(prepare), capture.target);
    const availability = mode === 'unknown' ? 'unknown' : mode === 'offline' ? 'offline' : mode === 'recovery' ? 'recovery' : 'available';
    steps.push(...await targetReads(sources, capture.target, availability));
    if (availability === 'available') {
      const commit = await sources.request('sc14-admit-request'), admitted = await sources.reply('sc14-admit-reply');
      steps.push(exchange(prepare, prepared), exchange(commit, admitted, mode === 'launch_uncertain'));
      if (mode === 'launch_uncertain') steps.push(exchange(await sources.request('sc14-exact-replay-request'), await sources.reply('sc14-exact-replay-reply')));
      const completed = snapshotValue(await sources.reply('sc15-complete-terminal-snapshot-reply')).operations.items[0];
      steps.push(await observePair(sources, completed));
      if (mode === 'launch_uncertain') instructions += ' The first commit observation is lost. Wait for timeout, then Replay exact action explicitly; admission still waits for Refresh action outcome.';
      else instructions += ' Admission remains pending until Refresh action outcome observes the accepted completed operation.';
    } else instructions += ` The scripted ${availability} reason keeps Review launch unavailable. Unknown and recovery do not imply a stopped game.`;
  } else instructions = 'The complete installation inventory has no items. No target is selected and no launch is available.';
  const provenance = await sources.manifest();
  const script = captureData<MockScript>({ id: `br18-home-${mode}-${draftOutcome}`, scenario: 'SC-18', case: 'Synthetic browser Home composition; no native execution', correlationSlot: 'requestId', sources: provenance, steps });
  const clock = new ManualClock(), transport = new ScenarioTransport(script as MockScript, { clock, latencyMs: 200 });
  const records: HomeRequestRecord[] = []; let sequence = 0, disposed = false;
  const client = new BridgeClient({ exchange(frame, options) {
    const request = decodeRequest(frame);
    if (records.length >= 256) return Promise.reject({ code: 'delivery_failed', delivery: 'not_sent' });
    records.push(Object.freeze({ requestId: request.requestId, method: request.body.type === 'query' ? request.body.query.name : request.body.command.name, frame }));
    return transport.exchange(frame, options);
  }, subscribe: (event, fault) => transport.subscribe(event, fault) }, {
    clock, timeoutMs: 1200, requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222',
  });
  const facade = new BridgeFacade(client, { idempotencyKey: () => {
    const next = transport.expectedRequest;
    if (next?.body.type !== 'command' || next.body.command.name !== 'commit') throw new Error('home_expected_commit');
    return next.body.command.input.idempotencyKey;
  } });
  if (dirty) { facade.work.openDraft(dirty); facade.work.observations.observeDraft(dirty); }
  const microtasks = async () => { for (let turn = 0; turn < 16; turn++) await Promise.resolve(); };
  return { mode, draftOutcome, facade, client, transport, clock, script, provenance, instructions,
    get requests() { return Object.freeze([...records]); },
    async tick(milliseconds = 200) { if (disposed) return; clock.advanceBy(milliseconds); await microtasks(); },
    async settle(maximumTurns = 64) {
      if (!Number.isSafeInteger(maximumTurns) || maximumTurns < 1 || maximumTurns > 256) throw new Error('home_settle_bound');
      for (let turn = 0; turn < maximumTurns && !disposed; turn++) {
        await microtasks();
        // WebCrypto verification uses the platform event loop, rather than the
        // delivery clock. Let it finish before advancing an observation timeout
        // whose raw reply was already received. No review is confirmed here.
        await new Promise<void>(resolve => setTimeout(resolve, 0));
        if (disposed) return;
        if (!clock.pendingCount) return;
        if (client.pendingCount && !transport.state.pendingExchanges && !transport.state.backendBusy) continue;
        clock.runNext();
      }
      if (!disposed && clock.pendingCount) throw new Error('home_settle_limit');
    },
    dispose() { if (disposed) return; disposed = true; facade.dispose(); client.dispose(); transport.dispose(); clock.dispose(); },
  };
}
