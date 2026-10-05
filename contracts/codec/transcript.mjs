// Pure relational checks over controlled synthetic transcripts. Every request,
// reply and event must already have passed schema AND Rust codec normalization.
// This is neither a dispatcher nor a schema validator. A "lost" reply is still
// an explicit modeled observation; it is not evidence that real work persisted.
// No check proves side-effect absence, native custody, real durability, expiry,
// permissions, or engine/native/release qualification.
export const MAX_TRANSCRIPT_STEPS = 4096;
export class TranscriptError extends Error {
  constructor(code, step) {
    super(code);
    this.name = 'TranscriptError';
    this.code = code;
    this.step = step;
  }
}
const own = (value, key) => Object.hasOwn(value, key);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);

export function checkTranscript(steps) {
  let index = -1;
  const reject = code => { throw new TranscriptError(code, index); };
  if (!Array.isArray(steps) || steps.length === 0 || steps.length > MAX_TRANSCRIPT_STEPS) reject('transcript_steps');
  const exact = (value, required, optional = []) => {
    if (!object(value) || required.some(key => !own(value, key))
      || Object.keys(value).some(key => !required.includes(key) && !optional.includes(key))) reject('transcript_step_shape');
  };
  // Generic comparison preserves array order. Semantic normalization follows
  // Rust's digest: effects is a set and only the restore capture's descriptive
  // backup.createdAt is excluded. Commit input and other timestamps stay exact.
  const canonical = (value, depth = 0) => {
    if (depth > 40) reject('transcript_not_normalized');
    if (value === null || typeof value === 'boolean' || typeof value === 'string') return JSON.stringify(value);
    if (typeof value === 'number' && Number.isSafeInteger(value) && !Object.is(value, -0)) return JSON.stringify(value);
    if (Array.isArray(value)) return '[' + Array.from(value, entry => canonical(entry, depth + 1)).join(',') + ']';
    if (object(value)) return '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key], depth + 1)).join(',') + '}';
    reject('transcript_not_normalized');
  };
  const equal = (left, right) => canonical(left) === canonical(right);
  const semanticsNormalized = semantics => {
    const normalized = { ...semantics, effects: [...semantics.effects].sort() };
    if (semantics.capture.kind === 'restore_configuration') {
      const backup = { ...semantics.capture.input.backup };
      delete backup.createdAt;
      normalized.capture = { ...semantics.capture, input: { ...semantics.capture.input, backup } };
    }
    return normalized;
  };
  const semanticsEqual = (left, right) => equal(semanticsNormalized(left), semanticsNormalized(right));
  const withNormalizedSemantics = value => ({ ...value, semantics: semanticsNormalized(value.semantics) });
  const counter = value => {
    if (typeof value !== 'string' || !/^(0|[1-9][0-9]{0,19})$/.test(value)) reject('transcript_counter');
    const parsed = BigInt(value);
    if (parsed > 18446744073709551615n) reject('transcript_counter');
    return parsed;
  };
  const cursorShape = value => {
    exact(value, ['hostEpoch', 'streamId', 'sequence']);
    if (typeof value.hostEpoch !== 'string' || !value.hostEpoch || typeof value.streamId !== 'string' || !value.streamId) reject('transcript_cursor');
    counter(value.sequence);
  };
  let cursor;
  const epochs = new Set();
  const streamSequences = new Map();
  const plans = new Map();
  const operations = new Map();
  const admissions = new Map();
  const summary = { boundaryCount: 0, exchangeCount: 0, eventCount: 0, operationCount: 0, lostReplyCount: 0 };
  const streamKey = value => canonical([value.hostEpoch, value.streamId]);

  function observeOperation(operation) {
    if (!object(operation) || typeof operation.operationId !== 'string' || !object(operation.semantics) || !object(operation.state)) reject('transcript_operation');
    const revision = counter(operation.operationRevision);
    const previous = operations.get(operation.operationId);
    if (previous) {
      if (!semanticsEqual(previous.semantics, operation.semantics)) reject('transcript_capture_changed');
      const priorRevision = counter(previous.operationRevision);
      if (revision < priorRevision) reject('transcript_revision_regressed');
      if (revision === priorRevision && !equal(withNormalizedSemantics(previous), withNormalizedSemantics(operation))) reject('transcript_revision_reused');
      if (previous.state.status === 'completed' && !equal(previous.state, operation.state)) reject('transcript_terminal_changed');
    }
    operations.set(operation.operationId, operation);
  }
  function matchInstallation(selector, binding) {
    if (selector.kind === 'registered' && (binding.kind !== 'registered'
      || selector.id !== binding.registrationId
      || own(selector, 'revisionAssertion') && selector.revisionAssertion !== binding.registrationRevision)) reject('transcript_prepare_target');
    // Directory selectors/assertions carry paths while bindings carry native
    // refs. Their physical equivalence is not derivable from this transcript.
  }
  function matchTarget(selector, binding) {
    matchInstallation(selector.installation, binding.installation);
    const requested = selector.profile, captured = binding.profile;
    if (requested.kind !== captured.kind) reject('transcript_prepare_target');
    if (requested.kind === 'isolated' && (requested.id !== captured.id
      || own(requested, 'revisionAssertion') && requested.revisionAssertion !== captured.revision)) reject('transcript_prepare_target');
    if (requested.kind === 'ordinary' && own(requested, 'catalogIdAssertion')
      && requested.catalogIdAssertion !== captured.ordinaryId) reject('transcript_prepare_target');
  }
  function matchPreparation(intent, semantics) {
    const input = intent.input, capture = semantics.capture;
    if (semantics.action !== intent.kind || capture.kind !== intent.kind) reject('transcript_prepare_action');
    if (['launch_ordinary', 'launch_isolated'].includes(intent.kind)) {
      matchTarget(input.target, capture.target);
      if ((input.unrecognizedRuntimeChoice ?? 'reject') !== (capture.unrecognizedRuntimeChoice ?? 'reject')) reject('transcript_prepare_choice');
      if (capture.runtime.kind === 'unrecognized' && capture.runtime.choice !== (input.unrecognizedRuntimeChoice ?? 'reject')) reject('transcript_prepare_choice');
      if (intent.kind === 'launch_isolated' && input.storeMode !== capture.storeMode) reject('transcript_prepare_store_mode');
    } else if (intent.kind === 'focus_session') {
      if (!equal(input.session, capture.session)) reject('transcript_prepare_session');
    } else if (intent.kind === 'create_profile') {
      const captured = capture.input;
      if (input.name !== captured.name || !equal(input.setup, captured.setup)
        || input.expectedCatalogRevision !== captured.catalogRevision) reject('transcript_prepare_input');
      matchInstallation(input.preferredInstallation, captured.preferredInstallation);
    } else if (intent.kind === 'register_installation') {
      if (input.name !== capture.input.name || input.expectedCatalogRevision !== capture.input.catalogRevision) reject('transcript_prepare_input');
      // The request's directory has no comparable path in this native capture.
    } else if (intent.kind === 'save_configuration') {
      if (!equal(input.draft, capture.input.draft.draft)) reject('transcript_prepare_input');
    } else if (!equal(input, capture.input)) reject('transcript_prepare_input');
  }
  function checkClose(disposition) {
    if (!object(disposition)) reject('transcript_close');
    const pending = [...operations.values()].filter(operation => operation.state.status !== 'completed');
    if (disposition.kind === 'ready') {
      if (pending.length) reject('transcript_close_obligations');
      return;
    }
    if (disposition.kind === 'deferred') {
      if (!Array.isArray(disposition.obligations) || !disposition.obligations.length) reject('transcript_close_obligations');
      const covered = new Set();
      const custody = new Set();
      for (const obligation of disposition.obligations) {
        if (obligation.kind === 'operation') {
          if (covered.has(obligation.operationId)) reject('transcript_close_obligations');
          covered.add(obligation.operationId);
          const operation = operations.get(obligation.operationId);
          if (operation && (operation.state.status === 'completed' || operation.operationRevision !== obligation.operationRevision)) reject('transcript_close_obligations');
        } else if (obligation.kind === 'session_custody') {
          const identity = canonical(obligation.session);
          if (custody.has(identity)) reject('transcript_close_obligations');
          custody.add(identity);
        } else reject('transcript_close_obligations');
      }
      if (pending.some(operation => !covered.has(operation.operationId))) reject('transcript_close_obligations');
      return;
    }
    if (disposition.kind === 'recovery_required') {
      if (!Array.isArray(disposition.recoveries) || !disposition.recoveries.length) reject('transcript_close_recovery');
      const covered = new Set();
      for (const recovery of disposition.recoveries) {
        if (covered.has(recovery.operationId)) reject('transcript_close_recovery');
        covered.add(recovery.operationId);
        const operation = operations.get(recovery.operationId);
        if (operation && (operation.state.status !== 'recovery_required' || !equal(operation.state.recovery, recovery))) reject('transcript_close_recovery');
      }
      if (pending.some(operation => operation.state.status !== 'recovery_required' || !covered.has(operation.operationId))) reject('transcript_close_recovery');
      return;
    }
    reject('transcript_close');
  }
  function boundary(step) {
    exact(step, ['type', 'reason', 'cursor']);
    cursorShape(step.cursor);
    const next = step.cursor;
    if (!cursor) {
      if (step.reason !== 'initial' || index !== 0) reject('transcript_boundary');
      epochs.add(next.hostEpoch);
    } else if (step.reason === 'restart') {
      if (epochs.has(next.hostEpoch) || next.streamId === cursor.streamId) reject('transcript_restart');
      epochs.add(next.hostEpoch);
      // Deliberately retain admitted mappings and operation observations. Plan
      // refs retain their old epoch so a fresh commit cannot use them afterward.
    } else if (['reconnect', 'resnapshot', 'retention_gap'].includes(step.reason)) {
      if (next.hostEpoch !== cursor.hostEpoch) reject('transcript_epoch');
      const prior = streamSequences.get(streamKey(next));
      if (prior !== undefined && counter(next.sequence) < prior) reject('transcript_sequence_regressed');
      if (step.reason === 'reconnect' && (next.streamId !== cursor.streamId || next.sequence !== cursor.sequence)) reject('transcript_reconnect');
      // Resnapshot/retention_gap may anchor a new stream or skip unseen events.
      // Operation revisions remain scoped to their operation and do not reset.
    } else reject('transcript_boundary');
    cursor = { ...next };
    streamSequences.set(streamKey(next), counter(next.sequence));
    summary.boundaryCount += 1;
  }
  function exchange(step) {
    exact(step, ['type', 'request', 'reply'], ['delivery']);
    if (own(step, 'delivery') && !['received', 'lost'].includes(step.delivery)) reject('transcript_delivery');
    const { request, reply } = step;
    if (!object(request) || !object(reply) || !object(request.body) || !object(reply.body)) reject('transcript_exchange');
    if (request.requestId !== reply.requestId || request.protocolVersion !== reply.protocolVersion) reject('transcript_reply_binding');
    const family = request.body.type;
    const invocation = request.body[family];
    if (!['query', 'command'].includes(family) || !object(invocation) || !object(invocation.input)) reject('transcript_exchange');
    const rejected = reply.body.type === 'rejected';
    const errorCode = rejected ? reply.body.error?.code : undefined;
    let output;
    if (!rejected) {
      const result = reply.body.result;
      if (reply.body.type !== 'result' || !object(result) || result.type !== family || result[family]?.name !== invocation.name) reject('transcript_result_variant');
      output = result[family].output;
      if (!object(output) && !Array.isArray(output)) reject('transcript_exchange');
    }
    if (family === 'command' && invocation.name === 'commit') {
      const input = invocation.input;
      if (!object(input.planRef) || typeof input.idempotencyKey !== 'string') reject('transcript_commit');
      const identity = canonical(input);
      const admitted = admissions.get(input.idempotencyKey);
      // Replay lookup precedes old-host plan lookup, including lost delivery.
      if (admitted) {
        if (identity !== admitted.input) {
          if (!rejected || errorCode !== 'idempotency_conflict') reject('transcript_idempotency_conflict');
        } else {
          if (rejected || output.operationId !== admitted.operationId) reject('transcript_replay_operation');
          observeOperation(output);
        }
      } else if (input.planRef.hostEpoch !== cursor.hostEpoch) {
        if (!rejected || errorCode !== 'plan_host_mismatch') reject('transcript_old_host_plan');
      } else if (!rejected) {
        const plan = plans.get(input.planRef.planId);
        if (!plan || !equal(plan.planRef, input.planRef)) reject('transcript_unobserved_plan');
        if (!semanticsEqual(plan.semantics, output.semantics)) reject('transcript_capture_changed');
        observeOperation(output);
        admissions.set(input.idempotencyKey, { input: identity, operationId: output.operationId });
      } else if (errorCode === 'idempotency_conflict') reject('transcript_unobserved_admission');
    } else if (!rejected && family === 'command' && invocation.name === 'prepare') {
      if (!object(output.planRef) || !object(output.semantics) || output.planRef.hostEpoch !== cursor.hostEpoch) reject('transcript_plan_binding');
      matchPreparation(invocation.input.intent, output.semantics);
      const prior = plans.get(output.planRef.planId);
      if (prior && !equal(withNormalizedSemantics(prior), withNormalizedSemantics(output))) reject('transcript_plan_changed');
      plans.set(output.planRef.planId, output);
    } else if (!rejected && family === 'query' && invocation.name === 'hello') {
      if (output.hostEpoch !== cursor.hostEpoch) reject('transcript_epoch');
    } else if (!rejected && family === 'query' && invocation.name === 'get_operation') {
      const observation = output.operation;
      if (!object(observation)) reject('transcript_operation');
      if (observation.status === 'observed') {
        if (observation.value?.operationId !== invocation.input.operationId) reject('transcript_operation_binding');
        observeOperation(observation.value);
      } else if (observation.status === 'missing' && [...admissions.values()].some(value => value.operationId === invocation.input.operationId)) reject('transcript_admitted_operation_missing');
    } else if (!rejected && family === 'query' && invocation.name === 'get_draft') {
      cursorShape(output.cursor);
      if (output.cursor.hostEpoch !== invocation.input.hostEpoch || output.cursor.hostEpoch !== cursor.hostEpoch
        || output.cursor.streamId !== cursor.streamId || !object(output.draft)) reject('transcript_draft_binding');
      if (output.draft.status === 'observed' && (output.draft.value?.draft?.draftId !== invocation.input.draftId
        || output.draft.value?.draft?.hostEpoch !== invocation.input.hostEpoch)) reject('transcript_draft_binding');
      // This per-draft read may be ahead of buffered global events. It cannot
      // advance their cursor or make an intervening operation event disappear.
    } else if (!rejected && family === 'query' && invocation.name === 'snapshot') {
      if (!equal(output.cursor, cursor)) reject('transcript_snapshot_cursor');
      // This inventory has no filter/pagination selector. A complete result
      // must account for all observed pending work, possibly with a terminal
      // transition. Partial results have explicit issues validated by Rust and
      // may omit observations, without erasing the prior relationship history.
      if (output.operations.completeness === 'complete') {
        const included = new Set(output.operations.items.map(operation => operation.operationId));
        for (const known of operations.values()) {
          if (known.state.status !== 'completed' && !included.has(known.operationId)) reject('transcript_snapshot_operation_missing');
        }
      }
      for (const operation of output.operations.items) observeOperation(operation);
    } else if (!rejected && family === 'query' && invocation.name === 'resume_events') {
      if (!equal(invocation.input.after, cursor) || !equal(output.after, cursor)) reject('transcript_resume_cursor');
      if (BigInt(output.events.length) > counter(invocation.input.maximumEvents)) reject('transcript_resume_limit');
      // Inline batch events use the same strict ordering as standalone events.
      // Replayed events are never silently deduplicated or sequence-coerced.
      for (const message of output.events) event({ type: 'event', event: message });
      if (!equal(output.next, cursor)) reject('transcript_resume_cursor');
    } else if (!rejected && family === 'command' && invocation.name === 'request_sensitive_input') {
      // Rust validates opaque references against the result's echoed binding.
      // This adds only the original-request relationship, including refusals
      // to capture; it proves nothing about protected native entry or storage.
      if (!equal(invocation.input, output.binding)) reject('transcript_sensitive_input_binding');
    } else if (!rejected && family === 'command' && invocation.name === 'request_export_destination') {
      // A captured destination is backend custody. Only the echoed preview
      // relationship is checked here, never an actual path or native grant.
      if (!equal(invocation.input, output.binding)) reject('transcript_export_destination_binding');
    } else if (!rejected && family === 'command' && invocation.name === 'cancel_operation') {
      const operation = output.operation;
      if (operation?.operationId !== invocation.input.operationId) reject('transcript_operation_binding');
      const prior = operations.get(invocation.input.operationId);
      if (prior && prior.operationRevision !== invocation.input.expectedOperationRevision) reject('transcript_cancel_revision');
      if (counter(operation.operationRevision) < counter(invocation.input.expectedOperationRevision)) reject('transcript_cancel_revision');
      const status = operation.state.status;
      const outcome = operation.state.outcome?.kind;
      const valid = {
        requested: status === 'cancellation_requested',
        cancelled_before_commit: status === 'completed' && outcome === 'cancelled_before_commit',
        too_late: status === 'running' || status === 'completed' && ['changed', 'no_change'].includes(outcome),
        already_terminal: status === 'completed',
        recovery_required: status === 'recovery_required'
      };
      if (!valid[output.kind]) reject('transcript_cancel_disposition');
      observeOperation(operation);
    } else if (!rejected && family === 'command' && invocation.name === 'request_host_close') {
      if (!equal(invocation.input.expectedCursor, cursor)) reject('transcript_close_cursor');
      checkClose(output);
    }
    summary.exchangeCount += 1;
    if (step.delivery === 'lost') summary.lostReplyCount += 1;
  }
  function event(step) {
    exact(step, ['type', 'event']);
    const message = step.event;
    if (!object(message) || !object(message.body)) reject('transcript_event');
    cursorShape(message.cursor);
    if (message.cursor.hostEpoch !== cursor.hostEpoch) reject('transcript_epoch');
    if (message.cursor.streamId !== cursor.streamId) reject('transcript_stream');
    if (counter(message.cursor.sequence) !== counter(cursor.sequence) + 1n) reject('transcript_sequence');
    cursor = { ...message.cursor };
    streamSequences.set(streamKey(cursor), counter(cursor.sequence));
    if (message.body.type === 'operation_changed') observeOperation(message.body.operation);
    else if (message.body.type === 'host_close_deferred') checkClose({ kind: 'deferred', obligations: message.body.obligations });
    else if (!['snapshot_invalidated', 'draft_changed'].includes(message.body.type)) reject('transcript_event');
    summary.eventCount += 1;
  }
  for (const [position, step] of steps.entries()) {
    index = position;
    if (!object(step)) reject('transcript_step_shape');
    // Copy only for comparison/observation; caller-owned objects are never edited.
    canonical(step);
    if (step.type === 'boundary') boundary(step);
    else {
      if (!cursor) reject('transcript_boundary');
      if (step.type === 'exchange') exchange(step);
      else if (step.type === 'event') event(step);
      else reject('transcript_step_type');
    }
  }
  summary.operationCount = operations.size;
  return Object.freeze(summary);
}
