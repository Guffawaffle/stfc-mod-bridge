import assert from 'node:assert/strict';
import { vitestEvidence } from './test-evidence.mjs';

// Frozen semantic inventory. Missing, renamed, skipped or substituted cases refuse.
export const homeCriteria = Object.freeze({
  "ui/tests/home/action-review.test.ts": [
    "Home ordinary action requires explicit Review and Confirm and acknowledges an actual terminal result",
    "Home isolated admission retains captured work while later target selection changes and readiness arrives",
    "Home focus preparation captures exact PID start executable and session instead of the visible target",
    "Home shared entry points exclude competing target close Save and Discard through uncertain admission",
    "Home pending dirty draft and Save review exclude generic preparation while view navigation preserves edits",
    "Home lost commit and replay rejection preserve the exact submission until terminal reconciliation",
    "Home initial sent rejection remains uncertain and disposal never forgets its owned replay",
    "Home exact lost-response replay observes the original admission without claiming completion or cancellation",
    "Home proved-unsent Stay releases only the captured replay and restores a connected opener",
    "Home generic terminal reconciliation permits two actions with one replay slot",
    "Home stale epoch and disposed preparation cannot install a late review",
    "Home foreign prepared profile capture cannot reach confirmation or allocate replay",
    "Home denied runtime review presents the typed per-attempt choice without implicit consent or confirmation",
    "Home conflicting preexisting replay remains owned by its original caller after Stay"
  ],
  "ui/tests/home/controller.test.ts": [
    "Home reads scoped ordinary availability without auto selecting an installation profile or session",
    "Home unknown availability never becomes ready stopped or an enabled launch",
    "Home isolated launch preserves immutable ID and requires an explicit new resume or existing mode",
    "Home conflicting registered identity refuses binding and never queries launch availability",
    "Home target change and disposal abandon old read observations without adopting late bindings",
    "Home focus availability is scoped to exact recycled process identity and unknown recheck clears old availability",
    "Home superseded focus observation cannot restore availability after a later failed check",
    "Home stale inventories revoke displayed availability while exact captured operation facts remain in the shared store"
  ],
  "ui/tests/home/presentation.test.ts": [
    "Home uninitialized view exposes explicit native selectors and unavailable action without auto selection",
    "Home duplicate profile names remain distinct options and captured summaries preserve the ordinary mode",
    "Home partial missing unknown and unavailable inventories keep distinct truthful copy",
    "Home backend blocked unknown offline and recovery reasons are shown without granting authority",
    "Home recycled PID live identity and isolation readiness remain separate and never imply account sign in",
    "Home normal render hides private paths and escapes long labels while separating maintenance destinations",
    "Home workspace navigation marks the current view and view changes preserve the same dirty draft",
    "Home queued dirty target change renders one semantic Save Discard Stay decision without claiming admission success",
    "Home operation progress preserves exact observed counts and refuses unknown stale or unsafe numeric percentages",
    "Home Save review copy describes protected edits without exposing references or private public-string paths"
  ],
  "ui/tests/home-preview/session.test.ts": [
    "Home preview every mode validates exact composed wire frames and hashes all unchanged shared source bytes",
    "Home preview ordinary launch binds observed revisions and preserves accepted review digest through admission and exact completion",
    "Home preview isolated launch preserves explicit existing mode immutable profile revision and accepted completion capture",
    "Home preview focus uses exact Windows session while old and recycled PID identities remain independent observations",
    "Home preview lost delivery retains exact commit until explicit replay then completion without automatic retry",
    "Home preview unknown partial offline missing and recovery states never enable or submit a launch",
    "Home preview dirty draft survives view navigation and Stay then exact reviewed Save applies target only after completion",
    "Home preview confirmed Discard uses the exact old dirty DraftRef and receipt before applying queued target",
    "Home preview disposal ends old pending observation and bounded clocks without projecting late results into replacement session"
  ]
});

export function homeEvidence(report, root) {
  const result = vitestEvidence(report, { root, required: homeCriteria });
  assert.equal(result.tests, 41); assert.equal(result.files.length, 4);
  for (const output of report.testResults) {
    const file = result.files.find(value => output.name.replaceAll('\\', '/').endsWith('/' + value.file));
    assert.ok(file && Object.hasOwn(homeCriteria, file.file));
    const names = output.assertionResults.map(value => value.title);
    assert.equal(new Set(names).size, names.length);
    assert.deepEqual([...names].sort(), [...homeCriteria[file.file]].sort());
  }
  return { ...result, criteria: homeCriteria };
}
