import { readFileSync } from 'node:fs';
import { BridgeClient, canonicalData, decodeReply, decodeRequest, type DeepReadonly } from '../../src/client';
import type { MutationIntent, Reply, Request, Snapshot } from '../../src/generated/protocol';
import { ManualClock } from '../../src/mocks/clock';
import { ScenarioTransport, type MockScript, type MockStep } from '../../src/mocks/scenario-transport';
import { BridgeFacade } from '../../src/state';

const fixtures = new URL('../../../contracts/fixtures/', import.meta.url);
export const frame = (id: string): any => JSON.parse(readFileSync(new URL(id + '.json', fixtures), 'utf8'));
export function catalog(id: string): MockScript {
  const scripts = JSON.parse(readFileSync(new URL('../../scenarios/catalog.json', import.meta.url), 'utf8')).scripts as MockScript[];
  const script = scripts.find(row => row.id === id); if (!script) throw new Error('missing_shared_scenario'); return script;
}
export function snapshot(): Snapshot {
  const reply = frame('sc15-complete-empty-snapshot-reply');
  reply.body.result.query.output.installations = frame('sc-01-registered-installations-list-reply').body.result.query.output;
  reply.body.result.query.output.profiles = frame('sc-05-catalog-with-duplicate-display-names-reply').body.result.query.output;
  reply.body.result.query.output.sessions = frame('sc-04-multiple-and-recycled-process-observations-reply').body.result.query.output;
  decodeReply(JSON.stringify(reply)); return reply.body.result.query.output;
}
export function request(id: string): Request { const value = frame(id); decodeRequest(JSON.stringify(value)); return value; }
export function reply(id: string): Reply { const value = frame(id); decodeReply(JSON.stringify(value)); return value; }
export const exchange = (input: Request, output: Reply): MockStep => ({ type: 'exchange', request: input, reply: { ...output, requestId: input.requestId } });
export function intent(script: MockScript): DeepReadonly<MutationIntent> {
  const step = script.steps.find(row => row.type === 'exchange' && row.request.body.type === 'command' && row.request.body.command.name === 'prepare');
  if (step?.type !== 'exchange' || step.request.body.type !== 'command' || step.request.body.command.name !== 'prepare') throw new Error('missing_prepare_intent');
  return step.request.body.command.input.intent;
}
export function harness(script: MockScript, maximumReplays = 64) {
  const clock = new ManualClock(), transport = new ScenarioTransport(script, { clock }); let sequence = 0;
  const id = (number: number) => number.toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222';
  const client = new BridgeClient(transport, { requestId: () => id(++sequence), clock, timeoutMs: 1000, maximumReplays });
  const facade = new BridgeFacade(client, { idempotencyKey: () => {
    const step = script.steps[transport.state.position];
    const next = transport.expectedRequest ?? (step?.type === 'exchange' ? step.request : undefined);
    if (next?.body.type !== 'command' || next.body.command.name !== 'commit') throw new Error('missing_expected_commit');
    return next.body.command.input.idempotencyKey;
  } });
  const observed = snapshot(); observed.cursor = transport.state.boundary?.cursor ?? observed.cursor; facade.work.observations.acceptSnapshot(observed);
  const stop = client.subscribe(event => facade.work.observations.acceptEvent(event), () => facade.work.observations.invalidate('disconnected'));
  return { clock, transport, client, facade,
    inject(value: Reply) { const raw = JSON.stringify({ ...value, requestId: id(sequence + 1) }); decodeReply(raw); transport.injectNextReply(raw); },
    dispose() { facade.dispose(); stop(); transport.dispose(); clock.dispose(); },
  };
}
export async function deliver<T>(pending: Promise<T>, clock: ManualClock, elapsed = 100): Promise<T> { clock.advanceBy(elapsed); return pending; }
export function sameInput(left: unknown, right: unknown): boolean { return canonicalData(left) === canonicalData(right); }
