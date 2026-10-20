<script lang="ts">
  import { onMount } from 'svelte';
  import Shell from '../Shell.svelte';
  import { Button, Dialog, Field, Navigation, Notice, Progress, Select, TargetSummary } from '../../components';
  import type { ScreenState } from '../../state';
  import type { WorkspaceView } from '../../client';
  import type { MockState } from '../../mocks/scenario-transport';
  import { createGallerySession, type GalleryMode, type GallerySession } from './session';
  import './hot-reload.css';

  type Theme = 'system' | 'light' | 'dark';
  type Platform = 'windows' | 'macos';
  type Confidence = 'current' | 'unknown' | 'stale';
  let theme = $state<Theme>('system');
  let platform = $state<Platform>('windows');
  let confidence = $state<Confidence>('current');
  let mode = $state<GalleryMode>('save_success');
  let longLabels = $state(true);
  let validation = $state(false);
  let displayName = $state('Fleet Command — primary desktop installation');
  let notification = $state('summary');
  let session = $state<GallerySession>();
  let screen = $state<ScreenState>();
  let delivery = $state<MockState>();
  let loading = $state(true);
  let error = $state('');
  let elapsed = $state(0);
  let generation = 0;
  let destroyed = false;
  let stops: (() => void)[] = [];
  const modeOptions = [
    { value: 'save_success', label: 'Save success' }, { value: 'save_uncertain', label: 'Save uncertain' },
    { value: 'discard_success', label: 'Discard success' }, { value: 'discard_refused', label: 'Discard refused' },
    { value: 'stay', label: 'Stay' },
  ];
  const navigationItems = [{ id: 'home', label: 'Home' }, { id: 'engineering', label: 'Engineering' },
    { id: 'settings', label: 'Settings' }, { id: 'history', label: 'History' }];
  const discardMode = $derived(mode === 'discard_success' || mode === 'discard_refused');
  const transition = $derived(screen?.transition.kind ?? 'idle');
  const busy = $derived(transition === 'preparing' || transition === 'admitting');
  const modalOpen = $derived(!!screen?.work.pendingNavigation && ['idle', 'preparing', 'review', 'admitting'].includes(transition));
  const completed = $derived(!!screen?.work.closeRequested || !!screen?.work.selector);
  const saved = $derived(completed && screen?.notice === 'Changes saved.');
  const receipt = $derived(screen?.work.closeRequested ? 'Close ready' : screen?.work.selector ? 'Target change ready'
    : screen?.work.pendingNavigation ? 'Navigation queued' : screen?.work.dirty ? 'Changes retained' : 'No staged changes');
  const modeDescription = $derived(discardMode
    ? 'This scenario starts with the shared dirty backend draft at revision 2. Discard uses its exact request and receipt.'
    : mode === 'save_uncertain' ? 'The first commit reply is lost. Timeout preserves the exact submission; replay is an explicit action.'
    : mode === 'stay' ? 'Stage an edit, request navigation, then Stay or press Escape. No command is submitted by Stay.'
    : 'Stage an edit, review Save, confirm it explicitly, then observe completion. Admission alone keeps navigation queued.');

  function cleanup(): void {
    stops.forEach(stop => stop()); stops = [];
    session?.dispose(); session = undefined; screen = undefined; delivery = undefined;
  }
  async function reset(): Promise<void> {
    const active = ++generation; cleanup(); loading = true; error = ''; elapsed = 0;
    try {
      const created = await createGallerySession(mode);
      if (destroyed || active !== generation) { created.dispose(); return; }
      session = created;
      created.facade.navigate('engineering');
      stops.push(created.facade.subscribe(value => { if (active === generation) screen = value; }));
      stops.push(created.transport.subscribeState(value => { if (active === generation) delivery = value; }));
      stops.push(created.facade.focus.register('close-opener', () => document.getElementById('gallery-close')),
        created.facade.focus.register('target-opener', () => document.getElementById('gallery-target')));
    } catch { if (active === generation) error = 'The shared fixture gallery could not be initialized.'; }
    finally { if (active === generation) loading = false; }
  }
  function selectMode(value: string): void { mode = value as GalleryMode; void reset(); }
  function selectView(value: string): void { session?.facade.navigate(value as WorkspaceView); }
  onMount(() => {
    void reset();
    // Elapsed time advances the deterministic delivery clock only. It grants no
    // native authority and does not synthesize operation completion.
    const timer = setInterval(() => {
      const current = session;
      if (current && !current.clock.disposed) { current.clock.advanceBy(20); elapsed = current.clock.now; }
    }, 20);
    return () => { destroyed = true; generation++; clearInterval(timer); cleanup(); };
  });
</script>

{#if session}
  <Shell {theme} {platform} announcements={session.facade.announcements}>
    {#snippet header()}<span class="gallery-badge">Development gallery</span><span class="shell-detail">Browser only</span>{/snippet}
    {#snippet navigation()}<Navigation label="Bridge sections" items={navigationItems} current={screen?.work.view ?? 'engineering'} onselect={selectView}/>{/snippet}
    {#snippet children()}
      <div class="gallery" data-testid="gallery-root">
        <div class="page-heading"><div><p class="eyebrow">Design and interaction</p><h1>Engineering</h1><p class="subtitle">A clear view of the target, changes, and the next decision.</p></div><span class="receipt" data-testid="gallery-receipt">{receipt}</span></div>
        <Notice title="Shared synthetic fixture gallery" tone="info">
          <p>No game or native host is active. This separate development entry exercises the same facade and components used by the application.</p>
        </Notice>
        <section class="controls" aria-labelledby="gallery-controls-title">
          <div class="section-heading"><h2 id="gallery-controls-title">Presentation and scenario</h2><Button onclick={reset}>Reset scenario</Button></div>
          <div class="control-grid">
            <Select id="gallery-platform" label="Platform" value={platform} options={[{value:'windows',label:'Windows'},{value:'macos',label:'macOS'}]} onchange={value => { platform = value as Platform; }}/>
            <Select id="gallery-theme" label="Theme" value={theme} options={[{value:'system',label:'System'},{value:'light',label:'Light'},{value:'dark',label:'Dark'}]} onchange={value => { theme = value as Theme; }}/>
            <Select id="gallery-scenario" label="Scenario" value={mode} options={modeOptions} onchange={selectMode}/>
            <Select id="gallery-confidence" label="Progress confidence" value={confidence} options={[{value:'current',label:'Known'},{value:'unknown',label:'Unknown'},{value:'stale',label:'Stale'}]} onchange={value => { confidence = value as Confidence; }}/>
          </div>
          <div class="toggle-row"><label><input type="checkbox" bind:checked={longLabels}/>Long target labels</label><label><input type="checkbox" bind:checked={validation}/>Show validation feedback</label></div>
          <p class="helper">{modeDescription} Scenario actions remain visible; the active script enables the corresponding Save or Discard path.</p>
        </section>
        <div class="workspace-grid">
          <section class="work-section" aria-labelledby="gallery-target-title">
            <h2 id="gallery-target-title">Your current target</h2>
            <TargetSummary installation={longLabels ? 'Fleet Command — primary desktop installation on the shared family workstation with a very long descriptive name' : 'Fleet Command — desktop'}
              profile={longLabels ? 'Ordinary profile — current settings for the selected installation, preserved while another target is reviewed' : 'Ordinary profile'} status="Synthetic target">
              <p class="helper">These display labels preview wrapping. Commands retain the shared fixture's captured target and draft bindings.</p>
            </TargetSummary>
            <div class="action-row">
              <Button id="gallery-stage" variant="primary" disabled={!!screen?.work.transitionBusy || completed || !!screen?.work.dirty} onclick={() => { session?.facade.stage(session.edits); }}>Stage shared boolean edit</Button>
              <Button id="gallery-close" disabled={!!screen?.work.pendingNavigation || !!screen?.work.transitionBusy || completed} onclick={() => { session?.facade.requestClose('close-opener'); }}>Request close</Button>
              <Button id="gallery-target" disabled={!!screen?.work.pendingNavigation || !!screen?.work.transitionBusy || completed} onclick={() => { if (session) session.facade.requestTarget(session.target, 'target-opener'); }}>Change target</Button>
            </div>
            <div class="draft-summary" data-testid="gallery-draft"><span>Draft revision <strong>{screen?.work.draft?.draft.revision ?? '—'}</strong></span><span>Captured edits <strong>{screen?.work.edits.length ?? 0}</strong></span></div>
          </section>
          <section class="work-section" aria-labelledby="gallery-status-title">
            <h2 id="gallery-status-title">Change status</h2>
            <Progress label="Save operation" value={saved ? 1 : undefined} max={1} {confidence}
              detail={transition === 'observing' ? 'Commit admitted. The operation outcome has not yet been observed.' : transition === 'uncertain' ? 'Delivery is uncertain. No Save or cancellation is inferred.' : saved ? 'The completed Save released its exact queued navigation.' : completed ? 'Navigation is ready. No Save progress is inferred from this decision.' : 'A percentage becomes known only when the scripted Save completes.'}/>
            <Notice title={screen?.notice || 'Changes stay with their captured target'} tone={transition === 'uncertain' ? 'warning' : completed ? 'success' : 'info'}>
              <p>{screen?.work.pendingNavigation ? 'Navigation remains queued until the reviewed decision is confirmed.' : screen?.work.dirty ? 'Your staged edit is retained.' : 'Stage the shared edit to try Save, Discard, or Stay.'}</p>
            </Notice>
            {#if transition === 'observing'}<Button variant="primary" onclick={async () => { await session?.facade.reconcileSave(); }}>Observe completed Save</Button>{/if}
            {#if transition === 'uncertain'}<Button variant="primary" onclick={async () => { await session?.facade.replaySave(); }}>Replay exact Save</Button>{/if}
            <dl class="state-details"><div><dt>Transition</dt><dd data-testid="gallery-transition">{transition}</dd></div><div><dt>Navigation</dt><dd data-testid="gallery-navigation">{screen?.work.pendingNavigation?.kind ?? (completed ? 'ready' : 'none')}</dd></div></dl>
          </section>
        </div>
        <section class="form-preview" aria-labelledby="gallery-form-title"><div><p class="eyebrow">Presentation preview</p><h2 id="gallery-form-title">Readable fields and feedback</h2><p class="helper">These sample labels exercise form accessibility without changing the captured fixture edit.</p></div>
          <div class="form-grid"><Field id="gallery-display-name" label="Display name" bind:value={displayName} description="A name that remains readable at larger text sizes." error={validation ? 'Choose a shorter display name for this presentation preview.' : undefined} required/>
            <Select id="gallery-notification" label="Notification presentation" bind:value={notification} options={[{value:'off',label:'Off'},{value:'summary',label:'Summary of important changes and operation results'}]} description="A native selection control with a long option label."/>
          </div>
          <div class="action-row"><Button disabled>Unavailable action</Button><Button busy>Working</Button><Button variant="quiet" onclick={() => { session?.facade.announcements.announce('Presentation preview announced.'); }}>Announce preview</Button></div>
        </section>
        <details class="fixture-details"><summary>Development delivery details</summary><p>This is a scripted synthetic browser demonstration, not a native execution receipt. The commit admission is derived from the unchanged completed fixture's semantics.</p><dl class="state-details"><div><dt>Elapsed fixture time</dt><dd>{elapsed} ms</dd></div><div><dt>Pending transport observations</dt><dd>{delivery?.pendingExchanges ?? 0}</dd></div><div><dt>Replay records</dt><dd>{delivery?.replayCount ?? 0}</dd></div></dl></details>
        <div data-testid="gallery-dialog">
          <Dialog open={modalOpen} title={transition === 'review' ? 'Review Save' : 'Unsaved changes'} description={transition === 'review' ? 'Confirm the captured boolean edit before submitting Save.' : 'Save your changes, discard the reviewed draft, or stay with your current target.'} {busy} onstay={() => { session?.facade.stay(); }}>
            {#snippet children()}
              {#if transition === 'review'}<div class="review"><p><strong>Boolean setting: On</strong></p><p>The shared review captures one public edit for draft revision {screen?.work.draft?.draft.revision}. Confirm Save submits that exact review.</p></div>
              {:else}<p>{busy ? 'Waiting for the captured draft decision. Your changes remain here while observation is pending.' : 'Your captured change has not been saved. Stay returns focus to the control that opened this decision.'}</p>
                {#if screen?.notice && !busy}<Notice title={screen.notice} tone="warning" live="assertive"><p>The edit and queued navigation are retained.</p></Notice>{/if}
              {/if}
            {/snippet}
            {#snippet actions()}
              {#if transition === 'review'}<Button variant="primary" onclick={async () => { await session?.facade.commitSave(); }}>Confirm Save</Button>
              {:else}<Button variant="primary" disabled={discardMode || mode === 'stay'} onclick={async () => { await session?.facade.prepareSave(); }}>Save</Button>{/if}
              <Button variant="danger" disabled={!discardMode || transition === 'review'} onclick={async () => { await session?.facade.discard(); }}>Discard</Button>
            {/snippet}
          </Dialog>
        </div>
      </div>
    {/snippet}
  </Shell>
{:else}
  <div class="gallery-loading" data-testid="gallery-loading"><h1>Component gallery</h1><p role="status">{error || (loading ? 'Loading shared fixture frames…' : 'Gallery unavailable.')}</p><button type="button" onclick={reset}>Reset scenario</button></div>
{/if}

<style>
  .gallery { display: grid; gap: 1.75rem; max-inline-size: 78rem; margin-inline: auto; min-inline-size: 0; }
  .page-heading, .section-heading { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 1rem; }
  h1 { font-size: clamp(1.75rem, 3vw, 2.3rem); line-height: 1.2; margin: .25rem 0 .6rem; }
  h2 { font-size: 1.125rem; line-height: 1.4; margin: 0; }
  p { margin: 0; overflow-wrap: anywhere; }
  .eyebrow { color: var(--bridge-accent-text); font-size: .8125rem; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
  .subtitle, .helper, .shell-detail { color: var(--bridge-muted); }
  .helper { font-size: .9375rem; }
  .gallery-badge, .receipt { border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius); padding: .35rem .6rem; font-size: .875rem; overflow-wrap: anywhere; }
  .shell-detail { margin-inline-start: auto; font-size: .875rem; }
  .controls { display: grid; gap: 1.1rem; padding-block: .25rem 1.5rem; border-bottom: 1px solid var(--bridge-border); }
  .control-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 1rem; }
  .toggle-row, .action-row { display: flex; flex-wrap: wrap; align-items: center; gap: .75rem 1.15rem; }
  .toggle-row label { display: inline-flex; align-items: center; gap: .5rem; min-block-size: 2rem; overflow-wrap: anywhere; }
  input[type='checkbox'] { inline-size: 1.1rem; block-size: 1.1rem; accent-color: var(--bridge-accent); }
  .workspace-grid { display: grid; grid-template-columns: minmax(0, 1.15fr) minmax(0, 1fr); gap: 2rem; }
  .work-section { display: grid; align-content: start; gap: 1.1rem; min-inline-size: 0; }
  .draft-summary { display: flex; flex-wrap: wrap; gap: .5rem 1.5rem; color: var(--bridge-muted); font-size: .9375rem; }
  .draft-summary strong { color: var(--bridge-text); margin-inline-start: .25rem; }
  .state-details { display: grid; gap: .5rem; margin: 0; }
  .state-details > div { display: flex; flex-wrap: wrap; gap: .25rem .75rem; }
  dt { color: var(--bridge-muted); } dd { margin: 0; overflow-wrap: anywhere; }
  .form-preview { display: grid; gap: 1.15rem; padding-block-start: 1.5rem; border-top: 1px solid var(--bridge-border); }
  .form-preview h2 { margin-block: .3rem .5rem; }
  .form-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1rem; }
  .fixture-details { color: var(--bridge-muted); font-size: .875rem; overflow-wrap: anywhere; }
  summary { cursor: pointer; padding-block: .5rem; }
  .fixture-details p, .fixture-details dl { margin-block-start: .65rem; }
  .review { display: grid; gap: .75rem; }
  .gallery-loading { max-inline-size: 40rem; padding: 2rem; }
  @media (max-width: 72rem) { .control-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } .workspace-grid { grid-template-columns: minmax(0, 1fr); } }
  @media (max-width: 40rem) { .control-grid, .form-grid { grid-template-columns: minmax(0, 1fr); } }
</style>
