<script lang="ts">
  import { onDestroy, onMount, untrack } from 'svelte';
  import { useBridge } from '../../app';
  import { Button, Notice, Select, TargetSummary } from '../../components';
  import type { BridgeFacade } from '../../state';
  import type { InheritedBoolean, SupportedPlatform, SyncMode } from '../../generated/protocol';
  import { targetLabels } from '../home/presentation';
  import { SettingsController } from '../settings/controller';
  import SchemaField from '../settings/SchemaField.svelte';
  import { applyLabel, draftHostDrift, draftTargetDrift, label, stagedApply } from '../settings/presentation';
  import { SyncController } from './controller';
  import { desiredFeed, desiredProxy, destinationRows, resolvedFeed } from './presentation';
  export interface Props { facade?: BridgeFacade; platform?: SupportedPlatform; controller?: SettingsController; onreview?: () => void | Promise<void>; ondiscard?: () => void | Promise<void>; }
  let { facade = useBridge(), platform, controller: supplied, onreview, ondiscard }: Props = $props();
  const controller = untrack(() => supplied ?? new SettingsController(facade, platform));
  const ownController = untrack(() => !supplied), sync = new SyncController(controller);
  let mode = $state('');
  let heading = $state<HTMLHeadingElement>();
  let discardReviewed = false;
  let discardDraft: typeof facade.work.state.draft;
  const schema = $derived($facade.work.draft?.schema), definitions = $derived(schema?.sync.filter(type => type.exposure !== 'hidden') ?? []);
  const createModes = $derived(definitions.filter(type => type.exposure === 'creatable'));
  const selected = $derived(createModes.find(type => type.mode === mode));
  const rows = $derived(destinationRows($facade.work, $controller.document));
  const labels = $derived(targetLabels($facade.work.draft?.draft.document.target ?? $facade.work.binding, $facade.work.selector, $facade.observations));
  const blocked = $derived.by(() => { $facade; $controller; return controller.blocked || $controller.busy; });
  const timing = $derived(stagedApply($facade.work));
  const saveBlocked = $derived(blocked || !$facade.work.dirty || !!$facade.work.publicInputs?.length || $controller.document?.preservation && $controller.document.preservation !== 'supported');
  const preparedEndpoint = $derived(selected && $facade.work.edits.some(edit => edit.kind === 'set_private' && edit.fieldId === selected.endpointFieldId));
  const preparedSecret = $derived(selected && $facade.work.edits.some(edit => edit.kind === 'replace_secret' && edit.fieldId === selected.secretFieldId));
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
<section class="sync" aria-label="Data Sync workspace">
  <header><div><h1 bind:this={heading} tabindex="-1">Data Sync</h1><p class="helper">Edit only modes and feeds supported by the active provider schema. Settings shares this draft.</p></div>
    <Button busy={$controller.busy} disabled={!$facade.work.selector || $facade.work.transitionBusy} onclick={async () => { await controller.refresh(); }}>Refresh configuration</Button></header>
  <TargetSummary installation={labels.installation} profile={labels.profile} status={$facade.work.draft ? 'Shared configuration draft' : 'No configuration draft'}/>
  {#if !$facade.work.selector}<Notice title="Choose a target first"><p>Select an installation and profile in Shuttle Bay.</p></Notice>{/if}
  {#if $controller.busy}<Notice title="Reading Data Sync configuration" live="polite"><p>Current staged changes remain retained.</p></Notice>{/if}
  {#if $controller.notice}<Notice title={$controller.notice} tone="warning"><p>Endpoint, token and proxy values stay hidden here. Replace them through secure entry.</p></Notice>{/if}
  {#if $facade.notice}<Notice title={$facade.notice}/>{/if}
  {#if $facade.work.draftConflict || $facade.work.draft?.state === 'stale'}<Notice title="Configuration changed outside this draft" tone="warning"><p>Your changes are retained. Reconcile the document and schema before Save.</p></Notice>{/if}
  {#if draftTargetDrift($facade.work)}<Notice title="Target observation differs from this draft" tone="warning"><p>The summary above identifies the retained draft target. Reconcile the updated observation before editing or Save.</p></Notice>{/if}
  {#if draftHostDrift($facade.work, $facade.observations.cursor?.hostEpoch)}<Notice title="Bridge connection changed" tone="warning" live="polite"><p>This draft belongs to an earlier Bridge host. Your changes remain retained. Reconcile the current connection before editing or Save.</p></Notice>{/if}
  {#if $facade.work.baselineRefreshRequired}<Notice title="Refresh configuration" tone="warning"><p>Refresh configuration after Save or Discard before making more changes.</p><Button onclick={async () => { await facade.reloadBaseline(); await controller.refresh(); }}>Retry configuration refresh</Button></Notice>{/if}
  {#if schema}
    {#if !definitions.length}<Notice title="Data Sync is unavailable in this schema"><p>This provider publishes no visible Data Sync modes for this configuration.</p></Notice>{/if}
    <section class="destinations" aria-labelledby="sync-destinations-title"><h2 id="sync-destinations-title">Destinations</h2>
      {#if !rows.length}<p class="helper">{$controller.document ? 'No destinations are configured or staged.' : 'Saved destinations have not been observed. Refresh configuration.'}</p>{/if}
      {#each rows as row, index (row.destination.id)}
        <article class="destination" aria-labelledby={`sync-destination-${index}`}>
          <header><h3 id={`sync-destination-${index}`}>Destination {index + 1} · {label(row.destination.mode)}</h3><p>{row.removed ? 'Removal staged' : row.added ? 'New destination staged' : 'Saved destination'}</p></header>
          <p class="helper">Endpoint is protected. {row.destination.secretConfigured ? 'A secret is configured or staged.' : 'No configured secret is observed.'}</p>
          {#if !row.definition || row.definition.exposure === 'hidden'}<Notice title="Editing unavailable for this mode" tone="warning"><p>The schema does not expose editable fields for this destination.</p></Notice>
          {:else if row.removed}<Button disabled={blocked} ariaLabel={`Undo removal of destination ${index + 1}`} onclick={() => { sync.undoRemoval(row.destination.id); }}>Undo destination removal</Button>
          {:else}
            {#each row.definition.feeds as feedId, feedIndex (feedId)}
              <Select id={`sync-destination-${index}-feed-${feedIndex}`} label={label(feedId)} value={desiredFeed($facade.work, row, feedId)} disabled={blocked}
                description={`${resolvedFeed(row, feedId)}. Inherit keeps the provider default.`} options={[{value:'inherit',label:'Inherit'},{value:'on',label:'On'},{value:'off',label:'Off'}]}
                onchange={value => { sync.setFeed(row.destination.id, feedId, value as InheritedBoolean); }}/>
            {/each}
            <Select id={`sync-destination-${index}-proxy`} label="Proxy preference" value={desiredProxy($facade.work, row).kind} disabled={blocked}
              description="Desired configuration and observed effective proxy are separate facts." options={[...(row.definition.inheritsGlobalProxy ? [{value:'global',label:'Inherit global proxy'}] : []),
                {value:'none',label:'No proxy'},...(desiredProxy($facade.work, row).kind === 'custom' ? [{value:'custom',label:'Protected custom proxy',disabled:true}] : [])]}
              onchange={value => { if (value === 'global' || value === 'none') sync.setProxy(row.destination.id, {kind:value}); }}/>
            <p class="helper">{row.destination.resolvedProxy.status === 'observed' ? 'An effective proxy preference was observed.' : row.destination.resolvedProxy.status === 'unknown' ? 'Effective proxy preference is unknown.' : 'Effective proxy preference is unavailable.'}</p>
            {#if row.definition.proxyFieldId}
              {@const proxyField = schema.fields.find(field => field.fieldId === row.definition?.proxyFieldId)}
              {#if proxyField}<SchemaField field={proxyField} work={$facade.work} {controller} document={$controller.document} {platform} index={schema.fields.indexOf(proxyField)} idPrefix={`sync-proxy-${index}`} disabled={blocked}/>
                <Button disabled={blocked || !sync.capturedProxy(row.destination.mode)} onclick={() => { const value = sync.capturedProxy(row.destination.mode); if (value) sync.setProxy(row.destination.id, value); }}>Use captured proxy for this destination</Button>{/if}
            {/if}
            <Button disabled={blocked} ariaLabel={`Stage removal of destination ${index + 1}`} onclick={() => { sync.remove(row.destination.id); }}>Stage destination removal</Button>
          {/if}
        </article>
      {/each}
    </section>
    <section class="create" aria-labelledby="sync-create-title"><h2 id="sync-create-title">Add a destination</h2>
      {#if createModes.length}
        <Select id="sync-create-mode" label="Data Sync mode" bind:value={mode} disabled={blocked} options={[{value:'',label:'Choose a supported mode'},...createModes.map(type => ({value:type.mode,label:label(type.mode)}))]}/>
        {#if selected}
          <p class="helper">Capture the endpoint and secret in the protected entry flow. This stage creates no network connection.</p>
          {#each [selected.endpointFieldId, selected.secretFieldId] as fieldId}
            {@const field = schema.fields.find(field => field.fieldId === fieldId)}
            {#if field}<SchemaField {field} work={$facade.work} {controller} document={$controller.document} {platform} index={schema.fields.indexOf(field)} idPrefix="sync-create" disabled={blocked}/>{/if}
          {/each}
          <Button disabled={blocked || !preparedEndpoint || !preparedSecret} onclick={() => { sync.create(selected.mode as SyncMode); }}>Stage new destination</Button>
        {/if}
      {:else}<p class="helper">This schema allows editing existing destinations only. It does not expose destination creation.</p>{/if}
    </section>
    <aside class="draft" aria-label="Shared staged configuration"><h2>{$facade.work.dirty ? 'Unsaved changes' : 'No unsaved changes'}</h2><p>{$facade.work.edits.length} staged edits across Settings and Data Sync.</p>
      {#if $facade.work.publicInputs?.length}<p class="error">Unfinished numeric input is retained in Settings. Complete or reset it before Save.</p>{/if}
      {#each timing as value}<p>{applyLabel(value)}</p>{/each}
      <div class="actions"><div use:registerFocus={'sync-save'}><Button variant="primary" disabled={!!saveBlocked} onclick={async () => { if (onreview) await onreview(); else await facade.prepareSaveInPlace({}, 'sync-save'); }}>Review Save</Button></div>
        <div use:registerFocus={'sync-discard'}><Button disabled={blocked || !$facade.work.dirty} onclick={async () => { if (ondiscard) await ondiscard(); else facade.prepareDiscardInPlace('sync-discard'); }}>Review Discard</Button></div></div>
    </aside>
  {/if}
</section>
<style>
  .sync { display: grid; gap: 1.2rem; min-inline-size: 0; max-inline-size: 76rem; margin-inline: auto; } header { display: flex; flex-wrap: wrap; gap: 1rem; justify-content: space-between; }
  h1 { margin: 0 0 .5rem; font-size: 2rem; } h2, h3 { margin: 0; font-size: 1.1rem; } p { margin: 0; overflow-wrap: anywhere; } .helper { color: var(--bridge-muted); }
  .destinations, .create, .destination, .draft { display: grid; gap: .85rem; min-inline-size: 0; } .destination, .create, .draft { padding: 1.1rem; border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius); }
  .actions { display: flex; flex-wrap: wrap; gap: .7rem; } .error { color: var(--bridge-danger-text); }
</style>
