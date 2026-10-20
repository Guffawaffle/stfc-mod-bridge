import { BridgeClient, decodeReply, decodeRequest, type DeepReadonly } from '../../client';
import type { ConfigurationEdit, DraftSnapshot, Reply, Request, TargetSelector } from '../../generated/protocol';
import { ManualClock } from '../../mocks/clock';
import { ScenarioTransport, type MockScript, type MockStep } from '../../mocks/scenario-transport';
import { BridgeFacade } from '../../state';
import cleanRaw from '../../../../contracts/fixtures/sc08-open-clean-draft-reply.json?raw';
import stagedRequestRaw from '../../../../contracts/fixtures/sc08-stage-dirty-draft-request.json?raw';
import stagedReplyRaw from '../../../../contracts/fixtures/sc08-stage-dirty-draft-reply.json?raw';
import prepareRequestRaw from '../../../../contracts/fixtures/sc10-save-reviewed-draft-request.json?raw';
import prepareReplyRaw from '../../../../contracts/fixtures/sc10-save-reviewed-draft-reply.json?raw';
import completedRequestRaw from '../../../../contracts/fixtures/sc10-save-verified-result-request.json?raw';
import completedReplyRaw from '../../../../contracts/fixtures/sc10-save-verified-result-reply.json?raw';
import discardRequestRaw from '../../../../contracts/fixtures/sc08-discard-draft-request.json?raw';
import discardReplyRaw from '../../../../contracts/fixtures/sc08-discard-draft-reply.json?raw';
import refusalRaw from '../../../../contracts/fixtures/sc10-save-stale-revision-reply.json?raw';
import targetRaw from '../../../../contracts/fixtures/sc-03-profile-one-prepare-request.json?raw';
import snapshotRaw from '../../../../contracts/fixtures/sc15-complete-empty-snapshot-reply.json?raw';

export type GalleryMode = 'save_success' | 'save_uncertain' | 'discard_success' | 'discard_refused' | 'stay';
export interface GallerySession {
  readonly facade: BridgeFacade;
  readonly client: BridgeClient;
  readonly transport: ScenarioTransport;
  readonly clock: ManualClock;
  readonly edits: readonly DeepReadonly<ConfigurationEdit>[];
  readonly target: DeepReadonly<TargetSelector>;
  dispose(): void;
}
const key = '00000001-1111-4111-8111-111111111111';
const rawSources = {
  'sc08-open-clean-draft-reply': cleanRaw,
  'sc08-stage-dirty-draft-request': stagedRequestRaw,
  'sc08-stage-dirty-draft-reply': stagedReplyRaw,
  'sc10-save-reviewed-draft-request': prepareRequestRaw,
  'sc10-save-reviewed-draft-reply': prepareReplyRaw,
  'sc10-save-verified-result-request': completedRequestRaw,
  'sc10-save-verified-result-reply': completedReplyRaw,
  'sc08-discard-draft-request': discardRequestRaw,
  'sc08-discard-draft-reply': discardReplyRaw,
  'sc10-save-stale-revision-reply': refusalRaw,
  'sc-03-profile-one-prepare-request': targetRaw,
  'sc15-complete-empty-snapshot-reply': snapshotRaw,
};
function request(raw: string): Request { decodeRequest(raw); return JSON.parse(raw); }
function reply(raw: string): Reply { decodeReply(raw); return JSON.parse(raw); }
function exchange(input: Request, output: Reply, delivery: 'received' | 'lost' = 'received'): MockStep {
  return { type: 'exchange', request: input, reply: { ...output, requestId: input.requestId }, delivery };
}
function draft(raw: string): DeepReadonly<DraftSnapshot> {
  const frame = decodeReply(raw);
  if (frame.body.type !== 'result' || frame.body.result.type !== 'command'
    || !['open_draft', 'set_draft_changes'].includes(frame.body.result.command.name)) throw new Error('gallery_fixture_shape');
  const command = frame.body.result.command;
  if (command.name === 'open_draft') return command.output;
  if (command.name === 'set_draft_changes') return command.output.snapshot;
  throw new Error('gallery_fixture_shape');
}

/** Development composition only. Sources are the unchanged shared accepted fixtures. */
export async function createGallerySession(mode: GalleryMode): Promise<GallerySession> {
  const stage = request(stagedRequestRaw), prepare = request(prepareRequestRaw), completed = reply(completedReplyRaw);
  if (stage.body.type !== 'command' || stage.body.command.name !== 'set_draft_changes'
    || prepare.body.type !== 'command' || prepare.body.command.name !== 'prepare'
    || completed.body.type !== 'result' || completed.body.result.type !== 'query'
    || completed.body.result.query.name !== 'get_operation' || completed.body.result.query.output.operation.status !== 'observed') throw new Error('gallery_fixture_shape');
  const plan = decodeReply(prepareReplyRaw);
  if (plan.body.type !== 'result' || plan.body.result.type !== 'command' || plan.body.result.command.name !== 'prepare') throw new Error('gallery_fixture_shape');
  const operation = completed.body.result.query.output.operation.value;
  const snapshot = reply(snapshotRaw);
  if (snapshot.body.type !== 'result' || snapshot.body.result.type !== 'query' || snapshot.body.result.query.name !== 'snapshot'
    || operation.semantics.capture.kind !== 'save_configuration' || operation.state.status !== 'completed'
    || operation.state.outcome.kind !== 'changed' || operation.state.outcome.receipt?.kind !== 'configuration_written') throw new Error('gallery_completion_shape');
  const captured = operation.semantics.capture.input.draft;
  const saved: DraftSnapshot = { ...captured, draft: { ...captured.draft, revision: (BigInt(captured.draft.revision) + 1n).toString(),
    document: operation.state.outcome.receipt.document }, edits: [], apply: [], validation: [], state: 'clean' };
  const getDraft: Request = { protocolVersion: 1, requestId: '00000901-1111-4111-8111-111111111111', body: { type: 'query', query: {
    name: 'get_draft', input: { hostEpoch: captured.draft.hostEpoch, draftId: captured.draft.draftId } } } };
  const cleanRead: Reply = { protocolVersion: 1, requestId: getDraft.requestId, body: { type: 'result', result: { type: 'query', query: {
    name: 'get_draft', output: { cursor: { ...snapshot.body.result.query.output.cursor, sequence: '2' }, draft: {
      status: 'observed', evidence: completed.body.result.query.output.operation.evidence, value: saved } } } } } };
  decodeRequest(JSON.stringify(getDraft)); decodeReply(JSON.stringify(cleanRead));
  const commit: Request = { protocolVersion: 1, requestId: '00000845-1111-4111-8111-111111111111',
    body: { type: 'command', command: { name: 'commit', input: { idempotencyKey: key, planRef: plan.body.result.command.output.planRef } } } };
  // This synthetic admission is derived from the shared completed capture. It
  // supplies no native policy and never qualifies a native execution.
  const admitted: Reply = { protocolVersion: 1, requestId: commit.requestId, body: { type: 'result', result: { type: 'command', command: {
    name: 'commit', output: { ...operation, operationRevision: '1', state: { status: 'admitted' } }
  } } } };
  decodeRequest(JSON.stringify(commit)); decodeReply(JSON.stringify(admitted));
  const discardMode = mode === 'discard_success' || mode === 'discard_refused';
  const steps: MockStep[] = discardMode
    ? [exchange(request(discardRequestRaw), reply(mode === 'discard_refused' ? refusalRaw : discardReplyRaw))]
    : [exchange(stage, reply(stagedReplyRaw)), exchange(prepare, reply(prepareReplyRaw)),
      exchange(commit, admitted, mode === 'save_uncertain' ? 'lost' : 'received'),
      ...(mode === 'save_uncertain' ? [exchange(commit, admitted)] : []), exchange(request(completedRequestRaw), completed), exchange(getDraft, cleanRead)];
  const sources = await Promise.all(Object.entries(rawSources).map(async ([id, raw]) => {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(raw));
    return { id, sha256: Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') };
  }));
  const script: MockScript = { id: `br12-gallery-${mode}`, scenario: 'SC-10', case: 'Development component interaction from shared fixtures', correlationSlot: 'requestId', sources, steps };
  const clock = new ManualClock();
  const transport = new ScenarioTransport(script, { clock, latencyMs: 450 });
  let sequence = 0;
  const client = new BridgeClient(transport, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222', clock, timeoutMs: 1200 });
  const facade = new BridgeFacade(client, { idempotencyKey: () => key });
  facade.work.observations.acceptSnapshot(snapshot.body.result.query.output);
  facade.work.openDraft(draft(discardMode ? stagedReplyRaw : cleanRaw));
  const target = decodeRequest(targetRaw);
  if (target.body.type !== 'command' || target.body.command.name !== 'prepare' || target.body.command.input.intent.kind !== 'launch_isolated') throw new Error('gallery_fixture_shape');
  return { facade, client, transport, clock, edits: stage.body.command.input.edits, target: target.body.command.input.intent.input.target,
    dispose() { facade.dispose(); client.dispose(); transport.dispose(); clock.dispose(); } };
}
