<script lang="ts">
  import { onMount } from 'svelte';
  import App from '../../App.svelte';
  import { Button, Select } from '../../components';
  import { createHomeSession, homeModes, type HomeMode, type HomeSession, type DevelopmentDraftOutcome } from '../../mocks/home-session';
  import type { ScreenState, ActionReviewState } from '../../state';
  import type { MockState } from '../../mocks/scenario-transport';
  import '../../styles/tokens.css';
  let mode = $state<HomeMode>('ordinary_ready'), draftOutcome = $state<DevelopmentDraftOutcome>('save');
  let session = $state<HomeSession>(), screen = $state<ScreenState>(), action = $state<ActionReviewState>(), delivery = $state<MockState>();
  let automatic = $state(true), error = $state(''), loading = $state(true), elapsed = $state(0), requestCount = $state(0);
  let generation = 0, destroyed = false, stops: (() => void)[] = [];
  const labels: Record<HomeMode, string> = { ordinary_ready: 'Ordinary ready', isolated_ready: 'Isolated ready', focus_sessions: 'Focus and multiple sessions',
    launch_uncertain: 'Launch delivery uncertain', unknown: 'Unknown and partial observations', missing: 'Missing installation', offline: 'Offline availability', recovery: 'Recovery required', dirty_draft: 'Dirty draft navigation' };
  function cleanup(): void { stops.forEach(stop => stop()); stops = []; session?.dispose(); session = undefined; screen = undefined; action = undefined; delivery = undefined; }
  async function reset(): Promise<void> {
    const active = ++generation; cleanup(); loading = true; error = ''; elapsed = 0; requestCount = 0;
    try {
      const created = await createHomeSession(mode, { draftOutcome });
      if (destroyed || active !== generation) { created.dispose(); return; }
      session = created;
      stops.push(created.facade.subscribe(value => { if (active === generation) screen = value; }));
      stops.push(created.facade.actions.subscribe(value => { if (active === generation) action = value; }));
      stops.push(created.transport.subscribeState(value => { if (active === generation) { delivery = value; requestCount = created.requests.length; } }));
    } catch { if (active === generation) error = 'The shared Home fixture composition could not be initialized.'; }
    finally { if (active === generation) loading = false; }
  }
  async function tick(): Promise<void> { const current = session; await current?.tick(); if (session === current && current) elapsed = current.clock.now; }
  async function settle(): Promise<void> {
    const current = session;
    try { await current?.settle(); } catch { if (session === current) error = 'The bounded development clock could not settle.'; }
    if (session === current && current) elapsed = current.clock.now;
  }
  onMount(() => {
    void reset();
    const timer = setInterval(() => { const current = session; if (automatic && current && !current.clock.disposed) { current.clock.advanceBy(20); elapsed = current.clock.now; requestCount = current.requests.length; } }, 20);
    return () => { destroyed = true; generation++; clearInterval(timer); cleanup(); };
  });
</script>
<section class="bridge-theme development" data-theme="system" aria-label="Development Home preview">
  <div class="heading"><div><h1>Home preview</h1><p>Shared synthetic fixtures · browser only · no game or native host execution</p></div><Button onclick={reset}>Reset Home scenario</Button></div>
  <div class="controls">
    <Select id="home-preview-mode" label="Home scenario" value={mode} options={homeModes.map(value => ({ value, label: labels[value] }))} onchange={value => { mode = value as HomeMode; void reset(); }}/>
    <Select id="home-preview-outcome" label="Development draft outcome" value={draftOutcome} options={[{value:'save',label:'Reviewed Save completion'},{value:'discard',label:'Confirmed Discard'}]} disabled={mode !== 'dirty_draft'} onchange={value => { draftOutcome = value as DevelopmentDraftOutcome; void reset(); }}/>
    <div class="clock"><label><input type="checkbox" bind:checked={automatic}/>Advance fixture time automatically</label><div><Button onclick={tick} disabled={!session}>Tick fixture clock</Button><Button onclick={settle} disabled={!session}>Settle fixture observations</Button></div></div>
  </div>
  <p class="instructions">{session?.instructions ?? (loading ? 'Loading shared fixtures…' : error)}</p>
  {#if error}<p role="alert">{error}</p>{/if}
  <dl class="diagnostics" aria-label="Development fixture diagnostics">
    <div><dt>Action and Save state</dt><dd data-testid="home-preview-action-state">action: {action?.transition.kind ?? 'idle'}; save: {screen?.transition.kind ?? 'idle'}</dd></div>
    <div><dt>Observation confidence</dt><dd data-testid="home-preview-confidence">{screen?.observations.confidence ?? 'uninitialized'}</dd></div>
    <div><dt>Applied target</dt><dd data-testid="home-preview-selection">{screen?.work.selector ? JSON.stringify(screen.work.selector) : 'Not selected'}</dd></div>
    <div><dt>Draft</dt><dd data-testid="home-preview-draft">revision: {screen?.work.draft?.draft.revision ?? 'none'}; edits: {screen?.work.edits.length ?? 0}; dirty: {String(screen?.work.dirty ?? false)}</dd></div>
    <div><dt>View and queued navigation</dt><dd data-testid="home-preview-navigation">view: {screen?.work.view ?? 'home'}; queued: {screen?.work.pendingNavigation?.kind ?? 'none'}; close: {String(screen?.work.closeRequested ?? false)}</dd></div>
    <div><dt>Script position and faults</dt><dd data-testid="home-preview-script">position: {delivery?.position ?? 0}/{session?.script.steps.length ?? 0}; pending: {delivery?.pendingExchanges ?? 0}; fault: {delivery?.lastFault ?? 'none'}; elapsed: {elapsed} ms</dd></div>
  </dl>
  <details><summary>Development requests and provenance</summary><p>Fixture combinations and availability variants are scripted browser demonstrations. Accepted review captures and digests remain unchanged. An observation is not native qualification.</p>
    <ol data-testid="home-preview-requests" aria-label="Recorded fixture requests">{#each session?.requests.slice(0, requestCount) ?? [] as record}<li>{record.method} · {record.requestId}</li>{/each}</ol>
    <ul data-testid="home-preview-provenance" aria-label="Shared fixture SHA-256 provenance">{#each session?.provenance ?? [] as source}<li>{source.id}: {source.sha256}</li>{/each}</ul>
  </details>
</section>
{#if session}{#key session}<App facade={session.facade}/>{/key}{/if}
<style>
  .development { padding: 1.25rem clamp(1.25rem,4vw,3rem); border-block-end: 2px solid var(--bridge-border); display: grid; gap: 1rem; min-inline-size: 0; }
  .heading { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; } h1 { font-size: 1.15rem; margin: 0 0 .35rem; } p { margin: 0; color: var(--bridge-muted); overflow-wrap: anywhere; }
  .controls { display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: 1rem; } .clock, .clock > div { display: flex; flex-wrap: wrap; align-items: center; gap: .75rem; } label { display: inline-flex; gap: .5rem; align-items: center; overflow-wrap: anywhere; }
  .diagnostics { margin: 0; display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: .6rem 1.5rem; font-size: .85rem; } dt { color: var(--bridge-muted); } dd { margin: .2rem 0 0; overflow-wrap: anywhere; }
  summary { cursor: pointer; color: var(--bridge-muted); padding-block: .4rem; } li { overflow-wrap: anywhere; } details ul { font-family: monospace; font-size: .8rem; } .instructions { max-inline-size: 80rem; }
  @media(max-width:48rem) { .controls,.diagnostics { grid-template-columns: minmax(0,1fr); } }
</style>
