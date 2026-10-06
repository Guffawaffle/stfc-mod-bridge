<script lang="ts">
  import { onDestroy, onMount, untrack } from 'svelte';
  import { useBridge } from '../../app';
  import { Button, Field, Notice, Select, TargetSummary } from '../../components';
  import type { BridgeFacade } from '../../state';
  import type { SupportedPlatform } from '../../generated/protocol';
  import { targetLabels } from '../home/presentation';
  import { SettingsController } from './controller';
  import { applyLabel, categories, draftHostDrift, draftTargetDrift, filterFields, label, stagedApply, violationLabel } from './presentation';
  import SchemaField from './SchemaField.svelte';
  export interface Props { facade?: BridgeFacade; platform?: SupportedPlatform; controller?: SettingsController; onreview?: () => void | Promise<void>; ondiscard?: () => void | Promise<void>; }
  let { facade = useBridge(), platform, controller: supplied, onreview, ondiscard }: Props = $props();
  const controller = untrack(() => supplied ?? new SettingsController(facade, platform));
  const ownController = untrack(() => !supplied);
  let search = $state(''), category = $state('');
  let heading = $state<HTMLHeadingElement>();
  let discardReviewed = false;
  let discardDraft: typeof facade.work.state.draft;
  const fields = $derived($facade.work.draft?.schema.fields ?? []), categoryRows = $derived(categories(fields));
  const visible = $derived(filterFields(fields, search, category));
  const labels = $derived(targetLabels($facade.work.draft?.draft.document.target ?? $facade.work.binding, $facade.work.selector, $facade.observations));
  const timing = $derived(stagedApply($facade.work));
  const blocked = $derived.by(() => { $facade; $controller; return controller.blocked || $controller.busy; });
  const saveBlocked = $derived(blocked || !$facade.work.draft || !$facade.work.dirty || !!$facade.work.publicInputs?.length || $controller.document?.preservation && $controller.document.preservation !== 'supported');
  onMount(() => { if (ownController) void controller.refresh(); }); onDestroy(() => { if (ownController) controller.dispose(); });
  $effect(() => {
    const state = $facade;
    if (state.transition.kind === 'discard_review' && state.reviewPurpose === 'in_place') { discardReviewed = true; discardDraft = state.work.draft; }
    else if (discardReviewed && state.transition.kind === 'idle' && !state.work.transitionBusy) {
      discardReviewed = false;
      if (!state.work.dirty && state.work.draft !== discardDraft) {
        const target = heading;
        queueMicrotask(() => { if (target?.isConnected && facade.state.transition.kind === 'idle' && !facade.work.state.dirty && !facade.actions.blocksTransitions) target.focus({ preventScroll: true }); });
      }
    }
  });
  function registerFocus(node: HTMLElement, key: string) { const stop = facade.focus.register(key, () => node.querySelector('button')); return { destroy: stop }; }
</script>
<section class="settings" aria-label="Settings workspace">
  <header><div><h1 bind:this={heading} tabindex="-1">Settings</h1><p class="helper">Change provider settings and review them before saving. Settings and Data Sync share one draft.</p></div>
    <Button busy={$controller.busy} disabled={!$facade.work.selector || $facade.work.transitionBusy} onclick={async () => { await controller.refresh(); }}>Refresh configuration</Button></header>
  <TargetSummary installation={labels.installation} profile={labels.profile} status={$facade.work.draft ? 'Draft bound to this target' : 'No configuration draft'}/>
  {#if !$facade.work.selector}<Notice title="Choose a target first"><p>Select an installation and profile in Shuttle Bay before opening configuration.</p></Notice>{/if}
  {#if $controller.busy}<Notice title="Reading configuration" live="polite"><p>Opening a missing document creates no file. Your current changes remain retained.</p></Notice>{/if}
  {#if $controller.notice}<Notice title={$controller.notice} tone="warning"><p>Save always reviews the complete staged set.</p></Notice>{/if}
  {#if $facade.notice}<Notice title={$facade.notice}/>{/if}
  {#if $facade.work.draftConflict || $facade.work.draft?.state === 'stale'}<Notice title="Configuration changed outside this draft" tone="warning" live="polite"><p>Your changes are retained. Review the changed target, document or schema before retrying.</p></Notice>{/if}
  {#if draftTargetDrift($facade.work)}<Notice title="Target observation differs from this draft" tone="warning"><p>The summary above identifies the retained draft target. Reconcile the updated target observation before editing or Save.</p></Notice>{/if}
  {#if draftHostDrift($facade.work, $facade.observations.cursor?.hostEpoch)}<Notice title="Bridge connection changed" tone="warning" live="polite"><p>This draft belongs to an earlier Bridge host. Your changes remain retained. Reconcile the current connection before editing or Save.</p></Notice>{/if}
  {#if $facade.work.baselineRefreshRequired}<Notice title="Refresh configuration" tone="warning"><p>Refresh configuration after Save or Discard before making more changes.</p><Button onclick={async () => { await facade.reloadBaseline(); await controller.refresh(); }}>Retry configuration refresh</Button></Notice>{/if}
  {#if $facade.work.draft}
    <div class="filters"><Field id="settings-search" label="Search settings" type="search" bind:value={search} description="Search field names, categories and provider search terms."/>
      <Select id="settings-category" label="Category" bind:value={category} options={[{value:'',label:'All categories'},...categoryRows.map(value => ({value,label:label(value)}))]}/></div>
    <p class="helper" role="status">{visible.length} of {fields.length} fields shown.</p>
    {#if !visible.length}<Notice title="No matching fields"><p>Clear the search or choose another category.</p></Notice>{/if}
    <div class="fields">{#each visible as field (field.fieldId)}<SchemaField {field} work={$facade.work} {controller} document={$controller.document} {platform} index={fields.indexOf(field)} disabled={blocked}/>{/each}</div>
    {#if $facade.work.draft.validation.length}<Notice title="Review validation before Save" tone="danger"><ul>{#each $facade.work.draft.validation as violation}<li>{violation.fieldId ? label(violation.fieldId) + ': ' : ''}{violationLabel(violation.code)}</li>{/each}</ul></Notice>{/if}
    <aside class="draft" aria-label="Staged configuration"><h2>{$facade.work.dirty ? 'Unsaved changes' : 'No unsaved changes'}</h2>
      <p>{$facade.work.edits.length} staged edits across Settings and Data Sync.</p>
      {#if $facade.work.publicInputs?.length}<p class="error">{$facade.work.publicInputs.length} unfinished numeric input(s). Complete or reset them before Save.</p>{/if}
      {#each timing as value}<p>{applyLabel(value)}</p>{/each}
      <div class="actions"><div use:registerFocus={'settings-save'}><Button variant="primary" disabled={!!saveBlocked} onclick={async () => { if (onreview) await onreview(); else await facade.prepareSaveInPlace({}, 'settings-save'); }}>Review Save</Button></div>
        <div use:registerFocus={'settings-discard'}><Button disabled={blocked || !$facade.work.dirty} onclick={async () => { if (ondiscard) await ondiscard(); else facade.prepareDiscardInPlace('settings-discard'); }}>Review Discard</Button></div></div>
      <p class="helper">Removing an override uses the provider default. Save preserves unrelated configuration and validates all changes together.</p>
    </aside>
  {/if}
</section>
<style>
  .settings { display: grid; gap: 1.2rem; min-inline-size: 0; max-inline-size: 76rem; margin-inline: auto; } header { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 1rem; }
  h1 { margin: 0 0 .5rem; font-size: 2rem; } h2 { margin: 0; font-size: 1.1rem; } p { margin: 0; overflow-wrap: anywhere; } .helper { color: var(--bridge-muted); }
  .filters { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr); gap: 1rem; } .fields { display: grid; gap: 1rem; }
  .draft { display: grid; gap: .7rem; padding: 1.1rem; border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius); background: var(--bridge-surface); } .actions { display: flex; flex-wrap: wrap; gap: .7rem; }
  .error { color: var(--bridge-danger-text); } @media (max-width: 42rem) { .filters { grid-template-columns: minmax(0, 1fr); } }
</style>
