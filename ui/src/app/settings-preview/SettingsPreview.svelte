<script lang="ts">
  import { onMount } from 'svelte';
  import App from '../../App.svelte';
  import { Button, Select } from '../../components';
  import { canonicalData } from '../../client';
  import { createSettingsSession, settingsModes, type SettingsDelivery, type SettingsMode, type SettingsSession } from '../../mocks/settings-session';
  import type { ScreenState } from '../../state';
  import '../../styles/tokens.css';
  let mode=$state<SettingsMode>('save'),session=$state<SettingsSession>(),screen=$state<ScreenState>(),delivery=$state<SettingsDelivery>(),error=$state('');
  const records=$derived.by(()=>{if(!delivery)return[];return session?.records??[];});
  let generation=0,destroyed=false,stops:(()=>void)[]=[];
  function cleanup(){stops.forEach(stop=>stop());stops=[];session?.dispose();session=undefined;screen=undefined;delivery=undefined;}
  async function reset(){const active=++generation;cleanup();error='';try{const created=await createSettingsSession(mode);if(destroyed||active!==generation){created.dispose();return;}session=created;
    stops.push(created.facade.subscribe(value=>{if(active===generation)screen=value;}),created.subscribe(value=>{if(active===generation)delivery=value;}));}catch{if(active===generation)error='Settings fixture composition failed.';}}
  onMount(()=>{void reset();const timer=setInterval(()=>{const current=session;if(current&&!current.clock.disposed)current.clock.advanceBy(20);},20);return()=>{destroyed=true;generation++;clearInterval(timer);cleanup();};});
  function registerFocus(node:HTMLElement){const stop=session!.facade.focus.register('preview-target',()=>node.querySelector('button'));return{destroy:stop};}
</script>
<section class="bridge-theme preview" aria-label="Development Settings preview"><header><h1>Settings preview</h1><Button onclick={reset}>Reset Settings scenario</Button></header>
  <Select id="settings-preview-mode" label="Settings scenario" value={mode} options={settingsModes.map(value=>({value,label:value}))} onchange={value=>{mode=value as SettingsMode;void reset();}}/>
  <p>Shared Rust-derived synthetic fixtures · actual App composition · browser only · no native host or network mutation</p>
  {#if session}<div use:registerFocus><Button onclick={()=>{session?.facade.requestTarget({installation:{kind:'registered',id:'a'.repeat(32)},profile:{kind:'isolated',id:'b'.repeat(32)}},'preview-target');}}>Queue synthetic target change</Button></div>{/if}
  {#if error}<p role="alert">{error}</p>{/if}
  <dl><div><dt>Session</dt><dd data-testid="settings-preview-session">{session?.mode??'loading'}</dd></div><div><dt>Save state</dt><dd data-testid="settings-preview-state">{screen?.transition.kind??'idle'}</dd></div><div><dt>Confidence</dt><dd data-testid="settings-preview-confidence">{screen?.observations.confidence??'uninitialized'}</dd></div>
    <div><dt>Draft</dt><dd data-testid="settings-preview-draft">revision: {screen?.work.draft?.draft.revision??'none'}; edits: {screen?.work.edits.length??0}; buffers: {screen?.work.publicInputs?.length??0}; dirty: {String(screen?.work.dirty??false)}</dd></div>
    <div><dt>Closed oracle</dt><dd data-testid="settings-preview-oracle">fault: {delivery?.lastFault??'none'}; pending: {delivery?.pending??0}; processing: {String(delivery?.processing??false)}</dd></div></dl>
  <details><summary>Development capture evidence</summary><pre data-testid="settings-preview-edits">{canonicalData(screen?.work.edits??[])}</pre>
    <ol data-testid="settings-preview-requests">{#each records as row}<li>{row.method} · {row.delivery}</li>{/each}</ol>
    <ul data-testid="settings-preview-provenance">{#each session?.provenance??[] as source}<li>{source.id}: {source.sha256}</li>{/each}</ul></details>
</section>
{#if session}{#key session}<App facade={session.facade}/>{/key}{/if}
<style>.preview{display:grid;gap:.7rem;padding:1rem 2rem;border-bottom:2px solid var(--bridge-border)}header{display:flex;gap:1rem;flex-wrap:wrap;justify-content:space-between}h1{margin:0;font-size:1.2rem}p{margin:0;color:var(--bridge-muted)}dl{display:flex;gap:1rem;flex-wrap:wrap;margin:0}dd{margin:0}pre,li,dd{overflow-wrap:anywhere;white-space:pre-wrap}details{min-inline-size:0}</style>
