<script lang="ts">
import { untrack } from 'svelte';
import { Button, Field, Select } from '../../components';
import type { ManagementController } from './controller';
import { observedInstallations, inventoryStatus, available } from './presentation';
import {bindingEquivalent} from '../../client/relations';
export interface Props {
    controller: ManagementController;
    registrationPlatform?: 'windows' | 'macos';
}
let { controller, registrationPlatform }: Props = $props();
const facade = untrack(() => controller.facade);
let choice = $state(''), name = $state(''), newName = $state(''), directory = $state('');
const rows = $derived(observedInstallations($facade.observations)), selected = $derived(rows.find(row => row.binding.kind === 'registered' && row.binding.registrationId === choice));
const target = $derived($facade.work.binding), scope = $derived(target ? { kind: 'target' as const, target } : undefined);
const catalogRevision = $derived($facade.observations.confidence === 'authoritative' ? controller.catalogRevision : undefined);
$effect(() => { selected; name = selected?.name ?? ''; });
async function rename() { if (!selected || !scope || !name.trim())
    return; await controller.review({ kind: 'edit_installation', input: { installation: selected.binding, name: name.trim() } }, scope, 'management-rename-installation'); }
async function register() { if (!catalogRevision || !registrationPlatform || !directory.trim() || !newName.trim())
    return; const scope = { kind: 'catalog' as const, revision: catalogRevision }; await controller.inspectActions(scope, ['register_installation']); const intent = controller.registerIntent({ name: newName.trim(), directory: { platform: registrationPlatform, value: directory } }); if (intent)
    await controller.review(intent, scope, 'management-register-installation'); }
</script>
<section aria-label="Installation management"><h2>Installations</h2><p>{inventoryStatus($facade.observations.snapshot?.installations)}</p>
 <Select id="management-installation" label="Installation to manage" bind:value={choice} options={[{value:'',label:'Choose an installation'},...rows.flatMap(row=>row.binding.kind==='registered'?[{value:row.binding.registrationId,label:`${row.name} · ${row.binding.registrationId}`}]:[])]}/>
 {#if selected}<article><h3>{selected.name}</h3><Field id="management-installation-name" label="Installation name" bind:value={name}/>
  {#if scope&&target&&selected.binding.kind==='registered'&&target.installation.kind==='registered'&&bindingEquivalent(selected.binding,target.installation)}<Button onclick={()=>controller.inspectActions(scope!,['edit_installation'])}>Check rename availability</Button><Button id="management-rename-installation" disabled={!available(controller.projection(scope,'edit_installation',$controller))||$facade.work.dirty||!name.trim()} onclick={rename}>Review installation rename</Button>
  {:else}<p>Apply this installation in the shared target selection before reviewing its rename.</p>{/if}
 </article>{/if}
 <article><h3>Register an installation</h3><Field id="management-register-name" label="Installation display name" bind:value={newName}/>
  <Field id="management-register-directory" label="Explicit installation directory" bind:value={directory} autocomplete="off" description="Private path input is used only for this explicit registration review."/>
  <Button id="management-register-installation" disabled={!catalogRevision||!registrationPlatform||!newName.trim()||!directory.trim()||$facade.work.dirty} onclick={register}>Review registration</Button>
  {#if !catalogRevision||!registrationPlatform}<p>Registration is unavailable until the backend supplies the canonical catalog baseline and platform route for this connection.</p>{/if}
 </article>
</section>
<style>section,article{display:grid;gap:.85rem;min-inline-size:0}article{padding:1rem;border:1px solid var(--bridge-border);border-radius:var(--bridge-radius);background:var(--bridge-surface)}h2,h3,p{margin:0;overflow-wrap:anywhere}</style>
