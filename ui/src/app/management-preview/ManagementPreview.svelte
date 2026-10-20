<script lang="ts">
import { onMount } from 'svelte';
import App from '../../App.svelte';
import { Button, Select } from '../../components';
import { canonicalData } from '../../client';
import { createManagementSession, managementModes, type ManagementDelivery, type ManagementMode, type ManagementRecord, type ManagementSession } from '../../mocks/management-session';
import type { ScreenState, ActionReviewState } from '../../state';
import '../../styles/tokens.css';
let mode = $state<ManagementMode>('management'), session = $state<ManagementSession>(), screen = $state<ScreenState>(), action = $state<ActionReviewState>(), delivery = $state<ManagementDelivery>(), records = $state<readonly ManagementRecord[]>([]), provenance = $state<ManagementSession['provenance']>([]), error = $state('');
let generation = 0, destroyed = false, stops: (() => void)[] = [];
function cleanup() { stops.forEach(stop => stop()); stops = []; session?.dispose(); session = undefined; screen = undefined; action = undefined; delivery = undefined; records = []; provenance = []; }
async function reset() {
    const active = ++generation;
    cleanup();
    error = '';
    try {
        const created = await createManagementSession(mode);
        if (destroyed || active !== generation) {
            created.dispose();
            return;
        }
        session = created;
        stops.push(created.facade.subscribe(value => { if (active === generation)
            screen = value; }), created.facade.actions.subscribe(value => { if (active === generation)
            action = value; }), created.subscribe(value => { if (active === generation) {
            delivery = value;
            records = created.records;
            provenance = created.provenance;
        } }));
    }
    catch {
        if (active === generation)
            error = 'Management fixture composition failed.';
    }
}
onMount(() => { void reset(); const timer = setInterval(() => { const current = session; if (current && !current.clock.disposed)
    current.clock.advanceBy(20); }, 20); return () => { destroyed = true; generation++; clearInterval(timer); cleanup(); }; });
</script>
<section class="bridge-theme preview" aria-label="Development Management preview"><header><h1>Management preview</h1><Button onclick={reset}>Reset Management scenario</Button></header>
  <Select id="management-preview-mode" label="Management scenario" value={mode} options={managementModes.map(value=>({value,label:value}))} onchange={value=>{mode=value as ManagementMode;void reset();}}/>
  <p>Shared Rust-derived synthetic fixtures · actual App composition · explicit synthetic target and metadata · browser only · no native host or game mutation</p>
  {#if error}<p role="alert">{error}</p>{/if}
  <dl><div><dt>Scenario</dt><dd data-testid="management-preview-session">{session?.mode??'loading'}</dd></div><div><dt>Confidence</dt><dd data-testid="management-preview-confidence">{screen?.observations.confidence??'uninitialized'}</dd></div>
    <div><dt>Review state</dt><dd data-testid="management-preview-state">{action?.transition.kind??'idle'}</dd></div><div><dt>Closed oracle</dt><dd data-testid="management-preview-oracle">fault: {delivery?.lastFault??'none'}; pending: {delivery?.pending??0}; processing: {String(delivery?.processing??false)}</dd></div></dl>
  <details><summary>Development capture evidence</summary><pre data-testid="management-preview-target">{canonicalData(screen?.work.binding??null)}</pre><pre data-testid="management-preview-capture">{canonicalData(action?.plan?.semantics??null)}</pre>
    <ol data-testid="management-preview-requests">{#each records as row}<li>{row.method}</li>{/each}</ol><ul data-testid="management-preview-provenance">{#each provenance as source}<li>{source.id}: {source.sha256}</li>{/each}</ul></details>
</section>
{#if session}{#key session}<App facade={session.facade} managementInputs={session.inputs} registrationPlatform="windows"/>{/key}{/if}
<style>.preview{display:grid;gap:.7rem;padding:1rem 2rem;border-bottom:2px solid var(--bridge-border)}header{display:flex;gap:1rem;flex-wrap:wrap;justify-content:space-between}h1{margin:0;font-size:1.2rem}p{margin:0;color:var(--bridge-muted)}dl{display:flex;gap:1rem;flex-wrap:wrap;margin:0}dd{margin:0}pre,li,dd{overflow-wrap:anywhere;white-space:pre-wrap}details{min-inline-size:0}</style>
