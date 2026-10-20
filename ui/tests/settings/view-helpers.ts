import { readFileSync } from 'node:fs';
import { BridgeClient, canonicalData, decodeReply, decodeRequest, type ClientClock, type DeepReadonly, type RawFrame } from '../../src/client';
import type { DocumentSnapshot, DraftSnapshot, Request, TargetSelector } from '../../src/generated/protocol';
import { BridgeFacade } from '../../src/state';
import { SettingsController } from '../../src/views/settings/controller';

const root = new URL('../../../contracts/fixtures/', import.meta.url);
export const frame = (name: string): any => JSON.parse(readFileSync(new URL(name + '.json', root), 'utf8'));
export const clean = (): DraftSnapshot => frame('sc08-open-clean-draft-reply').body.result.command.output;
export function document(): DocumentSnapshot { const draft = clean(); return { binding: draft.draft.document, schema: draft.schema, fields: [], sync: [], preservation: 'supported' }; }
export function selector(): TargetSelector { const target = clean().draft.document.target;
  if (target.installation.kind !== 'registered') throw new Error('fixture_target');
  return { installation: { kind: 'registered', id: target.installation.registrationId }, profile: { kind: 'ordinary' } };
}
const clock: ClientClock = { schedule: () => () => {} };
export function harness(change?: (request: DeepReadonly<Request>, output: unknown) => unknown | Promise<unknown>) {
  let sequence = 0; const sent: DeepReadonly<Request>[] = [];
  const observed = document();
  const client = new BridgeClient({ subscribe: () => () => {}, async exchange(encoded): Promise<RawFrame> {
    const request = decodeRequest(encoded); sent.push(request);
    const command = request.body.type === 'command', name = command ? request.body.command.name : request.body.query.name;
    let output: unknown;
    if (name === 'read_configuration') output = { status: 'observed', evidence: { observationId: '000003ea-1111-4111-8111-111111111111', source: 'native_live', observedAt: '2026-07-01T00:00:00Z' }, value: observed };
    else if (name === 'open_draft') output = { ...clean(), draft: { ...clean().draft, document: observed.binding }, schema: observed.schema };
    else if (name === 'set_draft_changes') output = frame('sc08-stage-dirty-draft-reply').body.result.command.output;
    else if (name === 'request_sensitive_input' && request.body.type === 'command' && request.body.command.name === 'request_sensitive_input') output = { binding: request.body.command.input, outcome: {status:'cancelled'} };
    else throw new Error('unexpected_test_method:' + name);
    output = await (change ? change(request, output) : output);
    const reply = { protocolVersion: 1, requestId: request.requestId, body: { type: 'result', result: command ? {type:'command',command:{name,output}} : {type:'query',query:{name,output}} } };
    const raw = JSON.stringify(reply); decodeReply(raw); return raw;
  } }, { requestId: () => (++sequence).toString(16).padStart(8, '0') + '-2222-4222-8222-222222222222', clock });
  const facade = new BridgeFacade(client, { idempotencyKey: () => '00000001-1111-4111-8111-111111111111' });
  facade.requestTarget(selector()); facade.work.bindTarget(clean().draft.document.target); facade.work.openDraft(clean());
  const controller = new SettingsController(facade, 'windows');
  return { facade, controller, observed, sent, dispose() { controller.dispose(); facade.dispose(); } };
}
export const same = (left: unknown, right: unknown) => canonicalData(left) === canonicalData(right);
