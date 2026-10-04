<script lang="ts">
  import { onDestroy, onMount, untrack } from 'svelte';
  import { useBridge } from '../../app';
  import { Button, Notice, Select, TargetSummary } from '../../components';
  import { canonicalData, type DeepReadonly } from '../../client';
  import type { BridgeFacade } from '../../state';
  import type { MutationIntent, StoreMode, UnrecognizedRuntimeChoice } from '../../generated/protocol';
  import { HomeController } from './controller';
  import { availabilityText, installations, inventoryText, profileKey, profileLabel, profiles, selectorFor, sessions, sessionStatus, targetLabels } from './presentation';
  export interface Props { facade?: BridgeFacade; onreview?: (intent: DeepReadonly<MutationIntent>, focusKey: string) => void | Promise<void>;
    onmaintenance?: (destination: 'game' | 'runtime' | 'bridge') => void; }
  let { facade = useBridge(), onreview, onmaintenance }: Props = $props();
  const scopedFacade = untrack(() => facade), controller = new HomeController(scopedFacade), actions = scopedFacade.actions;
  const initial = scopedFacade.work.state.selector;
  let installationChoice = $state(initial?.installation.kind === 'registered' ? initial.installation.id : '');
  let profileChoice = $state(initial?.profile.kind === 'ordinary' ? 'ordinary' : initial?.profile.id ?? '');
  let selectionKey = $state(canonicalData(initial ?? null));
  let storeMode = $state(''), runtimeChoice = $state('reject');
  let refreshing = $state(false);
  const installationRows = $derived(installations($facade.observations));
  const profileRows = $derived(profiles($facade.observations));
  const sessionRows = $derived(sessions($facade.observations));
  const labels = $derived(targetLabels($facade.work.binding, $facade.work.selector, $facade.observations));
  const candidate = $derived(selectorFor(installationChoice, profileChoice, $facade.observations));
  const blocked = $derived.by(() => {
    const phase = $actions.transition.kind, transitionBusy = $facade.work.transitionBusy;
    return ['preparing', 'review', 'admitting', 'uncertain'].includes(phase) || transitionBusy;
  });
  const reviewBlocked = $derived.by(() => {
    const phase = $actions.transition.kind, transitionBusy = $facade.work.transitionBusy;
    return phase !== 'idle' || transitionBusy;
  });
  const launch = $derived.by(() => { $controller; $facade; return controller.launchIntent(storeMode ? storeMode as StoreMode : undefined, runtimeChoice as UnrecognizedRuntimeChoice); });
  $effect(() => {
    const selected = $facade.work.selector, key = canonicalData(selected ?? null);
    if (key !== selectionKey) { selectionKey = key; installationChoice = selected?.installation.kind === 'registered' ? selected.installation.id : '';
      profileChoice = selected?.profile.kind === 'ordinary' ? 'ordinary' : selected?.profile.id ?? ''; storeMode = ''; runtimeChoice = 'reject'; }
  });
  onMount(() => controller.start()); onDestroy(() => controller.dispose());
  function registerFocus(node: HTMLElement, key: string) { const stop = facade.focus.register(key, () => node.querySelector('button')); return { destroy: stop }; }
  async function review(intent: DeepReadonly<MutationIntent> | undefined, key: string): Promise<void> {
    if (!intent || reviewBlocked) return;
    if (onreview) await onreview(intent, key); else await actions.prepare(intent, key);
  }
  async function refresh(): Promise<void> {
    if (refreshing) return; refreshing = true;
    try { await facade.refresh(); await controller.inspectTarget(); } finally { refreshing = false; }
  }
</script>
<section class="home" aria-label="Shuttle Bay">
  <header class="home-heading"><div><p class="eyebrow">YOUR NEXT SESSION</p><h1>Shuttle Bay</h1><p class="subtitle">Choose where you want to play. Review the intended action before you confirm.</p></div>
    <Button busy={refreshing || $controller.busy} onclick={refresh}>Refresh observations</Button></header>
  {#if $facade.observations.confidence !== 'authoritative'}
    <Notice title={$facade.observations.confidence === 'partial' ? 'Some observations are unavailable' : $facade.observations.confidence === 'stale' ? 'Observations need refresh' : 'Waiting for observations'} tone="warning">
      <p>{$facade.observations.confidence === 'partial' ? 'The lists are incomplete. Known facts and unavailable observations are shown separately.' : 'Unknown observations do not mean that a game is stopped or that a target is ready.'}</p>
    </Notice>
  {/if}
  <div class="target-grid">
    <div class="choose-target"><h2>Choose a target</h2><p class="helper">Apply both choices explicitly. A profile preference does not select an installation for you.</p>
      <Select id="home-installation" label="Installation" bind:value={installationChoice} disabled={blocked}
        options={[{ value: '', label: 'Choose an installation' }, ...installationRows.flatMap(row => row.binding.kind === 'registered' ? [{ value: row.binding.registrationId, label: row.name }] : [])]}/>
      <p class="inventory">{inventoryText($facade.observations.snapshot?.installations, 'Installations')}</p>
      <Select id="home-profile" label="Profile" bind:value={profileChoice} disabled={blocked}
        options={[{ value: '', label: 'Choose ordinary or isolated' }, ...profileRows.map((row, index) => ({ value: profileKey(row), label: profileLabel(row, index) }))]}/>
      <p class="inventory">{inventoryText($facade.observations.snapshot?.profiles, 'Profiles')}</p>
      <div use:registerFocus={'home-target'}><Button variant="primary" disabled={!candidate || blocked || canonicalData(candidate) === canonicalData($facade.work.selector ?? null)} onclick={() => {
        if (candidate) facade.requestTarget(candidate, 'home-target');
      }}>Use target</Button></div>
      {#if $facade.work.pendingNavigation}<p class="pending">The requested transition waits for Save, Discard or Stay.</p>{/if}
    </div>
    <div class="selected-target"><TargetSummary installation={labels.installation} profile={labels.profile}
      status={$controller.busy ? 'Checking target' : $facade.work.binding ? 'Target observed' : 'Target not confirmed'}>
      <div class="launch-action"><h3>{$facade.work.selector?.profile.kind === 'isolated' ? 'Isolated launch' : 'Ordinary launch'}</h3>
        <p id="home-launch-reason">{availabilityText($controller.launch?.availability)}</p>
        {#if $facade.work.selector?.profile.kind === 'isolated'}<Select id="home-store-mode" label="Isolated data mode" bind:value={storeMode}
          disabled={blocked} options={[{value:'',label:'Choose this launch mode'},{value:'new',label:'New isolated data'},{value:'resume',label:'Resume setup'},{value:'existing',label:'Use existing isolated data'}]}/>{/if}
        <details><summary>Runtime consent for this attempt</summary><Select id="home-runtime-choice" label="Unrecognized runtime" bind:value={runtimeChoice} disabled={blocked}
          options={[{value:'reject',label:'Require a recognized runtime'},{value:'allow_once',label:'Allow once, if the reviewed route permits it'}]}/></details>
        <div use:registerFocus={'home-launch'}><Button variant="primary" disabled={!launch || reviewBlocked} describedBy="home-launch-reason" onclick={async () => { await review(launch, 'home-launch'); }}>Review launch</Button></div>
      </div>
    </TargetSummary></div>
  </div>
  {#if $controller.notice}<Notice title={$controller.notice} tone="warning"><p>The requested target and captured operation remain unchanged.</p></Notice>{/if}
  <section class="sessions" aria-labelledby="home-sessions-title"><div class="section-heading"><h2 id="home-sessions-title">Observed sessions</h2><p class="helper">Session identity and isolation readiness are separate observations.</p></div>
    <p class="inventory">{inventoryText($facade.observations.snapshot?.sessions, 'Sessions')}</p>
    {#each sessionRows as session, index (session.binding.sessionId)}
      {@const sessionLabels = targetLabels(session.target.status === 'observed' ? session.target.value : undefined, undefined, $facade.observations)}
      <article class="session" aria-label={`Session ${index + 1}, PID ${session.binding.process.pid}`}>
        <div><h3>Session {index + 1} · PID {session.binding.process.pid}</h3><p>{sessionLabels.installation} · {sessionLabels.profile}</p><p class="helper">{sessionStatus(session)}</p></div>
        <div class="session-actions"><p id={`home-session-${index}-reason`}>{availabilityText($controller.focus[session.binding.sessionId]?.availability)}</p>
          <Button disabled={blocked} onclick={async () => { await controller.inspectSession(session.binding); }}>Check focus for session {index + 1}</Button>
          <div use:registerFocus={`home-session-${session.binding.sessionId}`}><Button disabled={reviewBlocked || !$controller.focus[session.binding.sessionId] || !controller.focusIntent(session.binding)} describedBy={`home-session-${index}-reason`}
            onclick={async () => { await review(controller.focusIntent(session.binding), `home-session-${session.binding.sessionId}`); }}>Review focus for session {index + 1}</Button></div>
        </div>
      </article>
    {/each}
  </section>
  <section class="maintenance" aria-labelledby="home-maintenance-title"><h2 id="home-maintenance-title">Manage deliberately</h2><p class="helper">Game, Community Mod and Bridge updates have separate reviews and recovery.</p>
    <div class="maintenance-links"><Button onclick={() => onmaintenance ? onmaintenance('game') : facade.navigate('engineering')}>Game and recovery</Button><Button onclick={() => onmaintenance ? onmaintenance('runtime') : facade.navigate('engineering')}>Community Mod</Button><Button onclick={() => onmaintenance ? onmaintenance('bridge') : facade.navigate('preferences')}>Bridge and preferences</Button></div>
  </section>
</section>
<style>
  .home { display: grid; gap: 1.75rem; min-inline-size: 0; max-inline-size: 74rem; margin-inline: auto; }
  .home-heading, .section-heading { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 1rem; }
  .eyebrow { color: var(--bridge-accent-text); font-size: .8125rem; font-weight: 700; letter-spacing: .08em; }
  h1 { margin: .3rem 0 .6rem; font-size: clamp(1.8rem, 4vw, 2.5rem); line-height: 1.2; }
  h2 { margin: 0; font-size: 1.2rem; } h3 { margin: 0; font-size: 1rem; }
  p { margin: 0; overflow-wrap: anywhere; } .subtitle, .helper, .inventory { color: var(--bridge-muted); }
  .target-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 24rem), 1fr)); gap: 1.75rem; }
  .choose-target, .launch-action, .sessions { display: grid; align-content: start; gap: .9rem; min-inline-size: 0; }
  .selected-target { min-inline-size: 0; } .inventory { font-size: .875rem; } .pending { color: var(--bridge-warning); }
  .session { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, .9fr); gap: 1rem; padding-block: 1.1rem; border-block-start: 1px solid var(--bridge-border); }
  .session > div, .session-actions { display: grid; align-content: start; gap: .55rem; min-inline-size: 0; }
  .session-actions p { font-size: .875rem; } .maintenance { display: grid; gap: .75rem; padding-block-start: 1.25rem; border-block-start: 1px solid var(--bridge-border); }
  .maintenance-links { display: flex; flex-wrap: wrap; gap: .75rem; } details { min-inline-size: 0; } summary { cursor: pointer; color: var(--bridge-muted); padding-block: .5rem; overflow-wrap: anywhere; }
  @media (max-width: 48rem) { .target-grid, .session { grid-template-columns: minmax(0, 1fr); } }
  @media (forced-colors: active) { .pending { color: CanvasText; } }
</style>
