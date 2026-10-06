import type { DocumentBinding, DraftSnapshot, OperationSnapshot } from '../generated/protocol';
import { bindingEquivalent } from './relations';
import { captureData, type DeepReadonly } from './wire';

/** Cross-message configuration receipt checks mirror the Rust capture contract. */
export function configurationCompletionValid(operation: DeepReadonly<OperationSnapshot>): boolean {
  const capture = operation.semantics.capture, state = operation.state;
  if ((capture.kind !== 'save_configuration' && capture.kind !== 'restore_configuration')
    || operation.semantics.action !== capture.kind || operation.semantics.trustDomain !== 'configuration' || state.status !== 'completed') return false;
  if (state.outcome.kind === 'no_change') return state.outcome.reason === 'already_satisfied';
  if (state.outcome.kind !== 'changed' || state.outcome.reason !== 'applied' || state.outcome.receipt?.kind !== 'configuration_written') return false;
  const expected = capture.kind === 'save_configuration' ? capture.input.draft.draft.document : capture.input.document;
  const digest = capture.kind === 'save_configuration' ? capture.input.candidateDigest : capture.input.backup.retainedDigest;
  const { document, backup } = state.outcome.receipt;
  return document.documentId === expected.documentId && bindingEquivalent(document.target, expected.target)
    && bindingEquivalent({ ...expected, schema: document.schema }, expected)
    && document.baseline.kind === 'existing' && document.baseline.contentDigest === digest
    && (expected.baseline.kind === 'missing' ? backup == null
      : backup != null && bindingEquivalent(backup.document, expected) && backup.retainedDigest === expected.baseline.contentDigest);
}

/** Only structural eligibility; a matching completed operation is still required. */
export function possibleSavedDraftSuccessor(previous: DeepReadonly<DraftSnapshot>, next: DeepReadonly<DraftSnapshot>): boolean {
  const revision = BigInt(previous.draft.revision) + 1n;
  if (revision > 18446744073709551615n || next.draft.document.documentId !== previous.draft.document.documentId
    || !bindingEquivalent(next.draft.document.target, previous.draft.document.target)
    || !bindingEquivalent({ ...previous.draft.document, schema: next.draft.document.schema }, previous.draft.document)) return false;
  return bindingEquivalent(next, {
    ...previous,
    draft: { ...previous.draft, revision: revision.toString(), document: next.draft.document },
    edits: [], apply: [], validation: [], state: 'clean',
  });
}

/** Exact old draft to exact clean successor, with no inferred protected transfer. */
export function savedDraftSuccessor(previous: DeepReadonly<DraftSnapshot>, next: DeepReadonly<DraftSnapshot>, input: DeepReadonly<OperationSnapshot>): boolean {
  const operation = captureData(input), capture = operation.semantics.capture;
  if (capture.kind !== 'save_configuration' || !configurationCompletionValid(operation)
    || !bindingEquivalent(capture.input.draft, previous) || operation.state.status !== 'completed') return false;
  const outcome = operation.state.outcome;
  if (outcome.kind === 'no_change' && !previous.edits.length) return previous.state === 'clean' && bindingEquivalent(next, previous);
  if (!possibleSavedDraftSuccessor(previous, next)) return false;
  const document: DeepReadonly<DocumentBinding> = outcome.kind === 'changed' && outcome.receipt?.kind === 'configuration_written'
    ? outcome.receipt.document : previous.draft.document;
  return bindingEquivalent(next.draft.document, document);
}
