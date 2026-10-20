<script lang="ts">
  import { connectionSettings } from './connection';
  import { onMount, untrack } from 'svelte';
  import { useBridge } from '../../app';
  import { Button, Notice } from '../../components';
  import type { BridgeFacade } from '../../state';
  import type { DeepReadonly } from '../../client';
  import type { AstrometricsView } from '../../generated/protocol';
  let { facade = useBridge() }: { facade?: BridgeFacade } = $props();
  const client = untrack(() => facade.client), lifetime = new AbortController();
  let view = $state<DeepReadonly<AstrometricsView>>(), busy = $state(false), unavailable = $state(false);
  let active = false;
  async function refresh(): Promise<void> {
    if (busy || !active) return;
    busy = true;
    try {
      const outcome = await client.query('astrometrics', {}, { signal: lifetime.signal });
      if (!active) return;
      unavailable = outcome.kind !== 'result';
      if (outcome.kind === 'result') view = outcome.value;
    } finally { if (active) busy = false; }
  }
  async function setEnabled(enabled: boolean): Promise<void> {
    if (busy || !active) return;
    busy = true;
    try {
      const outcome = await client.command('set_astrometrics', { enabled }, { signal: lifetime.signal });
      if (!active) return;
      unavailable = outcome.kind !== 'result';
      if (outcome.kind === 'result') view = outcome.value;
    } finally { if (active) busy = false; }
  }
  function recordedTime(value: string | null | undefined): string {
    if (!value) return 'Time unavailable';
    const date = new Date(Number(value));
    return Number.isFinite(date.getTime()) ? date.toLocaleString() : 'Time unavailable';
  }
  const configuration = $derived(view ? connectionSettings(view) : '');
  onMount(() => {
    active = true; void refresh();
    const timer = setInterval(() => { void refresh(); }, 3000);
    return () => { active = false; clearInterval(timer); lifetime.abort(); };
  });
</script>

<section class="astrometrics" aria-label="Astrometrics">
  <header><div><p class="eyebrow">DIAGNOSTIC COMPANION</p><h1>Astrometrics</h1></div>
    <Button busy={busy} onclick={refresh}>Refresh timeline</Button></header>
  <p>Collect diagnostic events locally and inspect what happened. Collection is optional and starts off each time Bridge opens.</p>
  {#if unavailable}
    <Notice title="Astrometrics is unavailable" tone="warning"><p>The current connection cannot confirm collection status. Refresh to check; a lost response does not stop a collector that already started.</p></Notice>
  {:else if view?.notice === 'unavailable'}
    <Notice title="Collection could not be confirmed" tone="warning"><p>The local port or diagnostic store is unavailable. Existing records are retained.</p></Notice>
  {/if}
  <div class="controls">
    <p role="status">{view?.notice === 'unavailable' ? 'Collection needs attention' : view?.enabled ? 'Collecting while Bridge is open' : view ? 'Collection is off' : 'Checking collection status'}</p>
    <Button variant="primary" busy={busy} disabled={!view || unavailable} onclick={() => setEnabled(!view?.enabled)}>{view?.enabled ? 'Stop collection' : 'Start collection'}</Button>
  </div>
  <p class="helper">Other tabs keep collection running. Closing Bridge stops its receiver. The mod keeps its existing fire-and-forget behavior; missed events are not replayed.</p>
  {#if configuration}
    <details><summary>Connect the mod</summary>
      <p>Add this optional sync target to your mod configuration and restart the game when convenient. It receives only diagnostics; your existing sync targets keep their settings. Diagnostic capture must also be enabled in the mod.</p>
      <label for="astrometrics-config">Local connection settings, including your receiver token</label>
      <textarea id="astrometrics-config" readonly value={configuration} rows="20" spellcheck="false"></textarea>
      <p class="helper">Keep this token private. The receiver listens only on this computer.</p>
    </details>
  {/if}
  {#if view?.historyAvailable}
    <p>{view.storedEvents} stored events · {view.rejectedRequests} rejected requests in this Bridge session</p>
    <h2>Recent observations</h2>
    <p class="helper">Newest arrivals first, up to 30. Sequence numbers apply within one producer session and stream. Account identity is unconfirmed.</p>
    {#if !view.events.length}<p>No diagnostic observations are stored yet.</p>{/if}
    {#each view.events as event (event.rowId)}
      <article aria-label={`${event.stream}: ${event.eventType}`}>
        <h3>{event.eventType}</h3>
        <p class="helper">{event.stream} · sequence {event.sequence} · session {event.sourceSession}{event.utcMs ? ` · recorded UTC milliseconds ${event.utcMs}` : ''}</p>
        <p>{event.summary}</p>
        <details><summary>Recorded evidence{event.payloadTruncated ? ' (preview truncated)' : ''}</summary><pre>{event.payloadJson}</pre></details>
      </article>
    {/each}
  {:else if view}
    <p>Stored history is unavailable. Existing records have not been removed.</p>
  {/if}
</section>
<style>
  .astrometrics { display: grid; gap: 1.1rem; max-inline-size: 74rem; margin-inline: auto; min-inline-size: 0; }
  header, .controls { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 1rem; }
  h1, h2, h3, p { margin: 0; overflow-wrap: anywhere; }
  h1 { font-size: 2rem; } h2 { font-size: 1.25rem; } h3 { font-size: 1rem; }
  .eyebrow, .helper { color: var(--bridge-muted); } .eyebrow { font-size: .8125rem; letter-spacing: .08em; }
  article { display: grid; gap: .65rem; padding: 1rem; border: 1px solid var(--bridge-border); border-radius: .5rem; }
  details { display: grid; min-inline-size: 0; } summary { cursor: pointer; padding-block: .5rem; }
  textarea { box-sizing: border-box; inline-size: 100%; margin-block: .5rem; padding: .75rem; background: var(--bridge-surface); color: var(--bridge-text); border: 1px solid var(--bridge-border); }
  pre { white-space: pre-wrap; overflow-wrap: anywhere; max-inline-size: 100%; }
</style>
