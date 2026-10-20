import { counter, type DeepReadonly, type ObservationState } from '../client';
import type { ConfigurationEdit, OperationSnapshot } from '../generated/protocol';
import type { ProgressConfidence } from '../components/progress';

export function actionProgress(operation: DeepReadonly<OperationSnapshot> | undefined, confidence: ObservationState['confidence']): {
  label: string; detail: string; confidence: ProgressConfidence; value?: number; max?: number;
} {
  const progress = operation?.state.status === 'running' || operation?.state.status === 'cancellation_requested' ? operation.state.progress : undefined;
  if (!progress) return { label: 'Operation progress', detail: 'Progress has not been observed.', confidence: confidence === 'stale' ? 'stale' : 'unknown' };
  const measure = progress.measurement, label = progress.phase.replaceAll('_', ' ');
  if (measure.unit === 'unknown') return { label, detail: 'The backend has not reported a measured total.', confidence: confidence === 'stale' ? 'stale' : 'unknown' };
  const detail = `${measure.completed}${measure.total == null ? '' : ` of ${measure.total}`} ${measure.unit} observed.`;
  if (confidence === 'stale') return { label, detail, confidence: 'stale' };
  const completed = counter(measure.completed), total = measure.total == null ? undefined : counter(measure.total), safe = BigInt(Number.MAX_SAFE_INTEGER);
  if (total === undefined || total < 1n || completed > total || completed > safe || total > safe) return { label, detail, confidence: 'unknown' };
  return { label, detail, confidence: 'current', value: Number(completed), max: Number(total) };
}
/** Public review copy never prints protected references, destinations or private paths. */
export function editSummary(edit: DeepReadonly<ConfigurationEdit>): string {
  switch (edit.kind) {
    case 'set_public': return edit.value.kind === 'boolean' ? (edit.value.value ? 'On' : 'Off')
      : edit.value.kind === 'integer' || edit.value.kind === 'number' ? `Set to ${edit.value.value}` : 'Public value updated';
    case 'remove_override': return 'Override removed';
    case 'set_private': return 'Private value updated';
    case 'replace_secret': return 'Protected value replaced';
    case 'clear_secret': return 'Protected value cleared';
    case 'add_sync_destination': return 'Sync destination added';
    case 'remove_sync_destination': return 'Sync destination removed';
    case 'set_sync_feed': return 'Sync feed preference updated';
    case 'set_sync_proxy': return 'Sync proxy preference updated';
  }
}
