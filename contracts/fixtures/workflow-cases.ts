// Controlled protocol examples only. Root validates every message with the
// generated schema and Rust codec before resolving transcript fixture IDs.
// These traces model replay, cancellation and observations; they prove neither
// a real writer exclusion nor persistence, worker custody or native execution.
import type {
  BridgeError, CloseObligation, Command, CommandResult, Cursor, Event, EventBody, OperationSnapshot,
  OrdinaryTargetSelector, PlanSemantics, Query, QueryResult, RecoveryRef, SessionBinding, SessionProjection, Snapshot
} from '../../ui/src/generated/protocol.js';
import type { FixtureHooks, GoldenCatalog, GoldenFixture, GoldenStep, GoldenTranscript, ScenarioId } from './model.ts';
import {
  commandReply, commandRequest, ordinaryBinding, ordinarySelector, protocolEvent,
  queryReply, queryRequest, rejectedReply, syntheticId
} from './helpers.ts';
import {
  cursor, digest, eventFixture, hostEpoch, inventory, observed, operation, prepared,
  refusalFixture, replyFixture, requestFixture, streamId, unavailable
} from './authoring.ts';

const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
type ExchangeStep = Extract<GoldenStep, { type: 'exchange' }>;
const maxCounter = '18446744073709551615';
const progress = { phase: 'synthetic-working', measurement: { unit: 'files' as const, completed: '1', total: '2' } };

export function buildCatalog(hooks: FixtureHooks): GoldenCatalog {
  const fixtures: GoldenFixture[] = [], transcripts: GoldenTranscript[] = [];
  let sequence = 6000;
  const nextId = () => syntheticId(sequence++);
  const at = (value: string, epoch = hostEpoch, stream = streamId): Cursor => ({ hostEpoch: epoch, streamId: stream, sequence: value });
  const initial = (): GoldenStep => ({ type: 'boundary', reason: 'initial', cursor });
  const error = (code: BridgeError['code'], retryDisposition: BridgeError['retryDisposition'] = 'after_resnapshot'): BridgeError => ({ code, retryDisposition, violations: [] });
  function commandCase(id: string, scenario: ScenarioId, input: Command, output: CommandResult): ExchangeStep {
    const requestId = nextId();
    fixtures.push(requestFixture(`${id}-request`, scenario, id, commandRequest(requestId, input)));
    fixtures.push(replyFixture(`${id}-reply`, scenario, id, commandReply(requestId, output)));
    return { type: 'exchange', request: `${id}-request`, reply: `${id}-reply` };
  }
  function queryCase(id: string, scenario: ScenarioId, input: Query, output: QueryResult): ExchangeStep {
    const requestId = nextId();
    fixtures.push(requestFixture(`${id}-request`, scenario, id, queryRequest(requestId, input)));
    fixtures.push(replyFixture(`${id}-reply`, scenario, id, queryReply(requestId, output)));
    return { type: 'exchange', request: `${id}-request`, reply: `${id}-reply` };
  }
  function rejectionCase(id: string, scenario: ScenarioId, input: Command | Query, refusal: BridgeError, family: 'command' | 'query' = 'command'): ExchangeStep {
    const requestId = nextId();
    const request = family === 'command' ? commandRequest(requestId, input as Command) : queryRequest(requestId, input as Query);
    fixtures.push(requestFixture(`${id}-request`, scenario, id, request));
    fixtures.push(replyFixture(`${id}-reply`, scenario, id, rejectedReply(requestId, refusal)));
    return { type: 'exchange', request: `${id}-request`, reply: `${id}-reply` };
  }
  function eventCase(id: string, scenario: ScenarioId, value: Cursor, body: EventBody): GoldenStep {
    fixtures.push(eventFixture(id, scenario, id, protocolEvent(value, body)));
    return { type: 'event', event: id };
  }
  function trace(id: string, scenario: ScenarioId, steps: GoldenStep[], expected: GoldenTranscript['expected'] = { accepted: true }) {
    transcripts.push({ id, scenario, case: id, steps, expected });
  }
  const launchSemantics: PlanSemantics = {
    hashProfile: 'bridge-plan-semantic-json-v1', action: 'launch_ordinary', trustDomain: 'session', effects: ['launch_session'],
    capture: { kind: 'launch_ordinary', target: ordinaryBinding, catalogRevision: 'synthetic-catalog-1', runtime: { kind: 'absent' } }
  };
  const launchSelector: OrdinaryTargetSelector = { installation: ordinarySelector.installation, profile: { kind: 'ordinary' } };
  const launchPlan = prepared(hooks, launchSemantics, sequence++);
  const admitted = operation(launchPlan, sequence++, '1', { status: 'admitted' });
  const commitInput = { planRef: launchPlan.planRef, idempotencyKey: nextId() };
  const prepare14 = commandCase('sc14-prepare', 'SC-14', { name: 'prepare', input: { intent: { kind: 'launch_ordinary', input: { target: launchSelector } } } }, { name: 'prepare', output: launchPlan });
  const admit14 = commandCase('sc14-admit', 'SC-14', { name: 'commit', input: commitInput }, { name: 'commit', output: admitted });
  const replay14 = commandCase('sc14-exact-replay', 'SC-14', { name: 'commit', input: clone(commitInput) }, { name: 'commit', output: admitted });
  const newEpoch = nextId(), newStream = nextId();
  const restart: GoldenStep = { type: 'boundary', reason: 'restart', cursor: at('0', newEpoch, newStream) };
  const lookup14 = queryCase('sc14-retained-operation', 'SC-14', { name: 'get_operation', input: { operationId: admitted.operationId } }, { name: 'get_operation', output: { operation: observed(admitted) } });
  const lost: GoldenStep = { ...admit14, delivery: 'lost' };
  trace('sc14-lost-response-restart-exact-replay', 'SC-14', [initial(), prepare14, lost, restart, replay14, lookup14]);
  const changedInput = { ...clone(commitInput), planRef: { ...commitInput.planRef, reviewDigest: digest('e') } };
  const conflict14 = rejectionCase('sc14-changed-input-conflict', 'SC-14', { name: 'commit', input: changedInput }, error('idempotency_conflict'));
  const freshInput = { ...clone(commitInput), idempotencyKey: nextId() };
  const oldHost14 = rejectionCase('sc14-fresh-old-host-plan', 'SC-14', { name: 'commit', input: freshInput }, error('plan_host_mismatch'));
  trace('sc14-replay-lookup-precedes-old-plan-check', 'SC-14', [initial(), prepare14, admit14, restart, conflict14, oldHost14, replay14]);
  const wrongReplay = commandCase('sc14-wrong-replay-operation', 'SC-14', { name: 'commit', input: commitInput }, { name: 'commit', output: { ...admitted, operationId: nextId() } });
  trace('sc14-refuse-replay-replacement', 'SC-14', [initial(), prepare14, admit14, restart, wrongReplay], { accepted: false, code: 'transcript_replay_operation', step: 4 });
  const wronglyLost = rejectionCase('sc14-forgot-admission', 'SC-14', { name: 'commit', input: commitInput }, error('plan_host_mismatch'));
  trace('sc14-refuse-erased-admission', 'SC-14', [initial(), prepare14, lost, restart, wronglyLost], { accepted: false, code: 'transcript_replay_operation', step: 4 });
  const wrongConflict = commandCase('sc14-changed-input-admitted', 'SC-14', { name: 'commit', input: changedInput }, { name: 'commit', output: admitted });
  trace('sc14-refuse-changed-input-replay', 'SC-14', [initial(), prepare14, admit14, restart, wrongConflict], { accepted: false, code: 'transcript_idempotency_conflict', step: 4 });
  const missing14 = queryCase('sc14-operation-missing', 'SC-14', { name: 'get_operation', input: { operationId: admitted.operationId } }, { name: 'get_operation', output: { operation: { status: 'missing', evidence: observed(admitted).evidence } } });
  trace('sc14-refuse-missing-admitted-operation', 'SC-14', [initial(), prepare14, admit14, restart, missing14], { accepted: false, code: 'transcript_admitted_operation_missing', step: 4 });
  const moved = clone(admitted);
  if (moved.semantics.capture.kind !== 'launch_ordinary') throw new Error('Synthetic launch fixture expected');
  moved.semantics.capture.target.installation.physicalId = 'synthetic-other-physical-installation';
  const moved14 = commandCase('sc14-different-physical-capture', 'SC-14', { name: 'commit', input: commitInput }, { name: 'commit', output: moved });
  trace('sc14-refuse-capture-substitution', 'SC-14', [initial(), prepare14, moved14], { accepted: false, code: 'transcript_capture_changed', step: 2 });
  const correlationId = nextId();
  fixtures.push(requestFixture('sc14-correlation-request', 'SC-14', 'Valid request needs its exact reply ID', commandRequest(correlationId, { name: 'commit', input: commitInput })));
  fixtures.push(replyFixture('sc14-correlation-reply', 'SC-14', 'An independently valid reply for another request must refuse', commandReply(nextId(), { name: 'commit', output: admitted })));
  trace('sc14-refuse-cross-request-reply', 'SC-14', [initial(), { type: 'exchange', request: 'sc14-correlation-request', reply: 'sc14-correlation-reply' }], { accepted: false, code: 'transcript_reply_binding', step: 1 });
  const variantId = nextId();
  fixtures.push(requestFixture('sc14-result-variant-request', 'SC-14', 'A command needs its corresponding result variant', commandRequest(variantId, { name: 'commit', input: commitInput })));
  fixtures.push(replyFixture('sc14-result-variant-reply', 'SC-14', 'An independently valid query result cannot answer commit', queryReply(variantId, { name: 'hello', output: { hostEpoch, hostKind: 'windows_x64', supportedVersions: [1], implementedCommands: [] } })));
  trace('sc14-refuse-query-result-for-command', 'SC-14', [initial(), { type: 'exchange', request: 'sc14-result-variant-request', reply: 'sc14-result-variant-reply' }], { accepted: false, code: 'transcript_result_variant', step: 1 });
  // A modeled busy reply is a domain projection, never evidence of exclusion.
  const busy14 = rejectionCase('sc14-second-submit-busy', 'SC-14', { name: 'commit', input: { ...clone(commitInput), idempotencyKey: nextId() } }, error('operation_busy', 'after_user_choice'));
  trace('sc14-modeled-second-submit-busy', 'SC-14', [initial(), prepare14, admit14, busy14]);

  const event1 = eventCase('sc14-event-one', 'SC-14', at('1'), { type: 'snapshot_invalidated', reason: 'operation_changed' });
  const event2 = eventCase('sc14-event-two', 'SC-14', at('2'), { type: 'snapshot_invalidated', reason: 'session_changed' });
  const event3 = eventCase('sc14-event-three', 'SC-14', at('3'), { type: 'snapshot_invalidated', reason: 'catalog_changed' });
  const event21 = eventCase('sc14-event-after-retention-gap', 'SC-14', at('21'), { type: 'snapshot_invalidated', reason: 'retention_gap' });
  const snapshotStream = nextId();
  const snapshotAnchor = at('0', hostEpoch, snapshotStream);
  const snapshot14 = queryCase('sc14-resnapshot', 'SC-14', { name: 'snapshot', input: {} }, { name: 'snapshot', output: {
    cursor: snapshotAnchor, preferences: unavailable(), profiles: unavailable(), installations: unavailable(), sessions: unavailable(),
    capabilities: inventory([]), operations: inventory([admitted])
  } });
  const afterSnapshot = eventCase('sc14-event-new-stream', 'SC-14', at('1', hostEpoch, snapshotStream), { type: 'snapshot_invalidated', reason: 'operation_changed' });
  const resnapshotRequired = rejectionCase('sc14-resume-gap-refusal', 'SC-14', { name: 'resume_events', input: { after: at('2'), maximumEvents: '128' } }, error('resnapshot_required'), 'query');
  trace('sc14-reconnect-retention-gap-resnapshot', 'SC-14', [initial(), prepare14, admit14, event1, { type: 'boundary', reason: 'reconnect', cursor: at('1') }, event2, resnapshotRequired,
    { type: 'boundary', reason: 'retention_gap', cursor: at('20') }, event21, { type: 'boundary', reason: 'resnapshot', cursor: snapshotAnchor }, snapshot14, afterSnapshot]);
  trace('sc14-refuse-silent-event-gap', 'SC-14', [initial(), event1, event3], { accepted: false, code: 'transcript_sequence', step: 2 });
  trace('sc14-refuse-event-replay', 'SC-14', [initial(), event1, event1], { accepted: false, code: 'transcript_sequence', step: 2 });
  trace('sc14-refuse-reordered-events', 'SC-14', [initial(), event2, event1], { accepted: false, code: 'transcript_sequence', step: 1 });
  const eventBody = (step: GoldenStep): Event => {
    if (step.type !== 'event') throw new Error('Synthetic event reference expected');
    const fixture = fixtures.find(item => item.id === step.event);
    if (!fixture || !('payload' in fixture) || fixture.kind !== 'event' || !fixture.expectedSemantic) throw new Error('Synthetic event fixture missing');
    return fixture.payload;
  };
  const resume14 = queryCase('sc14-contiguous-event-batch', 'SC-14', { name: 'resume_events', input: { after: at('1'), maximumEvents: '2' } }, { name: 'resume_events', output: {
    after: at('1'), events: [eventBody(event2), eventBody(event3)], next: at('3')
  } });
  trace('sc14-resume-contiguous-batch', 'SC-14', [initial(), event1, resume14]);
  trace('sc14-refuse-batch-from-stale-cursor', 'SC-14', [initial(), event1, event2, resume14], { accepted: false, code: 'transcript_resume_cursor', step: 3 });
  const wrongStream = eventCase('sc14-wrong-stream-event', 'SC-14', at('1', hostEpoch, snapshotStream), { type: 'snapshot_invalidated', reason: 'session_changed' });
  trace('sc14-refuse-unannounced-stream', 'SC-14', [initial(), wrongStream], { accepted: false, code: 'transcript_stream', step: 1 });
  const wrongEpoch = eventCase('sc14-wrong-epoch-event', 'SC-14', at('1', newEpoch, newStream), { type: 'snapshot_invalidated', reason: 'host_restarted' });
  trace('sc14-refuse-unannounced-host', 'SC-14', [initial(), wrongEpoch], { accepted: false, code: 'transcript_epoch', step: 1 });
  const invalidBatch = queryReply(nextId(), { name: 'resume_events', output: { after: at('1'), events: [eventBody(event3)], next: at('3') } });
  fixtures.push(refusalFixture('sc14-codec-refuse-discontinuous-batch', 'SC-14', 'A batch with a missing sequence refuses before relationship checks', 'reply', invalidBatch));

  const prepare15 = commandCase('sc15-prepare', 'SC-15', { name: 'prepare', input: { intent: { kind: 'launch_ordinary', input: { target: launchSelector } } } }, { name: 'prepare', output: launchPlan });
  const admit15 = commandCase('sc15-admit', 'SC-15', { name: 'commit', input: commitInput }, { name: 'commit', output: admitted });
  const base15 = (): GoldenStep[] => [initial(), prepare15, admit15];
  const running = { ...clone(admitted), operationRevision: '2', state: { status: 'running' as const, progress } };
  const requested = { ...clone(admitted), operationRevision: '3', state: { status: 'cancellation_requested' as const, progress } };
  const cancelled: OperationSnapshot = { ...clone(admitted), operationRevision: '4', state: { status: 'completed', outcome: { kind: 'cancelled_before_commit', reason: 'cancellation_accepted' } } };
  const observe15 = (id: string, value: OperationSnapshot) => queryCase(id, 'SC-15', { name: 'get_operation', input: { operationId: value.operationId } }, { name: 'get_operation', output: { operation: observed(value) } });
  const running15 = observe15('sc15-running', running);
  const requestCancel = commandCase('sc15-cancel-requested', 'SC-15', { name: 'cancel_operation', input: { operationId: admitted.operationId, expectedOperationRevision: '2' } }, { name: 'cancel_operation', output: { kind: 'requested', operation: requested } });
  const pending = [{ kind: 'operation' as const, operationId: admitted.operationId, operationRevision: '3' }];
  const deferred = commandCase('sc15-close-deferred', 'SC-15', { name: 'request_host_close', input: { expectedCursor: cursor } }, { name: 'request_host_close', output: { kind: 'deferred', obligations: pending } });
  const finishCancel = commandCase('sc15-cancel-before-commit', 'SC-15', { name: 'cancel_operation', input: { operationId: admitted.operationId, expectedOperationRevision: '3' } }, { name: 'cancel_operation', output: { kind: 'cancelled_before_commit', operation: cancelled } });
  const ready15 = commandCase('sc15-close-ready', 'SC-15', { name: 'request_host_close', input: { expectedCursor: cursor } }, { name: 'request_host_close', output: { kind: 'ready' } });
  trace('sc15-request-cancellation-retain-close-obligation', 'SC-15', [...base15(), running15, requestCancel, deferred, finishCancel, ready15]);
  trace('sc15-refuse-close-with-pending-operation', 'SC-15', [...base15(), ready15], { accepted: false, code: 'transcript_close_obligations', step: 3 });
  const staleClose = commandCase('sc15-close-stale-obligation', 'SC-15', { name: 'request_host_close', input: { expectedCursor: cursor } }, { name: 'request_host_close', output: {
    kind: 'deferred', obligations: [{ kind: 'operation', operationId: admitted.operationId, operationRevision: '1' }]
  } });
  trace('sc15-refuse-stale-close-revision', 'SC-15', [...base15(), running15, staleClose], { accepted: false, code: 'transcript_close_obligations', step: 4 });
  const closeEvent = eventCase('sc15-close-deferred-event', 'SC-15', at('1'), { type: 'host_close_deferred', obligations: [{ kind: 'operation', operationId: admitted.operationId, operationRevision: '2' }] });
  trace('sc15-close-event-covers-observed-operation', 'SC-15', [...base15(), running15, closeEvent]);
  const tooLate = commandCase('sc15-cancel-too-late', 'SC-15', { name: 'cancel_operation', input: { operationId: admitted.operationId, expectedOperationRevision: '2' } }, { name: 'cancel_operation', output: { kind: 'too_late', operation: running } });
  const session: SessionBinding = { sessionId: nextId(), revision: 'synthetic-session-1', process: {
    pid: 9876, startIdentity: { platform: 'windows', value: 'synthetic-process-generation-1' }, executableIdentity: 'synthetic-game-executable-1',
    installationPhysicalId: ordinaryBinding.installation.physicalId, architecture: 'x86_64'
  } };
  const sessionProjection: SessionProjection = { binding: session, target: observed(ordinaryBinding), liveIdentity: observed(true), readiness: observed('ordinary_spawned' as const) };
  const changed: OperationSnapshot = { ...clone(admitted), operationRevision: '3', state: { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'session_spawned', session: sessionProjection } } } };
  const changed15 = observe15('sc15-changed-with-session-receipt', changed);
  const terminalCancel = commandCase('sc15-cancel-already-terminal', 'SC-15', { name: 'cancel_operation', input: { operationId: admitted.operationId, expectedOperationRevision: '3' } }, { name: 'cancel_operation', output: { kind: 'already_terminal', operation: changed } });
  trace('sc15-too-late-observes-session-receipt', 'SC-15', [...base15(), running15, tooLate, changed15, terminalCancel, ready15]);
  for (const kind of ['no_change', 'failed', 'rolled_back'] as const) {
    const value: OperationSnapshot = { ...clone(admitted), operationRevision: '3', state: { status: 'completed', outcome: kind === 'failed'
      ? { kind, error: error('native_unavailable', 'after_user_choice') }
      : { kind, reason: kind === 'no_change' ? 'already_satisfied' : 'rollback_completed' } } };
    const terminal = observe15(`sc15-terminal-${kind.replaceAll('_', '-')}`, value);
    trace(`sc15-honest-${kind.replaceAll('_', '-')}-close`, 'SC-15', [...base15(), running15, terminal, ready15]);
  }
  const backwards = observe15('sc15-regressed-running', { ...clone(running), operationRevision: '1' });
  trace('sc15-refuse-revision-regression', 'SC-15', [...base15(), running15, backwards], { accepted: false, code: 'transcript_revision_regressed', step: 4 });
  const reused = observe15('sc15-reused-revision-state', { ...clone(running), operationRevision: '1' });
  trace('sc15-refuse-revision-reuse', 'SC-15', [...base15(), reused], { accepted: false, code: 'transcript_revision_reused', step: 3 });
  const resurrected = observe15('sc15-resurrected-terminal', { ...clone(running), operationRevision: '4' });
  trace('sc15-refuse-terminal-resurrection', 'SC-15', [...base15(), running15, changed15, resurrected], { accepted: false, code: 'transcript_terminal_changed', step: 5 });
  const wrongCancel = commandCase('sc15-wrong-operation-cancel', 'SC-15', { name: 'cancel_operation', input: { operationId: nextId(), expectedOperationRevision: '2' } }, { name: 'cancel_operation', output: { kind: 'too_late', operation: running } });
  trace('sc15-refuse-cross-operation-cancel', 'SC-15', [...base15(), running15, wrongCancel], { accepted: false, code: 'transcript_operation_binding', step: 4 });
  const recoveryRef: RecoveryRef = { operationId: admitted.operationId, transaction: 'synthetic-native-transaction-1', target: { kind: 'launch', target: ordinaryBinding } };
  const recovery: OperationSnapshot = { ...clone(admitted), operationRevision: '3', state: { status: 'recovery_required', reason: 'interrupted_transaction', recovery: recoveryRef } };
  const recovery15 = observe15('sc15-post-restart-recovery', recovery);
  const recoveryClose = commandCase('sc15-close-recovery-required', 'SC-15', { name: 'request_host_close', input: { expectedCursor: restart.cursor } }, { name: 'request_host_close', output: { kind: 'recovery_required', recoveries: [recoveryRef] } });
  const recoveryCancel = commandCase('sc15-cancel-recovery-required', 'SC-15', { name: 'cancel_operation', input: { operationId: admitted.operationId, expectedOperationRevision: '3' } }, { name: 'cancel_operation', output: { kind: 'recovery_required', operation: recovery } });
  const recoveryBusy = rejectionCase('sc15-conflicting-work-needs-recovery', 'SC-15', { name: 'commit', input: { ...clone(commitInput), idempotencyKey: nextId() } }, { ...error('recovery_required', 'after_recovery'), recovery: recoveryRef });
  // Recovery refusal is its own valid projection. The old-host gate remains
  // explicit; do not pretend this checker performs native journal arbitration.
  trace('sc15-forced-restart-observes-recovery', 'SC-15', [...base15(), running15, restart, recovery15, recoveryCancel, recoveryClose]);
  const wrongRecoveryClose = commandCase('sc15-close-substituted-recovery', 'SC-15', { name: 'request_host_close', input: { expectedCursor: restart.cursor } }, { name: 'request_host_close', output: {
    kind: 'recovery_required', recoveries: [{ ...clone(recoveryRef), transaction: 'synthetic-other-transaction' }]
  } });
  trace('sc15-refuse-recovery-substitution', 'SC-15', [...base15(), running15, restart, recovery15, wrongRecoveryClose], { accepted: false, code: 'transcript_close_recovery', step: 6 });
  const snapshot15 = (items: OperationSnapshot[]): Snapshot => ({ cursor, preferences: unavailable(), profiles: unavailable(), installations: unavailable(),
    sessions: unavailable(), capabilities: inventory([]), operations: inventory(items) });
  const emptySnapshot = queryCase('sc15-complete-empty-snapshot', 'SC-15', { name: 'snapshot', input: {} }, { name: 'snapshot', output: snapshot15([]) });
  trace('sc15-refuse-complete-snapshot-omitted-admission', 'SC-15', [...base15(), emptySnapshot], { accepted: false, code: 'transcript_snapshot_operation_missing', step: 3 });
  trace('sc15-refuse-complete-snapshot-omitted-running', 'SC-15', [...base15(), running15, emptySnapshot], { accepted: false, code: 'transcript_snapshot_operation_missing', step: 4 });
  trace('sc15-refuse-complete-snapshot-omitted-recovery', 'SC-15', [...base15(), running15, recovery15, emptySnapshot], { accepted: false, code: 'transcript_snapshot_operation_missing', step: 5 });
  trace('sc15-refuse-complete-snapshot-omitted-cancellation', 'SC-15', [...base15(), running15, requestCancel, emptySnapshot], { accepted: false, code: 'transcript_snapshot_operation_missing', step: 5 });
  const completeRunning = queryCase('sc15-complete-running-snapshot', 'SC-15', { name: 'snapshot', input: {} }, { name: 'snapshot', output: snapshot15([running]) });
  trace('sc15-complete-snapshot-retains-pending-operation', 'SC-15', [...base15(), running15, completeRunning]);
  const completeTerminal = queryCase('sc15-complete-terminal-snapshot', 'SC-15', { name: 'snapshot', input: {} }, { name: 'snapshot', output: snapshot15([changed]) });
  trace('sc15-complete-snapshot-explicit-terminal-transition', 'SC-15', [...base15(), running15, completeTerminal, emptySnapshot]);
  trace('sc15-complete-snapshot-omits-observed-terminal', 'SC-15', [...base15(), running15, changed15, emptySnapshot]);
  const partialSnapshot = snapshot15([]);
  partialSnapshot.operations.completeness = 'partial';
  partialSnapshot.operations.issues = [{ code: 'native_unavailable', resource: { kind: 'operation', id: admitted.operationId } }];
  const partial = queryCase('sc15-explicit-partial-snapshot', 'SC-15', { name: 'snapshot', input: {} }, { name: 'snapshot', output: partialSnapshot });
  trace('sc15-partial-snapshot-declares-missing-observation', 'SC-15', [...base15(), running15, partial]);
  trace('sc15-refuse-complete-omission-after-partial', 'SC-15', [...base15(), running15, partial, emptySnapshot], { accepted: false, code: 'transcript_snapshot_operation_missing', step: 5 });
  trace('sc15-refuse-ready-close-after-partial', 'SC-15', [...base15(), running15, partial, ready15], { accepted: false, code: 'transcript_close_obligations', step: 5 });
  const undeclaredPartial = clone(partialSnapshot); undeclaredPartial.operations.issues = [];
  fixtures.push(refusalFixture('sc15-codec-refuse-undeclared-partial-inventory', 'SC-15', 'Partial operation inventory must declare explicit projection issues', 'reply', queryReply(nextId(), { name: 'snapshot', output: undeclaredPartial })));
  // Keep the typed refusal fixture in the catalog without falsely making a
  // stale-host request eligible to reach that native recovery decision.
  void recoveryBusy;
  const invalidRecovery = clone(recovery); if (invalidRecovery.state.status === 'recovery_required') invalidRecovery.state.recovery.operationId = nextId();
  fixtures.push(refusalFixture('sc15-codec-refuse-cross-operation-recovery', 'SC-15', 'Recovery must bind its own operation', 'reply', commandReply(nextId(), { name: 'commit', output: invalidRecovery })));
  fixtures.push(refusalFixture('sc15-codec-refuse-empty-close-obligation', 'SC-15', 'Deferred close must declare an obligation', 'reply', commandReply(nextId(), { name: 'request_host_close', output: { kind: 'deferred', obligations: [] } })));
  fixtures.push(refusalFixture('sc15-codec-refuse-requested-terminal-cancel', 'SC-15', 'Requested cancellation must carry a pending cancellation state', 'reply', commandReply(nextId(), { name: 'cancel_operation', output: { kind: 'requested', operation: changed } })));

  const maximum = observe15('sc15-maximum-operation-revision', { ...clone(admitted), operationRevision: maxCounter });
  trace('sc15-exact-maximum-revision', 'SC-15', [initial(), maximum, maximum]);
  const maximumEvent = eventCase('sc18-maximum-event-sequence', 'SC-18', at(maxCounter), { type: 'snapshot_invalidated', reason: 'catalog_changed' });
  trace('sc18-exact-maximum-cursor', 'SC-18', [{ type: 'boundary', reason: 'initial', cursor: at('18446744073709551614') }, maximumEvent]);
  for (const [hostKind, platform, architecture] of [
    ['windows_x64', 'windows', 'x86_64'], ['macos_arm64', 'macos', 'arm64'], ['macos_arm64', 'macos', 'x86_64']
  ] as const) {
    const suffix = `${platform}-${architecture.replaceAll('_', '-')}`;
    const hello = queryCase(`sc18-${suffix}-hello`, 'SC-18', { name: 'hello', input: {} }, { name: 'hello', output: {
      hostEpoch, hostKind, supportedVersions: [1], implementedCommands: ['prepare', 'commit', 'cancel_operation', 'request_host_close']
    } });
    const resolve = queryCase(`sc18-${suffix}-resolve`, 'SC-18', { name: 'resolve_target', input: { target: {
      installation: { kind: 'directory', directory: platform === 'windows'
        ? { platform, value: 'D:\\Synthetic\\STFC\\Game' } : { platform, value: '/Applications/SyntheticSTFC.app' } }, profile: { kind: 'ordinary' }
    } } }, { name: 'resolve_target', output: { target: observed(ordinaryBinding) } });
    const binding: SessionBinding = { ...clone(session), sessionId: nextId(), process: { ...session.process,
      startIdentity: { platform, value: `synthetic-${suffix}-generation` }, architecture } };
    const projection: SessionProjection = { ...clone(sessionProjection), binding };
    const sessions = queryCase(`sc18-${suffix}-sessions`, 'SC-18', { name: 'list_sessions', input: {} }, { name: 'list_sessions', output: observed(inventory([projection])) });
    const focusSemantics: PlanSemantics = { hashProfile: 'bridge-plan-semantic-json-v1', action: 'focus_session', trustDomain: 'session', effects: ['focus_session'],
      capture: { kind: 'focus_session', session: binding, target: ordinaryBinding } };
    const focusPlan = prepared(hooks, focusSemantics, sequence++);
    const focusPrepare = commandCase(`sc18-${suffix}-focus-prepare`, 'SC-18', { name: 'prepare', input: { intent: { kind: 'focus_session', input: { session: binding } } } }, { name: 'prepare', output: focusPlan });
    const focused = operation(focusPlan, sequence++, '1', { status: 'completed', outcome: { kind: 'changed', reason: 'applied', receipt: { kind: 'session_focused', session: binding } } });
    const focusCommit = commandCase(`sc18-${suffix}-focus-commit`, 'SC-18', { name: 'commit', input: { planRef: focusPlan.planRef, idempotencyKey: nextId() } }, { name: 'commit', output: focused });
    trace(`sc18-${suffix}-synthetic-host-binding`, 'SC-18', [initial(), hello, resolve, sessions, focusPrepare, focusCommit]);
  }
  trace('sc18-refuse-cross-client-request-correlation', 'SC-18', [initial(), {
    type: 'exchange', request: 'sc18-windows-x86-64-hello-request', reply: 'sc18-macos-arm64-hello-reply'
  }], { accepted: false, code: 'transcript_reply_binding', step: 1 });

  // Malformed ingress and semantically invalid DTOs never enter a transcript.
  const validIngress = queryRequest(nextId(), { name: 'hello', input: {} });
  const ingressJson = JSON.stringify(validIngress);
  const rawRefusal = (id: string, case_: string, raw: string | Uint8Array) => fixtures.push({ id, scenario: 'SC-18', case: case_, tags: ['strict-ingress', 'codec-refusal'], kind: 'request', raw, expectedWire: false, expectedSemantic: false });
  rawRefusal('sc18-ingress-root-duplicate', 'Duplicate envelope key refuses', ingressJson.replace('"protocolVersion":1', '"protocolVersion":1,"protocolVersion":1'));
  rawRefusal('sc18-ingress-nested-duplicate', 'Duplicate nested key refuses before materialization', ingressJson.replace('"input":{}', '"input":{"duplicate":1,"duplicate":2}'));
  rawRefusal('sc18-ingress-bom', 'UTF-8 BOM is not discarded', '\uFEFF' + ingressJson);
  rawRefusal('sc18-ingress-invalid-utf8', 'Invalid UTF-8 refuses before JSON', new Uint8Array([0xc3, 0x28]));
  rawRefusal('sc18-ingress-unpaired-surrogate', 'Unpaired JSON surrogate refuses', ingressJson.replace(validIngress.requestId, '\\ud800'));
  rawRefusal('sc18-ingress-trailing-message', 'Two concatenated messages refuse', ingressJson + ingressJson);
  rawRefusal('sc18-ingress-too-deep', 'Container depth beyond 32 refuses', '['.repeat(33) + 'null' + ']'.repeat(33));
  rawRefusal('sc18-ingress-too-large', 'Message beyond 256 KiB refuses', JSON.stringify('x'.repeat(256 * 1024 - 1)));
  for (const [suffix, token] of [['fraction', '1.0'], ['exponent', '1e0'], ['negative-zero', '-0'], ['unsafe-integer', '9007199254740993']] as const) {
    rawRefusal(`sc18-ingress-${suffix}`, 'Numeric token is never silently coerced', ingressJson.replace('"protocolVersion":1', `"protocolVersion":${token}`));
  }
  const wireRefusal = (id: string, payload: unknown, kind: 'request' | 'reply' | 'event' = 'request') => fixtures.push(refusalFixture(id, 'SC-18', id, kind, payload, false));
  wireRefusal('sc18-ingress-unknown-envelope-member', { ...validIngress, unexpected: true });
  wireRefusal('sc18-ingress-unsupported-version', { ...validIngress, protocolVersion: 2 });
  wireRefusal('sc18-ingress-version-string', { ...validIngress, protocolVersion: '1' });
  wireRefusal('sc18-ingress-top-level-array', [validIngress.protocolVersion, validIngress.requestId, validIngress.body]);
  wireRefusal('sc18-ingress-tagged-content-array', { ...validIngress, body: ['query', { name: 'hello', input: {} }] });
  wireRefusal('sc18-ingress-nested-struct-array', { protocolVersion: 1, requestId: nextId(), body: { type: 'query', query: { name: 'resume_events', input: { after: [hostEpoch, streamId, '0'], maximumEvents: '1' } } } });
  wireRefusal('sc18-ingress-unknown-query', { ...validIngress, body: { type: 'query', query: { name: 'invented_query', input: {} } } });
  wireRefusal('sc18-ingress-unknown-input-member', { ...validIngress, body: { type: 'query', query: { name: 'hello', input: { unexpected: true } } } });
  for (const [suffix, value] of [['counter-number', 1], ['counter-leading-zero', '01'], ['counter-overflow', '18446744073709551616']] as const) {
    wireRefusal(`sc18-ingress-${suffix}`, { protocolVersion: 1, cursor: { ...cursor, sequence: value }, body: { type: 'snapshot_invalidated', reason: 'catalog_changed' } }, 'event');
  }
  const duplicateHello = queryReply(nextId(), { name: 'hello', output: { hostEpoch, hostKind: 'windows_x64', supportedVersions: [1], implementedCommands: ['commit', 'commit'] } });
  fixtures.push(refusalFixture('sc18-codec-refuse-empty-action-request', 'SC-18', 'An action request must name at least one action', 'request', queryRequest(nextId(), { name: 'get_actions', input: { scope: { kind: 'target', target: ordinaryBinding }, actions: [] } })));
  fixtures.push(refusalFixture('sc18-codec-refuse-duplicate-command-advertisement', 'SC-18', 'Implemented command identities are unique', 'reply', duplicateHello));
  fixtures.push(refusalFixture('sc18-codec-refuse-unbound-success-reply', 'SC-18', 'A success reply needs a request identity', 'reply', { ...duplicateHello, requestId: null,
    body: { type: 'result', result: { type: 'query', query: { name: 'hello', output: { hostEpoch, hostKind: 'windows_x64', supportedVersions: [1], implementedCommands: [] } } } } }));
  fixtures.push(refusalFixture('sc18-codec-refuse-missing-supported-versions', 'SC-18', 'Unsupported protocol refusal declares supported versions', 'reply', { protocolVersion: 1, requestId: null, body: { type: 'rejected', error: error('unsupported_protocol', 'never') } }));
  const invalidMeasurement: OperationSnapshot = { ...clone(running), state: { status: 'running', progress: { ...progress, measurement: { unit: 'files', completed: '3', total: '2' } } } };
  fixtures.push(refusalFixture('sc18-codec-refuse-inverted-progress', 'SC-18', 'Progress cannot exceed its declared total', 'reply', commandReply(nextId(), { name: 'commit', output: invalidMeasurement })));
  fixtures.push(replyFixture('sc18-invalid-request-null-correlation', 'SC-18', 'Framing failure can have no trusted request identity', { protocolVersion: 1, requestId: null, body: { type: 'rejected', error: error('invalid_request', 'never') } }));
  fixtures.push(replyFixture('sc18-unsupported-version-null-correlation', 'SC-18', 'Version failure declares the supported version without inventing an ID', { protocolVersion: 1, requestId: null,
    body: { type: 'rejected', error: { ...error('unsupported_protocol', 'never'), supportedVersions: [1] } } }));

  // A complete close projection can contain both pending work and exact session
  // custody for each of the 64 retained operations. Exercise the wire bound in
  // replies and events independently of any synthetic journey or native owner.
  const fullClose: CloseObligation[] = Array.from({ length: 64 }, (_, index) => [
    { kind: 'operation' as const, operationId: syntheticId(30000 + index), operationRevision: '3' },
    { kind: 'session_custody' as const, session: { ...clone(session), sessionId: syntheticId(31000 + index),
      process: { ...session.process, pid: 12000 + index, startIdentity: { platform: 'windows' as const, value: `synthetic-full-close-${index}` } } } }
  ]).flat();
  fixtures.push(replyFixture('sc15-full-close-obligations-reply', 'SC-15', 'All 64 pending operations and session bindings fit the complete close reply',
    commandReply(nextId(), { name: 'request_host_close', output: { kind: 'deferred', obligations: fullClose } })));
  fixtures.push(eventFixture('sc15-full-close-obligations-event', 'SC-15', 'All 64 pending operations and session bindings fit the complete close event',
    protocolEvent(at('1'), { type: 'host_close_deferred', obligations: fullClose })));
  const excessClose = [...fullClose, fullClose[0]!];
  fixtures.push(refusalFixture('sc15-refuse-excess-close-obligations-reply', 'SC-15', 'The close reply refuses 129 obligations without truncation', 'reply',
    commandReply(nextId(), { name: 'request_host_close', output: { kind: 'deferred', obligations: excessClose } }), false));
  fixtures.push(refusalFixture('sc15-refuse-excess-close-obligations-event', 'SC-15', 'The close event refuses 129 obligations without truncation', 'event',
    protocolEvent(at('1'), { type: 'host_close_deferred', obligations: excessClose }), false));

  return { fixtures, transcripts };
}
