<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { bridgeClient } from './client-instance';
  import { Shell, provideBridge } from './app';
  import { BridgeFacade } from './state';
  import { Navigation } from './components';
  import { Home } from './views/home';
  import { Settings } from './views/settings';
  import { DataSync } from './views/sync';
  import { Management, sections, type ManagementInputs, type ManagementSection } from './views/management';
  import { Support } from './views/support';
  import { ActionReview, UnsavedChanges, WorkspaceNavigation } from './navigation';
  let { facade = new BridgeFacade(bridgeClient, { idempotencyKey: () => crypto.randomUUID() }), managementInputs = {}, registrationPlatform }: {
    facade?: BridgeFacade; managementInputs?: ManagementInputs; registrationPlatform?: 'windows' | 'macos';
  } = $props();
  const scopedFacade = untrack(() => facade);
  provideBridge(scopedFacade);
  let connecting = $state(false);
  let managementSection = $state<ManagementSection>('profiles');
  let active = false;
  const observation = new AbortController();
  const connection = $derived($scopedFacade.observations.confidence === 'authoritative' ? 'Observations current'
    : $scopedFacade.observations.confidence === 'partial' ? 'Some observations unavailable'
    : $scopedFacade.observations.confidence === 'stale' ? 'Observations need refresh' : 'Waiting for Bridge');
  function openMaintenance(destination: 'game' | 'runtime' | 'bridge' | 'recovery'): void {
    if (destination === 'recovery') { scopedFacade.navigate('history'); return; }
    if (destination === 'bridge') { scopedFacade.navigate('preferences'); return; }
    managementSection = destination;
    scopedFacade.navigate('engineering');
  }
  async function reconnect(): Promise<void> {
    if (!active || connecting) return;
    connecting = true;
    try { await scopedFacade.connect({ signal: observation.signal }); }
    finally { if (active) connecting = false; }
  }
  onMount(() => {
    active = true; void reconnect();
    return () => { active = false; observation.abort(); scopedFacade.dispose(); };
  });
</script>
<Shell announcements={scopedFacade.announcements}>
  {#snippet header()}<p role="status" class="connection">{connection}</p>{/snippet}
  {#snippet navigation()}<WorkspaceNavigation facade={scopedFacade}/>{/snippet}
  {#if $scopedFacade.work.view === 'home'}
    <Home facade={scopedFacade} onmaintenance={openMaintenance}/>
  {:else if $scopedFacade.work.view === 'settings'}
    <Settings facade={scopedFacade}/>
  {:else if $scopedFacade.work.view === 'data_sync'}
    <DataSync facade={scopedFacade}/>
  {:else if $scopedFacade.work.view === 'diagnostics'}
    <Support facade={scopedFacade}/>
  {:else if $scopedFacade.work.view === 'history'}
    <Management facade={scopedFacade} section="history" inputs={managementInputs} {registrationPlatform}/>
  {:else if $scopedFacade.work.view === 'preferences'}
    <Management facade={scopedFacade} section="bridge" inputs={managementInputs} {registrationPlatform}/>
  {:else}
    <section class="workspace-content" aria-label="Engineering">
      <Navigation label="Engineering sections" items={sections.filter(section => section.id !== 'history' && section.id !== 'bridge')}
        current={managementSection} onselect={id => { const section = sections.find(item => item.id === id); if (section) managementSection = section.id; }}/>
      <Management facade={scopedFacade} section={managementSection} inputs={managementInputs} {registrationPlatform}/>
    </section>
  {/if}
  <UnsavedChanges facade={scopedFacade}/>
  <ActionReview facade={scopedFacade}/>
</Shell>
<style>
  .connection { margin: 0 0 0 auto; color: var(--bridge-muted); font-size: .9375rem; }
  .workspace-content { display: grid; gap: 1.5rem; }
  p { overflow-wrap: anywhere; }
</style>
