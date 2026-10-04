<script lang="ts">
  import { useBridge } from '../app';
  import { Button, Dialog, Notice } from '../components';
  import type { BridgeFacade } from '../state';
  import { editSummary } from './action-presentation';
  export interface Props { facade?: BridgeFacade; }
  let { facade = useBridge() }: Props = $props();
  const phase = $derived($facade.transition.kind);
  const pending = $derived(!!$facade.work.pendingNavigation);
  const inPlace = $derived($facade.reviewPurpose === 'in_place');
  const requested = $derived(pending || inPlace);
  const open = $derived(requested && ['idle', 'preparing', 'review', 'admitting', 'discard_review'].includes(phase));
  const busy = $derived(phase === 'preparing' || phase === 'admitting');
</script>
<Dialog {open} title={phase === 'review' ? 'Review Save' : phase === 'discard_review' ? 'Review Discard' : 'Unsaved changes'}
  description={phase === 'discard_review' ? 'Confirm discarding this captured draft. The selected target stays here.' : phase === 'review' ? inPlace ? 'Confirm these changes for the selected target. They stay here until Save finishes.' : 'Confirm these changes before switching target or closing Bridge.' : 'Save your changes, discard the reviewed draft, or stay with the current target.'}
  {busy} onstay={() => { facade.stay(); }}>
  <p>{phase === 'review' || phase === 'discard_review' ? `${$facade.work.edits.length} captured change${$facade.work.edits.length === 1 ? '' : 's'}. ${inPlace ? 'The current target remains selected.' : 'The requested transition waits for completed Save.'}`
    : busy ? 'Waiting for the captured draft decision. Your changes remain here.' : 'The current draft and target remain selected until this decision is confirmed.'}</p>
  {#if phase === 'review' || phase === 'discard_review'}<ul>{#each $facade.work.edits as edit}
    <li>{'fieldId' in edit ? $facade.work.draft?.schema.fields.find(field => field.fieldId === edit.fieldId)?.path.join('.') ?? 'Setting' : 'Data Sync'}: {editSummary(edit)}</li>
  {/each}</ul>{/if}
  {#if $facade.notice && !busy}<p class="feedback">{$facade.notice}</p>{/if}
  {#snippet actions()}
    {#if phase === 'discard_review'}<Button variant="danger" onclick={async () => { await facade.confirmDiscard(); }}>Confirm Discard</Button>
    {:else if phase === 'review'}<Button variant="primary" onclick={async () => { await facade.commitSave(); }}>Confirm Save</Button>
    {:else}<Button variant="primary" onclick={async () => { await facade.prepareSave(); }}>Save</Button>
      <Button variant="danger" onclick={async () => { await facade.discard(); }}>Discard</Button>{/if}
  {/snippet}
</Dialog>
{#if requested && (phase === 'uncertain' || phase === 'observing')}
  <Notice title={phase === 'uncertain' ? 'Save outcome unconfirmed' : 'Save admitted'} tone="warning" live="polite">
    <p>{$facade.notice} {inPlace ? 'The current target and captured draft remain selected.' : 'The requested transition remains pending.'}</p>
    {#snippet actions()}
      {#if phase === 'uncertain'}<Button onclick={async () => { await facade.replaySave(); }}>Replay exact Save</Button>
      {:else}<Button onclick={async () => { await facade.reconcileSave(); }}>Refresh Save outcome</Button>{/if}
    {/snippet}
  </Notice>
{/if}
<style>.feedback { color: var(--bridge-muted); } p { overflow-wrap: anywhere; }</style>
