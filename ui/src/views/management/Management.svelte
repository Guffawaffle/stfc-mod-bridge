<script lang="ts">
import { onDestroy, untrack } from 'svelte';
import { useBridge } from '../../app';
import { Button, Notice } from '../../components';
import type { BridgeFacade } from '../../state';
import { ManagementController, type ManagementInputs } from './controller';
import {sections,type ManagementSection } from './presentation';
import Profiles from './Profiles.svelte';
import Installations from './Installations.svelte';
import Updates from './Updates.svelte';
import History from './History.svelte';
import Runtime from './Runtime.svelte';
export interface Props {
    facade?: BridgeFacade;
    section?: ManagementSection;
    inputs?: ManagementInputs;
    registrationPlatform?: 'windows' | 'macos';
}
let { facade = useBridge(), section = 'profiles', inputs = {}, registrationPlatform }: Props = $props();
const controller = new ManagementController(untrack(() => facade), untrack(() => inputs));
onDestroy(() => controller.dispose());
const heading=$derived(sections.find(item=>item.id===section)?.label??'Management');
</script>
<section class="management" aria-label="Management workspace"><header><h1>{heading}</h1><Button onclick={async()=>{await facade.refresh();}} busy={$controller.busy}>Refresh details</Button></header>
 {#if $facade.observations.confidence!=='authoritative'}<Notice title="Refresh needed" tone="warning"><p>Some details are out of date. Refresh before reviewing changes to a selected item.</p></Notice>{/if}
 {#if $controller.notice}<Notice title="Management status"><p>{$controller.notice}</p></Notice>{/if}
 {#if section==='profiles'}<Profiles {controller}/>{:else if section==='installations'}<Installations {controller} {registrationPlatform}/>{:else if section==='history'}<History {controller}/>{:else if section==='runtime'}<Runtime {controller}/>{:else}<Updates {controller} {section}/>{/if}
</section>
<style>.management{display:grid;gap:1.5rem;max-inline-size:72rem;margin-inline:auto;padding:1.5rem;min-inline-size:0}header{display:flex;align-items:center;justify-content:space-between;gap:1rem;flex-wrap:wrap}h1{margin:0}@media(max-width:36rem){.management{padding:1rem}}</style>
