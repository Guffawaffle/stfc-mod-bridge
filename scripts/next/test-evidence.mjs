import assert from 'node:assert/strict';
import path from 'node:path';

export function nodeTestEvidence(text, { root, file, requiredNames, required = { [file]: requiredNames } }) {
  const events = text.trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.ok(events.length > 0 && events.every(event => ['test:pass', 'test:fail', 'test:summary'].includes(event.type)));
  const summary = events.filter(event => event.type === 'test:summary' && !event.data.file);
  assert.equal(summary.length, 1, 'Node tests require one final machine-readable summary');
  const counts = summary[0].data.counts;
  assert.equal(summary[0].data.success, true);
  for (const key of ['failed', 'cancelled', 'skipped', 'todo']) assert.equal(counts[key], 0, `Node tests cannot qualify with ${key}`);
  assert.ok(Number.isSafeInteger(counts.tests) && counts.tests > 0 && counts.passed === counts.tests);
  assert.ok(!events.some(event => event.type === 'test:fail'));
  const passed = events.filter(event => event.type === 'test:pass' && event.data.details?.type === 'test');
  assert.equal(passed.length, counts.tests);
  for (const event of passed) {
    const observedFile = path.relative(root, event.data.file).replaceAll('\\', '/');
    assert.ok(Object.hasOwn(required, observedFile), 'Node evidence contains an unexpected test file');
    assert.ok(!event.data.skip && !event.data.todo);
  }
  for (const [expectedFile, names] of Object.entries(required)) {
    for (const name of names) assert.ok(passed.some(event => event.data.name === name
      && path.relative(root, event.data.file).replaceAll('\\', '/') === expectedFile), `Node criterion test did not execute: ${name}`);
  }
  return { tests: counts.tests, passedNames: passed.map(event => event.data.name) };
}

export function vitestEvidence(report, { root, required }) {
  assert.equal(report.success, true);
  for (const key of ['numFailedTests', 'numPendingTests', 'numTodoTests', 'numFailedTestSuites', 'numPendingTestSuites']) {
    assert.equal(report[key], 0, `Vitest cannot qualify with ${key}`);
  }
  assert.ok(Number.isSafeInteger(report.numTotalTests) && report.numTotalTests > 0);
  assert.equal(report.numPassedTests, report.numTotalTests);
  assert.ok(Array.isArray(report.testResults) && report.testResults.length > 0);
  const files = new Map();
  let total = 0;
  for (const result of report.testResults) {
    assert.equal(result.status, 'passed');
    const file = path.relative(root, result.name).replaceAll('\\', '/');
    assert.ok(!files.has(file), 'Vitest file inventory must be unique');
    assert.ok(Array.isArray(result.assertionResults) && result.assertionResults.length > 0);
    for (const assertion of result.assertionResults) assert.equal(assertion.status, 'passed', `Vitest test did not execute: ${assertion.title}`);
    const titles = result.assertionResults.map(assertion => assertion.title);
    files.set(file, titles); total += titles.length;
  }
  assert.equal(total, report.numTotalTests, 'Vitest summary must match actual assertions');
  for (const [file, titles] of Object.entries(required)) {
    const passed = files.get(file);
    assert.ok(passed, `Required frontend test file did not execute: ${file}`);
    for (const title of titles) assert.ok(passed.includes(title), `Frontend criterion test did not execute: ${title}`);
  }
  return { tests: total, files: [...files].map(([file, titles]) => ({ file, tests: titles.length })) };
}

export const frontendCriteria = {
  'ui/tests/client-configuration-completion.test.ts': [
    'completed Save then exact DraftChanged adopts the receipt baseline and clears only captured intent',
    'an early clean read defers without a watermark and later receipt reconciliation consumes every event',
    'an early NoChange cleanup defers edits and its watermark until the exact completion arrives',
    'an explicitly requested empty staging successor reconciles lost ACK at the captured old baseline',
    ...['document_revision', 'file_identity'].map(reuse => `a Changed receipt cannot reuse the captured ${reuse}`),
    ...['save_configuration', 'restore_configuration'].map(kind =>
      `NoChange ${kind} cannot claim candidate bytes different from the captured baseline`),
    ...['no_receipt', 'document_id', 'target', 'schema', 'digest', 'backup', 'action', 'domain', 'reason'].map(mismatch =>
      `a schema-valid completed Save with mismatched ${mismatch} cannot authorize baseline adoption`),
    ...['revision', 'state', 'edits', 'schema', 'document'].map(mismatch =>
      `a completed receipt cannot clear intent using a mismatched clean successor ${mismatch}`),
    ...['typed_edits', 'unfinished_input', 'changed_generation', 'reentrant_input'].map(change =>
      `receipt reconciliation preserves newer local ${change}`),
    ...['changed', 'no_change'].map(kind =>
      `exact ${kind} completion retires synchronized protected refs without transferring them`),
    'same-host completed Save retains queued navigation until the exact cleaned successor arrives',
    'identical clean no-change captures remain correlated to the current Save operation ID',
    ...['discard', 'missing'].map(removal => `receipt evidence cannot resurrect a draft removed by ${removal}`),
    'same-host reconnect uses the retained exact completion without replaying Save or reopening a document',
    'Restore completion preserves stale draft bindings and protected intent rather than adopting its receipt baseline',
    ...['refused', 'old_dirty'].map(failure =>
      `facade retains Save custody on a ${failure} completion read and retries observation without another mutation`)
  ],
  'ui/tests/client-calls.test.ts': [
    ...['open_draft', 'set_draft_changes', 'request_sensitive_input', 'request_export_destination'].flatMap(method => [
      `typed binding echo accepts Rust None equivalence: ${method}`,
      `typed binding echo still refuses actual id changes: ${method}`,
      `typed binding echo still refuses actual revision changes: ${method}`,
      `typed binding echo still refuses actual document changes: ${method}`
    ]),
    'typed binding equivalence remains bounded and refuses accessor conversion',
    'preparation refuses a schema-valid reply captured for another requested profile',
    'first commit refuses schema-valid semantics unrelated to its captured review digest',
    'directory selector may resolve to an owner-captured registered installation',
    'preparation treats nullable assertions and explicit default reject as Rust DTO defaults',
    'semantic digest matches every Rust-approved shared prepared plan',
    'semantic normalization schema assumptions stay closed and optional',
    'commit digest accepts only declared set timestamp and serde normalization exceptions',
    'digest completion after timeout abort or disposal cannot admit replay identity',
    'unavailable semantic hashing is a sanitized local fault and retains uncertain commit input',
    'all accepted generated method pairs cross the same raw boundary',
    'domain rejection preserves generated error and recovery instead of creating local failure',
    'unavailable production binding is explicit and never chooses a mock',
    'abort before send prevents dispatch; abort afterward abandons only observation',
    'timeout retains exact commit replay without cancel or a new preparation',
    'replay key conflict and replay/pending bounds refuse dispatch',
    'reentrant replay reserves new submission custody before an older unsent settlement',
    'late old admission cannot change a forgotten and replaced equal-input replay',
    'duplicate injected ID refuses; late first request cannot settle a later call',
    'adapter exceptions and malformed frames are sanitized',
    'validated subscriptions are bounded and malformed event stops only its observer',
    'synchronous first event can dispose client and release the returned raw observer exactly once',
    'reentrant subscription cannot bypass reserved subscription capacity',
    'synchronous fault retains registration custody until raw disposer is available',
    'admitted replay cannot substitute a different operation identity',
    'draft results must echo exact captured document and draft binding'
  ],
  'ui/tests/client-wire.test.ts': [
    'capture detaches and deeply freezes data, preserving safe prototype keys',
    'accessors and serialization hooks never execute',
    'request capture uses the accepted parser and exact generated request validator',
    'local boundary diagnostics contain only safe codes'
  ],
  'ui/tests/client-draft-reconciliation.test.ts': [
    'a current draft read fences only draft payloads and consumes every intervening operation event',
    'Missing after eventless Discard creates a tombstone even at the same cursor',
    'Missing and Discard both suppress old buffered draft payload while consuming its sequence',
    'an older read cannot regress a newer draft event or replace its watermark with Missing',
    'Missing watermarks share the bounded draft identity budget',
    'an unchanged authoritative draft preserves unsynchronized edits and unfinished numeric input',
    'a read cannot automatically clear already synchronized protected references',
    'current successor reconciliation recovers matching lost public stage ACK without opening or changing a baseline',
    ...['clean', 'dirty'].flatMap(state => ['event', 'read'].map(route =>
      `the real same-revision ${state} to stale ${route} keeps draft and protected custody while the global stream remains consumable`)),
    'unwatermarked draft payloads cannot assert a same-revision stale transition',
    'a stale read cannot rewrite an observed draft at the exact same read watermark',
    ...['edits', 'schema', 'apply', 'document', 'resurrection'].map(change =>
      `same-revision stale allowance still refuses changed ${change} custody`),
    ...['newer_event', 'missing', 'discard', 'sequence_gap', 'disconnected', 'stream_changed'].map(change =>
      `synchronous ${change} observation cannot certify an obsolete retained-draft read`)
  ],
  'ui/tests/client-observation.test.ts': [
    'draft observations accept typed None equivalence without erasing local edits or document identity',
    'terminal operation state accepts omitted None while preserving actual receipt revisions',
    'recovery close accepts typed None equivalence while retaining exact transaction binding',
    'save reconciles typed DraftRef None but keeps captured edits exact',
    'deferred close covers safe recovery together with session custody or another running operation',
    'operation capture equivalence uses the same optional and default serde normalization',
    'pending operation remains captured across partial snapshot and complete omission faults',
    'complete snapshot can explicitly account for pending work with terminal transition',
    'terminal state stays stable, exact maximum operation revision is accepted',
    'consecutive events, identical duplicate, contradictory duplicate and bounded history',
    'snapshot watermark discards buffered prior events and applies only consecutive later events',
    'u64 maximum event sequence compares exactly within scope',
    'invalidation and hello epoch change demand a fresh snapshot without erasing operations',
    'resume binding, maximum count and consecutive event checks refuse gaps/replay',
    'operation/draft bounds refuse new state while preserving retained observations',
    'close ready and stale close obligations cannot erase pending or partial evidence',
    'snapshot subscription precedes query and disconnected late snapshot cannot certify stream',
    'synchronous subscription failure cannot anchor a snapshot and next start registers a fresh observer',
    'synchronous registration events stay buffered until the authoritative snapshot watermark',
    'session setup guards reentrant start and disposal before raw subscription returns',
    'old resume reply cannot invalidate a new epoch snapshot after reconnect',
    'disposed session abandons a pending resume observation without reconciling it',
    'discard receipt removes only the exact observed draft and retains bounded revision history',
    'older discard receipt cannot remove a newer observed draft revision',
    'view navigation preserves draft and one target transition; failed save retains everything',
    'discard requires exact backend draft acknowledgement and only applies queued transition',
    'save applies selection only after exact reviewed draft has completed authoritatively',
    'known host invalidation freezes typed public and protected edits while retaining exact custody',
    'old host draft synchronization cannot adopt a matching late acknowledgement',
    'old host Discard acknowledgement cannot clear exact typed edits or raw numeric text',
    'completed admitted Save remains reconcilable after its draft host is replaced',
    'replacement host clean drafts restore edit capability without replacing retained dirty drafts',
    'ordinary document conflicts remain distinct from a replaced draft host'
  ],
  'ui/tests/mock-clock.test.ts': [
    'deadlines and ties retain insertion order including nested schedules',
    'cancellation and reset remove old tasks and reset elapsed time',
    'a stale cancellation handle cannot remove a task scheduled after reset',
    'bounds reject runaway scheduling, invalid times, and reentrant advances'
  ],
  'ui/tests/mock-transport.test.ts': [
    'accepted shared transcripts preserve every frame, correlation slot, boundary, and source digest',
    'request matching changes only declared correlation, preserving command inputs and detached scripts',
    'abort before dispatch sends nothing; abort after dispatch leaves backend events and replay record',
    'lost admitted reply survives restart and declared exact replay returns original operation',
    'injected lost reply times out locally while the next script step remains available',
    'a scripted busy refusal retains its error and does not claim an admitted replay record',
    'same-epoch reconnect, retention refusal and resnapshot expose only declared observations',
    'malformed reply/event fault probes use common validation without rewriting golden frames',
    'reset removes old observers and tasks while dispose never executes further work',
    'an event observer can reset without delivering to old observers or advancing the old script',
    'an event observer can dispose without delivering to old observers or advancing the old script',
    'an observer reset can immediately dispatch new work without the old event changing it',
    'a disconnect observer can reset without notifying the old fault snapshot',
    'a disconnect observer can dispose without notifying the old fault snapshot',
    'a state observer can reset without continuing the old notification snapshot',
    'a state observer can dispose without continuing the old notification snapshot',
    'nested state changes supersede an older notification within the same scenario',
    'an observer removed synchronously by an earlier observer does not receive the event',
    'script, observer and frame bounds fail visibly without dispatch or unscripted success'
  ]
};
