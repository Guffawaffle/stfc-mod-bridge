<script lang="ts">
  import { untrack } from 'svelte';
  import { useBridge } from '../app';
  import { Button, Dialog, Notice, Progress, TargetSummary } from '../components';
  import type { BridgeFacade } from '../state';
  import { targetLabels } from '../views/home/presentation';
  import { actionProgress } from './action-presentation';
  import { captureSummary } from '../views/management/presentation';
  export interface Props { facade?: BridgeFacade; }
  let { facade = useBridge() }: Props = $props();
  const reviewController = untrack(() => facade.actions);
  const phase = $derived($reviewController.transition.kind);
  const plan = $derived($reviewController.plan);
  const capture = $derived(plan?.semantics.capture);
  const target = $derived(capture && 'target' in capture ? capture.target : undefined);
  const labels = $derived(targetLabels(target, undefined, $facade.observations));
  const title = $derived(capture?.kind === 'launch_ordinary' ? 'Review ordinary launch' : capture?.kind === 'launch_isolated' ? 'Review isolated launch'
    : capture?.kind === 'focus_session' ? 'Review session focus' : plan ? captureSummary(plan).title : 'Review action');
  const summary = $derived(plan ? captureSummary(plan) : undefined);
  const busy = $derived(phase === 'preparing' || phase === 'admitting');
  const open = $derived(['preparing', 'review', 'admitting'].includes(phase));
  const operationId = $derived($reviewController.transition.kind === 'observing' ? $reviewController.transition.operationId : undefined);
  const operation = $derived($facade.observations.operations.find(row => row.operationId === operationId));
  const progress = $derived(actionProgress(operation, $facade.observations.confidence));
</script>
<Dialog {open} {title} description="This review captures the intended scope. Confirm submits this exact review." {busy}
  onstay={() => { reviewController.stay(); }}>
  {#if target}<TargetSummary title="Captured target" installation={labels.installation} profile={labels.profile}
    session={capture?.kind === 'focus_session' ? `PID ${capture.session.process.pid} · exact captured session` : undefined}/>{/if}
  {#if capture?.kind === 'launch_isolated'}<p>Isolated data mode: {capture.storeMode}.</p>{/if}
  {#if capture && (capture.kind === 'launch_ordinary' || capture.kind === 'launch_isolated') && capture.unrecognizedRuntimeChoice === 'allow_once'}
    <p>Unrecognized runtime consent applies to this attempt only.</p>
  {/if}
  {#if plan}<p>Proposed effects:</p><ul>{#each plan.semantics.effects as effect}<li>{effect.replaceAll('_', ' ')}</li>{/each}</ul>{/if}
  {#if summary}
    <ul aria-label="Captured action details">{#each summary.lines as line}<li>{line}</li>{/each}</ul>
    {#if summary.warning}<Notice title="Review this effect" tone="warning"><p>{summary.warning}</p></Notice>{/if}
  {/if}
  <p>{$reviewController.notice}</p>
  {#snippet actions()}<Button variant="primary" disabled={phase !== 'review'} onclick={async () => { await reviewController.confirm(); }}>Confirm action</Button>{/snippet}
</Dialog>
{#if phase === 'uncertain' || phase === 'observing'}
  <Notice title={phase === 'uncertain' ? 'Action outcome unconfirmed' : 'Action admitted'} tone={phase === 'uncertain' ? 'warning' : 'info'} live="polite">
    <p>{$reviewController.notice}</p>
    {#if phase === 'observing'}<Progress {...progress}/>{/if}
    {#snippet actions()}
      {#if phase === 'uncertain'}<Button onclick={async () => { await reviewController.replay(); }}>Replay exact action</Button>
      {:else}<Button onclick={async () => { await reviewController.reconcile(); }}>Refresh action outcome</Button>{/if}
    {/snippet}
  </Notice>
{:else if phase === 'idle' && $reviewController.notice}<p role="status" class="action-result">{$reviewController.notice}</p>{/if}
<style>p, li { overflow-wrap: anywhere; } .action-result { color: var(--bridge-muted); }</style>
