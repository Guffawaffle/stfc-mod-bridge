<script lang="ts">
import { untrack } from 'svelte';
import { Button, Notice, Select } from '../../components';
import type { ConfigurationParticipant, MutationIntent } from '../../generated/protocol';
import type { DeepReadonly } from '../../client';
import type { ManagementController } from './controller';
import { available, availabilityReason } from './presentation';
export interface Props {
    controller: ManagementController;
}
let { controller }: Props = $props();
const facade = untrack(() => controller.facade);
let configurationChoice = $state('unchanged'), sourceConfirmed = $state(false), adoptionConfirmed = $state(false);
const target = $derived($facade.work.binding), ownership = $derived($facade.observations.confidence === 'authoritative' && target ? controller.runtime : undefined);
const scope = $derived(target ? { kind: 'target' as const, target } : undefined);
const preference = $derived($facade.observations.snapshot?.preferences);
const action = $derived(ownership?.kind === 'absent' ? 'runtime_install' : 'runtime_update');
const checked = $derived($controller.runtimeRelease);
const provider = $derived(preference?.status === 'observed' ? preference.value.values.provider : undefined);
const document = $derived($facade.work.draft?.draft.document);
const sourceChanged = $derived(ownership?.kind === 'managed' && checked?.status === 'observed' && (checked.value.providerId !== ownership.reference.binding.providerId || checked.value.distributionId !== ownership.reference.binding.distributionId));
$effect(() => { target; checked; configurationChoice = 'unchanged'; sourceConfirmed = false; adoptionConfirmed = false; });
function participant(): DeepReadonly<ConfigurationParticipant> | undefined {
    if (!document)
        return;
    if (configurationChoice === 'migration' && checked?.status === 'observed')
        return { kind: 'compatible_migration', document, destination: checked.value.configurationSchema };
    return { kind: 'unchanged', document };
}
async function check() {
    if (provider)
        await controller.checkRuntime(provider.providerId, provider.channelId);
    if (scope)
        await controller.inspectActions(scope, ['runtime_install', 'runtime_update', 'runtime_repair', 'runtime_remove', 'runtime_stop_managing', 'runtime_adopt', 'runtime_switch_source']);
}
async function review(kind: 'runtime_install' | 'runtime_update' | 'runtime_repair' | 'runtime_remove' | 'runtime_stop_managing' | 'runtime_adopt' | 'runtime_switch_source') {
    if (!scope)
        return;
    let intent: DeepReadonly<MutationIntent> | undefined;
    if (kind === 'runtime_remove' || kind === 'runtime_stop_managing')
        intent = controller.managedRuntimeIntent(kind);
    else if (kind === 'runtime_adopt' && adoptionConfirmed)
        intent = controller.adoptionIntent();
    else {
        const configuration = participant();
        if (configuration)
            intent = kind === 'runtime_switch_source' ? sourceConfirmed ? controller.switchRuntimeIntent(configuration) : undefined : controller.runtimeIntent(kind === 'runtime_install' || kind === 'runtime_repair' ? kind : 'runtime_update', configuration);
    }
    if (intent)
        await controller.review(intent, scope, 'management-update-review');
}
</script>
<section aria-label="Community Mod management"><h2>Community Mod</h2>
 <p>Community Mod packages include their matching settings format.</p>
 {#if !ownership}<Notice title="Installed mod unavailable"><p>Bridge has not confirmed the installed mod for this installation and profile. Mod changes are currently unavailable.</p></Notice>
 {:else}<p>Installed mod: {ownership.kind}. {ownership.kind==='managed'?`Source: ${ownership.reference.binding.providerId}`:''}</p>{/if}
 {#if provider}<p>Selected source: {provider.providerId} · channel {provider.channelId}</p>{:else}<Notice title="Release source unavailable"><p>Release checks need a confirmed source and channel.</p></Notice>{/if}
 <p>Other sources and channels are currently unavailable.</p>
 <Button busy={$controller.busy} disabled={!target} onclick={check}>Check runtime release and actions</Button>
 {#if checked?.status==='observed'}
  <Notice title="Release ready for review"><p>Release {checked.value.releaseVersion}. Review the selected package and settings before confirming.</p></Notice>
  <Select id="management-runtime-configuration" label="Settings for this change" bind:value={configurationChoice} options={[{value:'unchanged',label:'Keep current settings'},{value:'migration',label:'Review a compatible settings migration'}]}/>
  {#if !document}<p>Open Settings for this installation and profile before changing its mod package.</p>{/if}
 {:else if checked}<p>{checked.status==='missing'?'The backend reports no selected release.':checked.status==='unknown'?'Release state is unknown.':'Release checks are unavailable.'}</p>{/if}
 {#if scope}<p>{availabilityReason(controller.projection(scope,action,$controller))}</p>
  <Button id="management-update-review" disabled={checked?.status!=='observed'||sourceChanged||!available(controller.projection(scope,action,$controller))||$facade.work.dirty||!document||!ownership||ownership.kind==='unmanaged'} onclick={()=>review(action)}>Review runtime {ownership?.kind==='absent'?'install':'update'}</Button>
  {#if ownership?.kind==='managed'}
   <Button disabled={checked?.status!=='observed'||sourceChanged||!document||$facade.work.dirty||!available(controller.projection(scope,'runtime_repair',$controller))} onclick={()=>review('runtime_repair')}>Review runtime repair</Button>
   <Button variant="danger" disabled={$facade.work.dirty||!available(controller.projection(scope,'runtime_remove',$controller))} onclick={()=>review('runtime_remove')}>Review managed runtime removal</Button>
   <Button disabled={$facade.work.dirty||!available(controller.projection(scope,'runtime_stop_managing',$controller))} onclick={()=>review('runtime_stop_managing')}>Review stop managing</Button>
   <p>Removing runtime uses its captured ownership. Stop managing leaves the reviewed runtime bytes installed.</p>
   {#if sourceChanged}
    <label><input type="checkbox" bind:checked={sourceConfirmed}/> Change runtime and configuration source together.</label>
    <Button disabled={!sourceConfirmed||!document||$facade.work.dirty||!available(controller.projection(scope,'runtime_switch_source',$controller))} onclick={()=>review('runtime_switch_source')}>Review explicit source switch</Button>
   {/if}
  {:else if ownership?.kind==='unmanaged'}
   <Notice title="Unmanaged runtime"><p>Bridge needs to recognize the exact installed package before adopting it.</p></Notice>
   <label><input type="checkbox" bind:checked={adoptionConfirmed}/> Adopt only the exact recognized artifact shown in review.</label>
   <Button disabled={!adoptionConfirmed||!controller.adoptionIntent()||$facade.work.dirty||!available(controller.projection(scope,'runtime_adopt',$controller))} onclick={()=>review('runtime_adopt')}>Review recognized runtime adoption</Button>
  {/if}
 {/if}
 {#if $facade.work.dirty}<p>Resolve the shared Settings/Data Sync draft before reviewing a management mutation.</p>{/if}
</section>
<style>section{display:grid;gap:1rem;min-inline-size:0}h2,p{margin:0;overflow-wrap:anywhere}label{display:flex;align-items:start;gap:.7rem}input{inline-size:1.25rem;block-size:1.25rem}</style>
