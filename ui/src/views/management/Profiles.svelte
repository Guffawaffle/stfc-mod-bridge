<script lang="ts">
import { Button, Field, Select, Notice } from '../../components';
import { untrack } from 'svelte';
import { type DeepReadonly } from '../../client';
import type { PreferredInstallationEdit, ProfileProjection } from '../../generated/protocol';
import type { ManagementController } from './controller';
import { observedProfiles, observedInstallations, profileIdentity, profileLabel, availabilityReason, available, inventoryStatus } from './presentation';
import { installationLabel } from '../home/presentation';
export interface Props {
    controller: ManagementController;
}
let { controller }: Props = $props();
const facade = untrack(() => controller.facade);
let choice = $state(''), name = $state(''), preference = $state('keep'), confirmedDelete = $state(false), newName = $state(''), newInstallation = $state(''), setup = $state('new'), importConfirmed = $state(false);
const rows = $derived(observedProfiles($facade.observations)), installations = $derived(observedInstallations($facade.observations));
const selected = $derived(rows.find(row => profileIdentity(row) === choice));
const catalogRevision = $derived($facade.observations.confidence === 'authoritative' ? controller.catalogRevision : undefined);
const scope = $derived(selected ? controller.profileScope(selected) ?? (catalogRevision ? { kind: 'catalog' as const, revision: catalogRevision } : undefined) : undefined);
const source = $derived($controller.imports?.find(row => row.reference.sourceId === setup));
$effect(() => { setup; source; importConfirmed = false; });
$effect(() => { selected; name = selected?.kind === 'isolated' ? selected.name : ''; preference = 'keep'; confirmedDelete = false; });
function preferenceEdit(row: DeepReadonly<ProfileProjection>): DeepReadonly<PreferredInstallationEdit> | undefined {
    if (preference === 'keep')
        return { kind: 'keep', expected: row.preferredInstallation ? { kind: 'registered', id: row.preferredInstallation } : { kind: 'none' } };
    if (preference === 'clear')
        return { kind: 'clear' };
    const installation = installations.find(item => item.binding.kind === 'registered' && item.binding.registrationId === preference)?.binding;
    return installation?.kind === 'registered' ? { kind: 'set', installation } : undefined;
}
async function inspect() { if (scope)
    await controller.inspectActions(scope, selected?.kind === 'ordinary' ? ['edit_ordinary_profile'] : ['edit_isolated_profile', 'archive_profile', 'restore_profile', 'delete_profile']); }
async function edit(action: 'edit' | 'archive' | 'restore' | 'delete') {
    if (!selected || !scope || action === 'delete' && !confirmedDelete)
        return;
    const pref = preferenceEdit(selected);
    if (!pref)
        return;
    const intent = controller.profileIntent(selected, action, name, pref);
    if (intent)
        await controller.review(intent, scope, action === 'edit' ? 'management-profile-review' : `management-profile-${action}-review`);
}
async function create() {
    const installation = installations.find(item => item.binding.kind === 'registered' && item.binding.registrationId === newInstallation)?.binding;
    if (installation?.kind !== 'registered' || !newName.trim() || !catalogRevision || setup !== 'new' && (!source || source.accessibility.status !== 'observed' || !source.accessibility.value || !importConfirmed))
        return;
    const scope = { kind: 'catalog' as const, revision: catalogRevision };
    await controller.inspectActions(scope, ['create_profile']);
    const intent = controller.createIntent({ name: newName.trim(), preferredInstallation: { kind: 'registered', id: installation.registrationId, revisionAssertion: installation.registrationRevision }, setup: setup === 'new' ? { kind: 'new' } : source ? { kind: 'windows_user_import', source: source.reference, approval: 'request_native_approval' } : { kind: 'new' } });
    if (intent)
        await controller.review(intent, scope, 'management-create-profile');
}
</script>
<section aria-label="Profile management"><h2>Profiles</h2><p>{inventoryStatus($facade.observations.snapshot?.profiles)}</p>
 <p class="help">Preferences apply to a future launch. Existing sessions keep their captured installation.</p>
 <Select id="management-profile" label="Profile to manage" bind:value={choice} options={[{value:'',label:'Choose a profile'},...rows.map(row=>({value:profileIdentity(row),label:`${profileLabel(row)} · ${profileIdentity(row)}`}))]} />
 {#if selected}<article><h3>{profileLabel(selected)}</h3><p class="identity">Profile ID {profileIdentity(selected)}</p>
  {#if selected.kind==='isolated'}<p>Status: {selected.reference.state}</p><Field id="management-profile-name" label="Profile name" bind:value={name} description="Rename this exact observed profile." />{/if}
  <Select id="management-profile-preference" label="Next-launch installation preference" bind:value={preference} options={[{value:'keep',label:'Keep observed preference'},{value:'clear',label:'Clear preference'},...installations.flatMap(row=>row.binding.kind==='registered'?[{value:row.binding.registrationId,label:`${row.name} · ${row.binding.registrationId}`}]:[])]} />
  <Button onclick={inspect}>Check profile actions</Button>
  {#if scope}<p>{availabilityReason(controller.projection(scope,selected.kind==='ordinary'?'edit_ordinary_profile':'edit_isolated_profile',$controller))}</p>
   <Button id="management-profile-review" disabled={$facade.work.dirty||$facade.work.transitionBusy||!available(controller.projection(scope,selected.kind==='ordinary'?'edit_ordinary_profile':'edit_isolated_profile',$controller))} onclick={()=>edit('edit')}>Review profile changes</Button>
   {#if selected.kind==='isolated'}
    <Button id={`management-profile-${selected.reference.state==='active'?'archive':'restore'}-review`} disabled={$facade.work.dirty||!available(controller.projection(scope,selected.reference.state==='active'?'archive_profile':'restore_profile',$controller))} onclick={()=>edit(selected?.kind==='isolated'&&selected.reference.state==='active'?'archive':'restore')}>{selected.reference.state==='active'?'Review archive':'Review restore'}</Button>
    <label class="confirmation"><input type="checkbox" bind:checked={confirmedDelete} /> I want to delete this entire owned profile and its data.</label>
    <Button id="management-profile-delete-review" variant="danger" disabled={!confirmedDelete||$facade.work.dirty||!available(controller.projection(scope,'delete_profile',$controller))} onclick={()=>edit('delete')}>Review permanent profile deletion</Button>
   {/if}
  {:else}<Notice title="Profile actions unavailable"><p>The canonical catalog mutation baseline is unavailable.</p></Notice>{/if}
 </article>{/if}
 <article><h3>Create an isolated profile</h3><Field id="management-new-profile" label="New profile name" bind:value={newName} />
  <Select id="management-new-installation" label="Preferred installation for the new profile" bind:value={newInstallation} options={[{value:'',label:'Choose explicitly'},...installations.flatMap(row=>row.binding.kind==='registered'?[{value:row.binding.registrationId,label:installationLabel(row)}]:[])]} />
  <Select id="management-profile-setup" label="Initial isolated store" bind:value={setup} options={[{value:'new',label:'Create a new store'},...($controller.imports??[]).map(row=>({value:row.reference.sourceId,label:`${row.name} · ${row.reference.sourceId}`}))]}/>
  {#if setup!=='new'&&source}<p>Captured source {source.reference.sourceId} · revision {source.reference.sourceRevision}. Accessibility: {source.accessibility.status}.</p>
   <label class="confirmation"><input type="checkbox" bind:checked={importConfirmed}/> Request native approval to import this exact user store.</label>{/if}
  <Button id="management-create-profile" disabled={!catalogRevision||!newName.trim()||!newInstallation||$facade.work.dirty||setup!=='new'&&(!importConfirmed||source?.accessibility.status!=='observed'||!source.accessibility.value)} onclick={create}>Review profile creation</Button>
  {#if !catalogRevision}<p class="help">Creation waits for the canonical catalog baseline for this connection.</p>{/if}
  <p class="help">Native import discovery requires explicit approval for an observed ordinary owner. Bridge never requests game passwords.</p>
  {#each rows.filter(row=>row.kind==='ordinary') as ordinary}
   {#if ordinary.kind==='ordinary'}<Button disabled={$controller.busy} onclick={()=>controller.discoverImports(ordinary.reference.ownerScope)}>Request native import discovery</Button>{/if}
  {/each}
  {#if $controller.imports}<p>{$controller.imports.length} native import sources observed. Import creation requires review of an exact source and native approval choice.</p>{/if}
 </article>
</section>
<style>section{display:grid;gap:1rem;min-inline-size:0}article{display:grid;gap:.85rem;padding:1.1rem;background:var(--bridge-surface);border:1px solid var(--bridge-border);border-radius:var(--bridge-radius)}h2,h3,p{margin:0;overflow-wrap:anywhere}.help{color:var(--bridge-muted)}.identity{font-size:.9rem}.confirmation{display:flex;align-items:start;gap:.7rem}.confirmation input{inline-size:1.25rem;block-size:1.25rem}</style>
