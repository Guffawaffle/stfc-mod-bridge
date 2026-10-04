import assert from 'node:assert/strict';
import { vitestEvidence } from './test-evidence.mjs';

const primitives = [
  'button uses a native disabled busy guard and explicit action type',
  'field associates visible label, instructions, required and invalid explanation with its input',
  'select remains native with a visible label and disabled options',
  'notice announces only when requested and gives a warning a visible textual title',
  'navigation identifies the current view without tab-widget keyboard assumptions',
  'target summary labels installation, profile and exact session separately',
  'dialog has a native dialog element, named body and guaranteed Stay action',
  'current valid progress emits the observed value and maximum',
  'unknown progress never emits a known percentage or determinate value',
  'stale progress never emits a known percentage or determinate value',
  'long labels and HTML-like content render as text',
  ...[[undefined, 100], [NaN, 100], [Infinity, 100], [-1, 100], [101, 100], [1, 0], [1, -1], [1, Infinity], [1, NaN],
    [Number.MAX_SAFE_INTEGER + 1, Number.MAX_SAFE_INTEGER + 1]].map(([value, max]) => `absent or invalid progress ${value} / ${max} remains unknown`),
  'zero is observed progress and the safe maximum is handled without coercion',
  'showModal opens once and closing restores the connected opener once',
  'Escape prevents native dismissal and requests semantic Stay only once',
  'busy prevents Escape and Stay; native accidental close reopens the same modal',
  'Tab and Shift Tab wrap focus and leave intermediate native focus handling alone',
  'focus outside the modal and an empty enabled-control set remain contained',
  'unmount closes modal and a disconnected opener is never focused',
  'initial focus accepts only a connected element within this modal',
  'unexpected nonbusy native dismissal requests Stay and restores focus',
  'queued close event from a previous opening cannot dismiss a reopened modal',
  ...['light', 'dark'].flatMap(palette => [
    ...[['text', 'surface'], ['text', 'background'], ['muted', 'surface'], ['muted', 'accent-soft'], ['muted', 'warning-soft'],
      ['accent-text', 'accent-soft'], ['on-accent', 'accent'], ['on-danger', 'danger'], ['danger-text', 'danger-soft']]
      .map(([foreground, background]) => `${palette} ${foreground} on ${background} retains normal text contrast`),
    `${palette} field boundary and focus retain nontext contrast`,
  ]),
];
const facade = [
  'facade stages acknowledges reviews explicitly commits and waits for actual completed Save',
  'facade synchronization refusal retains local edits and queued navigation without preparation',
  'facade synchronized draft accepts Rust optional None binding while retaining exact submitted edits',
  'facade commit timeout retains exact replay and never claims Save or backend cancellation',
  'facade Stay releases only review navigation and restores a connected registered opener',
  'facade reentrant Stay cannot deliver superseded Save review to a later listener',
  'facade remount during nested Stay publication cannot revive superseded Save review',
  'facade unsent initial commit retains review and allows explicit Stay without claiming Save',
  'facade unsent replay preserves the original uncertain submission and navigation custody',
  'facade sent domain rejection retains exact uncertain custody until authoritative completion',
  'facade post-timeout replay rejection cannot prove the original submission unadmitted',
  'facade two authoritative completed Saves reuse a bounded one-entry replay capacity',
  'facade explicit retry retires only the proved-unsent owned replay before using a fresh key',
  'facade replay conflict and Stay preserve another callers preexisting capture',
  'facade uncertain disposal retains exact replay while proved-unsent disposal releases owned capture',
  'facade terminal reconciliation cannot forget a replacement replay with a different capture',
  'facade unsent invocation of a preexisting identical replay cannot prove earlier non-admission',
  'facade exact terminal failure releases replay without claiming Save or releasing queued navigation',
  'facade uncertain replay refuses a replaced key rather than submitting another callers input',
  'facade contradictory completed operation cannot reconcile an uncertain reviewed submission',
  'facade confirmed Discard releases queued navigation only for the exact reviewed backend draft',
  'facade foreign Discard receipt retains local edits and queued navigation',
  'draft synchronization rejects foreign regressed reused and changed schema acknowledgements',
  'late draft synchronization cannot release another captured review generation',
  'facade disposal during discard releases local review but ignores a late receipt',
  'announcements stay bounded and focus refuses disconnected or replaced registrations',
  'facade known host invalidation refuses new draft Save and Discard without outbound work',
  'facade reentrant host invalidation during synchronization publication sends no old draft command',
  'facade matching synchronization reply after host replacement cannot advance old draft review',
  'facade matching prepared plan after host replacement cannot become a Save review',
  'facade host replacement during review cannot submit a fresh old draft Save',
  'facade host replacement during admitting cannot submit a fresh old draft Save',
  'facade host replacement prevents reviewed Discard and preserves exact numeric buffers',
  'facade late old host Discard receipt retains typed edits numeric buffers and queued navigation',
  'facade admitted Save keeps exact replay and reconciles completion after host replacement',
  'facade uncertain Save replays only its retained exact input after host replacement',
  'facade synchronous disposal during synchronized review publication releases exact updated custody',
  'facade synchronized review disposal cleanup preserves a newly captured review owner',
];
assert.equal(primitives.length, 51); assert.equal(facade.length, 38);
export const componentCriteria = Object.freeze({
  'ui/tests/components/primitives.test.ts': Object.freeze(primitives),
  'ui/tests/components/facade.test.ts': Object.freeze(facade),
});

export function componentEvidence(report, root) {
  const inventory = vitestEvidence(report, { root, required: componentCriteria });
  assert.equal(inventory.tests, 89, 'Component evidence must execute all 89 bound assertions');
  assert.equal(inventory.files.length, 2, 'Component evidence cannot substitute another test file');
  for (const result of report.testResults) {
    const observed = inventory.files.find(file => result.name.replaceAll('\\', '/').endsWith(`/${file.file}`));
    assert.ok(observed && Object.hasOwn(componentCriteria, observed.file));
    const titles = result.assertionResults.map(assertion => assertion.title);
    assert.equal(new Set(titles).size, titles.length, 'Component assertion identities must be unique');
    assert.equal(titles.length, componentCriteria[observed.file].length);
    assert.deepEqual([...titles].sort(), [...componentCriteria[observed.file]].sort());
  }
  return { ...inventory, criteria: componentCriteria };
}
