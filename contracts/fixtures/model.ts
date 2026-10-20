import type { Cursor, Event, PlanSemantics, DiagnosticContent, Request, Reply } from '../../ui/src/generated/protocol.js';

export type ScenarioId = `SC-${'01'|'02'|'03'|'04'|'05'|'06'|'07'|'08'|'09'|'10'|'11'|'12'|'13'|'14'|'15'|'16'|'17'|'18'}`;
type FixtureBase = { id: string; scenario: ScenarioId; case: string; tags: readonly string[] };
export type GoldenFixture = FixtureBase & (
  | { kind: 'request'; payload: Request; expectedWire: true; expectedSemantic: true }
  | { kind: 'reply'; payload: Reply; expectedWire: true; expectedSemantic: true }
  | { kind: 'event'; payload: Event; expectedWire: true; expectedSemantic: true }
  | { kind: 'request'|'reply'|'event'; payload: unknown; expectedWire: boolean; expectedSemantic: false }
  | { kind: 'request'|'reply'|'event'; raw: string|Uint8Array; expectedWire: false; expectedSemantic: false }
);
export type GoldenStep =
  | { type: 'boundary'; reason: 'initial'|'restart'|'reconnect'|'resnapshot'|'retention_gap'; cursor: Cursor }
  | { type: 'exchange'; request: string; reply: string; delivery?: 'received'|'lost' }
  | { type: 'event'; event: string };
export type GoldenTranscript = {
  id: string; scenario: ScenarioId; case: string; steps: readonly GoldenStep[];
  expected: { accepted: true } | { accepted: false; code: string; step?: number };
};
export type GoldenCatalog = { fixtures: readonly GoldenFixture[]; transcripts: readonly GoldenTranscript[] };
export type FixtureHooks = {
  semanticDigest: (semantics: PlanSemantics) => string;
  diagnosticDigest: (content: DiagnosticContent) => string;
};

// These are authoring descriptors, not a domain implementation. Their messages
// are checked by the generated schema and the real Rust codec before any golden
// relationship transcript runs. A fixture is never runtime/release evidence.
