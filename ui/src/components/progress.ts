export type ProgressConfidence = 'current' | 'unknown' | 'stale';

export interface ProgressPresentation {
  readonly determinate: boolean;
  readonly value?: number;
  readonly max: number;
  readonly text: string;
}

/** Presentation cannot turn an absent, unsafe or stale observation into progress. */
export function presentProgress(value: number | undefined, max = 100,
  confidence: ProgressConfidence = 'current'): ProgressPresentation {
  if (confidence === 'stale') return { determinate: false, max: 100, text: 'Observation stale' };
  const safe = confidence === 'current' && value !== undefined && Number.isFinite(value)
    && Number.isFinite(max) && max > 0 && max <= Number.MAX_SAFE_INTEGER
    && value >= 0 && value <= max && value <= Number.MAX_SAFE_INTEGER;
  return safe ? { determinate: true, value, max, text: `${Math.round(value / max * 100)}%` }
    : { determinate: false, max: 100, text: 'Progress unknown' };
}
