<script lang="ts">
import { untrack } from 'svelte';
import { Button, Notice } from '../../components';
import type { ManagementController } from './controller';
import type { ManagementSection } from './presentation';
import { available, availabilityReason } from './presentation';
export interface Props {
    controller: ManagementController;
    section: Extract<ManagementSection, 'game' | 'bridge'>;
}
let { controller, section }: Props = $props();
const facade = untrack(() => controller.facade);
const target = $derived($facade.work.binding), application = $derived($facade.observations.confidence === 'authoritative' ? controller.application : undefined), scope = $derived(section === 'bridge' ? application ? { kind: 'application' as const, application } : undefined : target ? { kind: 'target' as const, target } : undefined);
const action = $derived(section === 'game' ? 'game_update' : 'bridge_update');
const checked = $derived(section === 'game' ? $controller.game : $controller.bridgeRelease);
async function check() { if (section === 'game')
    await controller.checkGame();
else
    await controller.checkBridge(); if (scope)
    await controller.inspectActions(scope, [action]); }
async function review() { if (!scope)
    return; const intent = section === 'game' ? controller.gameIntent() : controller.bridgeIntent(); if (intent)
    await controller.review(intent, scope, 'management-update-review'); }
</script>
<section aria-label={section==='game'?'Game updates':'Bridge updates'}><h2>{section==='game'?'Game client':'Bridge application'}</h2>
 <p>{section==='game'?'Updates the official game client in the selected installation.':'Updates this Bridge application and its paired support components.'}</p>
 {#if section==='bridge'&&!application}<Notice title="Current Bridge package unavailable"><p>Bridge cannot check updates until its installed package is identified.</p></Notice>{/if}
 <Button busy={$controller.busy} disabled={section==='bridge'?!application:!target} onclick={check}>Check {section==='game'?'game':'Bridge'} updates</Button>
 {#if checked?.status==='observed'}
  <Notice title="Update ready for review"><p>Review the checked update before confirming installation.</p></Notice>
 {:else if checked}<p>{checked.status==='missing'?'The backend reports no selected update.':checked.status==='unknown'?'Update state is unknown.':'Update checks are unavailable.'}</p>{/if}
 {#if scope}<p>{availabilityReason(controller.projection(scope,action,$controller))}</p><Button id="management-update-review" disabled={checked?.status!=='observed'||!available(controller.projection(scope,action,$controller))||$facade.work.dirty} onclick={review}>Review {section==='game'?'game update':'Bridge update'}</Button>{/if}
 {#if $facade.work.dirty}<p>Resolve the shared Settings/Data Sync draft before reviewing a management mutation.</p>{/if}
</section>
<style>section{display:grid;gap:1rem;min-inline-size:0}h2,p{margin:0;overflow-wrap:anywhere}</style>
