import type { Cursor, Event, Reply, Request } from '../../ui/src/generated/protocol.js';

/** Messages must already be normalized by the Rust codec and schema validated.
 * Acceptance checks only modeled relationships, never real execution/durability.
 * Complete snapshots account for all observed pending operations; partial
 * snapshots have codec-validated issues and retain prior relationship history.
 * eventCount includes standalone events and events observed in resume_events. */
export type TranscriptStep =
  | { type: 'boundary'; reason: 'initial' | 'restart' | 'reconnect' | 'resnapshot' | 'retention_gap'; cursor: Cursor }
  | { type: 'exchange'; request: Request; reply: Reply; delivery?: 'received' | 'lost' }
  | { type: 'event'; event: Event };
export const MAX_TRANSCRIPT_STEPS: number;
export class TranscriptError extends Error {
  readonly code: string;
  readonly step: number;
}
export function checkTranscript(steps: readonly TranscriptStep[]): Readonly<{
  boundaryCount: number;
  exchangeCount: number;
  eventCount: number;
  operationCount: number;
  lostReplyCount: number;
}>;
