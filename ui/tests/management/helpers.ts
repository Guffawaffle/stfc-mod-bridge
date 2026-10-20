import { decodeReply, type QueryOutput } from '../../src/client';
import type { DocumentBinding, DraftSnapshot, MutationIntent, OperationSnapshot, PreparedPlan, Query, ResolvedTarget } from '../../src/generated/protocol';
import type { MockStep } from '../../src/mocks/scenario-transport';
import { catalog, frame, harness, reply } from '../home/helpers';
export { deliver, exchange, frame, harness, reply, request, snapshot } from '../home/helpers';
/** Accepted synthetic DTOs and scripted transport only; no native claim. */
export const script = (steps: MockStep[]) => ({ ...catalog('sc-02-ordinary-ready-action-journey'), id: 'management-synthetic-fixture-composition', steps });
export function output<N extends Query['name']>(id: string, name: N): QueryOutput<N> {
    const value = frame(id);
    decodeReply(JSON.stringify(value));
    if (value.body.type !== 'result' || value.body.result.type !== 'query' || value.body.result.query.name !== name)
        throw new Error('fixture_query');
    return value.body.result.query.output;
}
export function fixtureIntent(id: string): MutationIntent {
    const value = frame(id);
    if (value.body.type !== 'command' || value.body.command.name !== 'prepare')
        throw new Error('fixture_intent');
    return value.body.command.input.intent;
}
export function plan(id: string): PreparedPlan {
    const value = reply(id);
    if (value.body.type !== 'result' || value.body.result.type !== 'command' || value.body.result.command.name !== 'prepare')
        throw new Error('fixture_plan');
    return structuredClone(value.body.result.command.output);
}
export function operation(id: string): OperationSnapshot {
    const value = output(id, 'get_operation').operation;
    if (value.status !== 'observed')
        throw new Error('fixture_operation');
    return structuredClone(value.value);
}
export function draft(document?: DocumentBinding): DraftSnapshot {
    const value = frame('sc08-open-clean-draft-reply');
    if (document)
        value.body.result.command.output.draft.document = document;
    decodeReply(JSON.stringify(value));
    return value.body.result.command.output;
}
export function bind(run: ReturnType<typeof harness>, target: ResolvedTarget): void {
    if (target.installation.kind !== 'registered')
        throw new Error('registered_fixture');
    run.facade.requestTarget({ installation: { kind: 'registered', id: target.installation.registrationId }, profile: target.profile.kind === 'ordinary' ? { kind: 'ordinary' } : { kind: 'isolated', id: target.profile.id } });
    run.facade.work.bindTarget(target);
}
export function epoch(run: ReturnType<typeof harness>): string { const value = run.facade.work.observations.state.cursor?.hostEpoch; if (!value)
    throw new Error('fixture_epoch'); return value; }
