<script lang="ts">
import { untrack } from 'svelte';
import { Button, Notice, Progress } from '../../components';
import { actionProgress } from '../../navigation/action-presentation';
import type { BridgeFacade } from '../../state';
import type { DeepReadonly } from '../../client';
import type { OperationSnapshot } from '../../generated/protocol';
import { actionTitle, operationStatus, recoveryLabel, captureSummary } from './presentation';
export interface Props {
    facade: BridgeFacade;
    onrefresh?: (operation: DeepReadonly<OperationSnapshot>) => void | Promise<void>;
    onrecover?: (operation: DeepReadonly<OperationSnapshot>) => void | Promise<void>;
}
let { facade, onrefresh, onrecover }: Props = $props();
const actions = untrack(() => facade.actions);
async function cancel(operation: DeepReadonly<OperationSnapshot>) { await facade.cancelOperation(operation); }
async function recover(operation: DeepReadonly<OperationSnapshot>) { if (onrecover)
    await onrecover(operation);
else
    await facade.actions.prepareRecovery(operation, `management-operation-recovery-${operation.operationId}`); }
</script>
<section aria-label="Recent observed operations"><h2>Recent observed operations</h2><p>These are the operations observed by this connection. They are not a complete persistent history.</p>
 {#if $facade.observations.operations.length===0}<p>No operation records have been observed.</p>{/if}
 {#each $facade.observations.operations as operation(operation.operationId)}
  {@const progress=actionProgress(operation,$facade.observations.confidence)}
  {@const summary=captureSummary(operation)}
  <article><h3>{actionTitle(operation.semantics.capture.kind)}</h3><p>{operationStatus(operation)}</p><p class="id">Operation {operation.operationId} · revision {operation.operationRevision}</p>
   {#each summary.lines as line}<p>{line}</p>{/each}
   {#if operation.state.status==='running'||operation.state.status==='cancellation_requested'||operation.state.status==='admitted'}<Progress {...progress}/>{/if}
   {#if operation.state.status==='recovery_required'}<Notice title="Retained recovery obligation" tone="warning"><p>{recoveryLabel(operation)}</p><p>The original operation and its captured target remain retained.</p></Notice>
    {#if operation.state.recovery.target.kind==='game'||operation.state.recovery.target.kind==='bridge'}<Button id={`management-operation-recovery-${operation.operationId}`} disabled={$facade.observations.confidence!=='authoritative'||$facade.work.dirty||$actions.transition.kind!=='idle'} onclick={()=>recover(operation)}>Review recorded recovery</Button>{:else}<p>Executable recovery is unavailable for this recorded operation.</p>{/if}
   {:else if operation.state.status==='admitted'||operation.state.status==='running'}
    <Button busy={$actions.cancellingOperationIds.includes(operation.operationId)} disabled={$facade.observations.confidence!=='authoritative'} onclick={()=>cancel(operation)}>Request cancellation</Button><p class="help">Cancellation can be too late. Closing a view stops observation and does not cancel backend work.</p>
   {/if}
   <Button disabled={!onrefresh} onclick={()=>onrefresh?.(operation)}>Refresh this recorded operation</Button>
  </article>
 {/each}
</section>
<style>section,article{display:grid;gap:.85rem;min-inline-size:0}article{padding:1rem;border:1px solid var(--bridge-border);border-radius:var(--bridge-radius);background:var(--bridge-surface)}h2,h3,p{margin:0;overflow-wrap:anywhere}.help,.id{color:var(--bridge-muted);font-size:.9rem}</style>
