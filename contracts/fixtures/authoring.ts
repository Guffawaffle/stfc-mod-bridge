import type {
  Cursor, Evidence, Event, OperationSnapshot, OperationState, PlanSemantics,
  PreparedPlan, Request, Reply
} from '../../ui/src/generated/protocol.js';
import { syntheticId } from './helpers.ts';
import type { FixtureHooks, GoldenFixture, ScenarioId } from './model.ts';

export const hostEpoch = syntheticId(1000);
export const streamId = syntheticId(1001);
export const cursor: Cursor = { hostEpoch, streamId, sequence: '0' };
export const evidence: Evidence = {
  observationId: syntheticId(1002), observedAt: '2026-10-03T11:00:00Z', source: 'native_live'
};
export const digest = (character: string): string => {
  if (!/^[0-9a-f]$/.test(character)) throw new Error('Synthetic digest requires one hexadecimal character');
  return `sha256:${character.repeat(64)}`;
};
export const observed = <T>(value: T) => ({ status: 'observed' as const, value, evidence });
export const unavailable = () => ({ status: 'unavailable' as const, reason: 'native_unavailable' as const });
export const inventory = <T>(items: T[]) => ({ items, completeness: 'complete' as const, issues: [], revision: 'synthetic-inventory-1' });

export function requestFixture(id: string, scenario: ScenarioId, case_: string, payload: Request, tags: string[] = []): GoldenFixture {
  return { id, scenario, case: case_, tags, kind: 'request', payload, expectedWire: true, expectedSemantic: true };
}
export function replyFixture(id: string, scenario: ScenarioId, case_: string, payload: Reply, tags: string[] = []): GoldenFixture {
  return { id, scenario, case: case_, tags, kind: 'reply', payload, expectedWire: true, expectedSemantic: true };
}
export function eventFixture(id: string, scenario: ScenarioId, case_: string, payload: Event, tags: string[] = []): GoldenFixture {
  return { id, scenario, case: case_, tags, kind: 'event', payload, expectedWire: true, expectedSemantic: true };
}
export function refusalFixture(id: string, scenario: ScenarioId, case_: string, kind: 'request'|'reply'|'event', payload: unknown, expectedWire = true): GoldenFixture {
  return { id, scenario, case: case_, tags: ['codec-refusal'], kind, payload, expectedWire, expectedSemantic: false };
}
export function prepared(hooks: FixtureHooks, semantics: PlanSemantics, sequence: number): PreparedPlan {
  return {
    planRef: { planId: syntheticId(sequence), hostEpoch, reviewDigest: hooks.semanticDigest(semantics) },
    semantics, expiresAt: '2026-10-04T00:00:00Z', grantsLock: false, grantsPermission: false
  };
}
export function operation(plan: PreparedPlan, sequence: number, revision: string, state: OperationState): OperationSnapshot {
  return { operationId: syntheticId(sequence), operationRevision: revision, semantics: plan.semantics, state };
}
