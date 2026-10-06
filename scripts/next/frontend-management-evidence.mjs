import assert from 'node:assert/strict';
import path from 'node:path';
import { vitestEvidence } from './test-evidence.mjs';

// Author-frozen semantic identities, independent of whatever a later run reports.
// Parameterized names are expanded here from their reviewed, closed inputs.
const diagnosticNames = ['sc16-redacted-preview', 'sc16-explicit-path-disclosure'];
const cancellations = [
  ['sc15-cancel-requested', 'requested'], ['sc15-cancel-before-commit', 'cancelled_before_commit'],
  ['sc15-cancel-too-late', 'too_late'], ['sc15-cancel-already-terminal', 'already_terminal'],
  ['sc15-cancel-recovery-required', 'recovery_required'],
];
const diagnostic = [
  ...diagnosticNames.map(name => `actual Rust diagnostic golden digest and client reply agree: ${name}`),
  ...['prefix_missing_nul', 'wrong_domain', 'content_only', 'wrong_digest'].map(name => `refuses a shape-valid wrong diagnostic digest profile: ${name}`),
  ...['content_paths', 'path_profile', 'target_profile', 'fact_profile', 'reference_profile', 'issue_resource', 'input_assertions'].map(name => `known Rust diagnostic Option None is equivalent: ${name}`),
  ...['registration', 'registration_revision', 'ordinary_id', 'isolated_profile', 'reference_target', 'reference_disclosure', 'content_disclosure', 'redacted_paths', 'target_fact'].map(name => `refuses diagnostic target/disclosure/fact relationship drift: ${name}`),
  'directory selector may resolve to the golden registered binding without frontend physical authority',
  'in-scope session/runtime facts from actual golden DTOs cross the client',
  ...['session_installation', 'runtime_physical', 'runtime_profile'].map(name => `fresh digest cannot approve foreign observed fact scope: ${name}`),
  ...['fact_order', 'issue', 'evidence_time', 'session_revision', 'session_pid', 'runtime_digest', 'disclosed_path'].map(name => `diagnostic digest retains exact fact/path/evidence content: ${name}`),
  'Unicode path bytes hash with JSON escaping while the digest excludes no path content',
  'actual golden Bridge binding may appear as an observed diagnostic fact',
  ...['empty_payloads', 'missing_application', 'duplicate_role', 'zero_size', 'windows_arm64', 'macos_x64'].map(name => `refuses a fresh-hashed shape-valid invalid Bridge diagnostic fact: ${name}`),
  ...['plaintext', 'required_null'].map(name => `generated diagnostic boundary rejects arbitrary content normalization: ${name}`),
  ...['timeout', 'observational_abort', 'disposed'].map(name => `diagnostic hashing cannot publish after ${name}`),
  'pending capacity remains occupied until diagnostic digest verification finishes',
  'failed diagnostic hashing becomes a sanitized local fault',
  'unavailable WebCrypto remains a sanitized local diagnostic fault',
  ...cancellations.map(([name]) => `actual Rust cancellation disposition remains correlated: ${name}`),
  ...cancellations.map(([name]) => `cancellation refuses another operation: ${name}`),
  ...cancellations.map(([name]) => `cancellation refuses a regressed observed revision: ${name}`),
  ...['embedded_operation', 'physical_target', 'profile_target'].map(name => `correct outer cancellation identity cannot approve foreign recovery custody: ${name}`),
  'recovery target binding retains Rust None equivalence to captured operation scope',
  ...cancellations.flatMap(([name, original]) => cancellations.map(([, kind]) => kind).filter(kind => kind !== original
    && !(original === 'cancelled_before_commit' && kind === 'already_terminal')
    && !(original === 'already_terminal' && kind === 'too_late')).map(kind => `cancellation kind ${kind} contradicts the observed state in ${name}`)),
  'too_late may honestly observe the already completed golden changed outcome',
  'already_terminal may honestly observe the golden cancelled-before-commit outcome',
  'cancellation compares decimal revisions above JavaScript integer precision exactly',
  'maximum Rust operation revision remains a lossless string and never wraps',
];
const criteria = {
  'ui/tests/components/cancellation.test.ts': [
    'cancellation captures exact observed revision and survives later UI target selection',
    'duplicate pending cancellation cannot send another request or erase its busy state',
    ...['unobserved', 'old_revision', 'different_capture', 'different_state', 'stale_inventory', 'already_requested', 'completed', 'recovery'].map(name => `cancellation refuses ${name} without sending`),
    'cancellation rejects a reply with substituted plan capture before it reaches the observation store',
    ...['host_restart', 'stale_inventory'].map(name => `cancellation does not adopt an otherwise valid result after ${name}`),
    'a newer terminal event is retained when an older cancellation reply arrives',
    'same-revision cancellation contradiction requires reconciliation',
    ...['cancelled_before_commit', 'too_late', 'already_terminal', 'recovery_required'].map(name => `cancellation reports actual ${name} disposition`),
    'disposal aborts only observation and cannot install a late cancellation claim',
  ],
  'ui/tests/components/recovery-custody.test.ts': [
    'recovery observation retains original replay and allows a separate exact modeled review',
    'a recovery completion releases its own replay while original custody awaits original terminal reconciliation',
    ...['old_revision', 'substituted_recovery', 'unobserved', 'unsupported_recovery', 'stale_inventory'].map(name => `recovery review refuses ${name} while retaining original replay`),
    'rejected recovery preparation preserves the original operation and exact replay',
    'disposal of a retained recovery cannot forget its durable submission',
  ],
  'ui/tests/action-custody-adversarial.test.ts': [
    'known host replacement prevents fresh generic preparation without outbound work',
    'host replacement during generic preparing publication prevents outbound preparation',
    'matching generic preparation after known host replacement cannot install review',
    'known host replacement prevents fresh generic confirmation without replay custody',
    'host replacement during generic admitting publication prevents fresh commit',
    'obsolete generic review releases its proved-unsent replay before a new host admission',
    'obsolete generic review cleanup preserves a foreign replacement replay capture',
    ...['pending', 'uncertain', 'admitted', 'not_sent'].map(name => `proved-unsent generic cleanup retains a shared later submission: ${name}`),
    'a later listener cannot redisplay review after a reentrant Stay closes it',
    'a replacement listener mounted during nested publication does not receive the superseded review',
    ...['preparing', 'admitting', 'cancelling'].map(name => `synchronous disposal during ${name} publication prevents its outbound invocation`),
    'an original in-flight admission cannot adopt after authoritative host replacement',
    'an original in-flight admission cannot adopt after inventory confidence is lost',
    'a terminal reply from an in-flight old-host query cannot retire replay in the replacement epoch',
    'a newly requested exact replay after a host replacement still observes the original durable identity',
    ...['active', 'parked', 'recovery'].map(name => `terminal reconciliation of ${name} custody cannot forget an equal-input replacement capture`),
    'original terminal reconciliation during recovery admission releases only original custody',
  ],
  'ui/tests/client-diagnostic-integrity.test.ts': diagnostic,
  'ui/tests/management/controller.test.ts': [
    'duplicate display names retain immutable profile references and expected preferences',
    'catalog mutation uses explicit epoch-bound canonical revision and never inventory revision',
    'profile preference edits refuse forged installations and stale keep assertions',
    'profile lifecycle refuses stale references and ordinary deletion',
    'profile review captures exact row and stops before commit',
    'availability for one observed profile cannot prepare another duplicate-name profile',
    'late availability from an old target selection cannot enable review',
    'native import discovery binds destination owner and refuses foreign or inaccessible sources',
    'history and restore stay bound to the current document and retained exact backup',
    'history refuses any foreign target or producer receipt',
    'runtime release and configuration use exact observed target ownership',
    'runtime ownership from another target or host cannot enable managed operations',
    'a different checked runtime distribution requires explicit source-switch capture and cannot use update or repair',
    'runtime migration cannot substitute a schema that differs from the checked release',
    'game check from a foreign installation or host never becomes an update intent',
    'Bridge checks require explicit current application and reject a foreign checked host',
    'caller observation abort is retained rather than replaced by controller lifecycle',
    'superseded read publication cannot issue work after a synchronous target change',
    'operation refresh observes the exact row and retains a newer event instead of regressing it',
    'unsupported retained recovery has no invented generic command',
    'Bridge recovery review derives the exact recorded journal, independent of selected target',
  ],
  'ui/tests/management/support.test.ts': [
    'verified redacted preview and backend destination stop at shared review without an export write',
    'explicit disclosure change clears destination and review; paths require a fresh verified preview',
    'late native destination after disclosure change cannot revive export authority',
    'destination cancellation and unavailability leave preview but produce no export intent',
    'invalid content digest cannot reach preview, destination selection or export review',
    'preview from another physical binding cannot replace the selected resolved target',
    'target changes and disposal abandon old diagnostic observations',
    'a new host resets explicit path disclosure and captured destination',
    'changing disclosure while preparation is in flight cannot expose the old export review',
    'reentrant disclosure publication does not deliver a stale busy state or issue an old query',
    'redacted public diagnostic rows never render native custody or filesystem paths',
    'optional-none target binding differences preserve the verified diagnostic scope',
  ],
  'ui/tests/management/presentation.test.ts': [
    'all terminal outcomes have distinct honest presentation',
    'captured source-switch summary preserves runtime and configuration participants with explicit warning',
    'configuration review excludes protected reference custody and public value payloads',
    'backup route distinguishes exact target, document and producer while allowing historical baseline',
    'Bridge review shows offered release and actual package identities without inventing current version',
    'unavailable and partial inventories never read as a complete empty catalog',
    'management availability preserves blocked active session public feedback',
    'management availability preserves blocked interrupted transaction public feedback',
    'management availability preserves unknown identity public feedback',
    'management availability preserves unavailable native route public feedback',
  ],
  'ui/tests/management/observation.test.ts': [
    'late superseded availability failure cannot replace the newer successful read message',
    'late superseded availability success cannot clear the newer failed read message',
    'partially superseded availability updates only retained actions and preserves the newest message',
    'a reentrant newer availability read prevents a fully superseded outbound request',
    'a replaced document baseline clears old complete backup history',
    'changed provider preference invalidates an in-flight old release check',
    'null and omitted optional profile preferences preserve exact observed identity',
  ],
  'ui/tests/management-preview/session.test.ts': [
    'Management mock current-draft queries retain exact namespace and cursor with truthful Missing and foreign-host refusal',
    'Management preview modes validate every golden provenance hash and recorded wire frame',
    'Management preview duplicate profile review preserves immutable identity and future-launch preference',
    'Management preview native import discovery uses exact observed destination owner and explicit approval',
    'Management preview archive admission remains nonterminal until exact row completion is observed',
    'Management preview updater routes preserve distinct runtime game and Bridge captured authorities',
    'Management preview history uses the shared exact document and retained backup without a parallel draft',
    'Management preview cancellation binds the observed row and preserves requested versus too late outcomes',
    'Management preview retained recovery derives only recorded game and Bridge intents independent of current selection',
    'Management preview diagnostics bind disclosure preview chooser and review while excluded fields remain absent',
    'Management preview cancelled destination and absent metadata do not create review authority',
    'Management preview closed request budget refuses foreign document reads and disposal rejects owned pending observations',
  ],
};
for (const [file, titles] of Object.entries(criteria)) {
  assert.ok(file.startsWith('ui/tests/') && titles.length > 0);
  assert.ok(titles.every(title => typeof title === 'string' && title.length > 0));
  assert.equal(new Set(titles).size, titles.length, `Duplicate authored Management criterion: ${file}`);
  Object.freeze(titles);
}
export const managementCriteria = Object.freeze(criteria);
export const managementCounts = Object.freeze({ tests: 206, files: 9 });
assert.equal(Object.keys(managementCriteria).length, managementCounts.files);
assert.equal(Object.values(managementCriteria).reduce((total, titles) => total + titles.length, 0), managementCounts.tests);

export function managementEvidence(report, root) {
  const inventory = vitestEvidence(report, { root, required: managementCriteria });
  assert.equal(inventory.files.length, managementCounts.files, 'Management evidence cannot add or substitute test files');
  assert.equal(inventory.tests, managementCounts.tests, 'Management evidence must execute every bound assertion exactly once');
  for (const result of report.testResults) {
    const file = path.relative(root, result.name).replaceAll('\\', '/');
    assert.ok(Object.hasOwn(managementCriteria, file));
    const titles = result.assertionResults.map(assertion => assertion.title);
    assert.equal(new Set(titles).size, titles.length, `Management assertion identities must be unique: ${file}`);
    assert.deepEqual([...titles].sort(), [...managementCriteria[file]].sort(), `Management semantic inventory changed: ${file}`);
  }
  return { ...inventory, criteria: managementCriteria };
}

// Synthetic report controls validate the receipt parser, never the UI behavior.
export function managementEvidenceControls(root) {
  const baseline = () => ({ success: true, numFailedTests: 0, numPendingTests: 0, numTodoTests: 0,
    numFailedTestSuites: 0, numPendingTestSuites: 0,
    numTotalTests: Object.values(managementCriteria).reduce((total, titles) => total + titles.length, 0),
    numPassedTests: Object.values(managementCriteria).reduce((total, titles) => total + titles.length, 0),
    testResults: Object.entries(managementCriteria).map(([file, titles]) => ({ name: path.join(root, file), status: 'passed', assertionResults: titles.map(title => ({ title, status: 'passed' })) })),
  });
  managementEvidence(baseline(), root);
  const controls = [['complete unique closed inventory', 'accepted']];
  function refused(name, change) { const report = baseline(); change(report); assert.throws(() => managementEvidence(report, root), name); controls.push([name, 'refused']); }
  for (const status of ['failed', 'skipped', 'todo', 'pending', 'cancelled']) refused(`assertion ${status}`, report => { report.testResults[0].assertionResults[0].status = status; });
  for (const title of ['', 'unrelated passing criterion', managementCriteria['ui/tests/components/cancellation.test.ts'][1]]) refused(`missing renamed or duplicate identity ${JSON.stringify(title)}`, report => { report.testResults[0].assertionResults[0].title = title; });
  refused('missing assertion with adjusted summary', report => { report.testResults[0].assertionResults.pop(); report.numTotalTests--; report.numPassedTests--; });
  refused('missing file with adjusted summary', report => { const removed = report.testResults.pop(); report.numTotalTests -= removed.assertionResults.length; report.numPassedTests = report.numTotalTests; });
  refused('substituted file', report => { report.testResults[0].name = path.join(root, 'ui/tests/unrelated.test.ts'); });
  refused('duplicate file', report => { report.testResults.push(structuredClone(report.testResults[0])); report.numTotalTests += report.testResults[0].assertionResults.length; report.numPassedTests = report.numTotalTests; });
  refused('extra file', report => { report.testResults.push({ name: path.join(root, 'ui/tests/extra.test.ts'), status: 'passed', assertionResults: [{ title: 'extra', status: 'passed' }] }); report.numTotalTests++; report.numPassedTests++; });
  refused('dishonest total', report => { report.numTotalTests++; report.numPassedTests++; });
  refused('failed file', report => { report.testResults[0].status = 'failed'; });
  refused('unsuccessful run', report => { report.success = false; });
  for (const field of ['numFailedTests', 'numPendingTests', 'numTodoTests', 'numFailedTestSuites', 'numPendingTestSuites']) refused(`nonzero ${field}`, report => { report[field] = 1; });
  const previewFile = 'ui/tests/management-preview/session.test.ts';
  const preview = report => report.testResults.find(result => result.name === path.join(root, previewFile));
  refused('omitted preview file with adjusted summary', report => { const removed = preview(report); report.testResults = report.testResults.filter(result => result !== removed); report.numTotalTests -= removed.assertionResults.length; report.numPassedTests = report.numTotalTests; });
  refused('omitted preview case with adjusted summary', report => { preview(report).assertionResults.pop(); report.numTotalTests--; report.numPassedTests--; });
  refused('preview case replaced by an unrelated passing source case', report => { preview(report).assertionResults[0].title = managementCriteria['ui/tests/management/controller.test.ts'][0]; });
  refused('duplicate preview case with adjusted summary', report => { preview(report).assertionResults.push(structuredClone(preview(report).assertionResults[0])); report.numTotalTests++; report.numPassedTests++; });
  refused('skipped preview case cannot qualify', report => { preview(report).assertionResults[0].status = 'skipped'; });
  return { boundary: 'Synthetic receipt-parser controls only', controls: controls.map(([criterion, outcome]) => ({ criterion, outcome })) };
}
