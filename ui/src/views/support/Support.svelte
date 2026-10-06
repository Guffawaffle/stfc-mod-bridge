<script lang="ts">
import { onDestroy, untrack } from 'svelte';
import { useBridge } from '../../app';
import { Button, Notice } from '../../components';
import type { BridgeFacade } from '../../state';
import { SupportController, type SupportOptions } from './controller';
import { diagnosticRows, disclosedPaths } from './presentation';
export interface Props {
    facade?: BridgeFacade;
    options?: SupportOptions;
}
let { facade = useBridge(), options = {} }: Props = $props();
const controller = new SupportController(untrack(() => facade), untrack(() => options));
onDestroy(() => controller.dispose());
const rows = $derived($controller.preview ? diagnosticRows($controller.preview) : []), paths = $derived(disclosedPaths($controller.preview));
</script>
<section class="support" aria-label="Support and diagnostics"><header><h1>Support</h1><p>Preview diagnostics for the selected installation and profile before choosing an export destination.</p></header>
 <Notice title="Protected data stays excluded"><p>Account contents, passwords and protected configuration values are excluded. Private filesystem paths are included only after an explicit disclosure choice.</p></Notice>
 <label class="disclosure"><input type="checkbox" checked={$controller.includePaths} onchange={event=>controller.setDisclosure(event.currentTarget.checked)}/> Include installation and profile paths in this preview and reviewed export</label>
 <Button busy={$controller.busy} disabled={!$facade.work.selector||$facade.observations.confidence!=='authoritative'} onclick={()=>controller.preview()}>Preview diagnostics</Button>
 {#if $controller.notice}<p role="status">{$controller.notice}</p>{/if}
 {#if $controller.preview}<article aria-label="Verified export preview"><h2>Reviewed export contents</h2><p>{$controller.preview.reference.disclosure==='include_paths'?'Includes explicitly disclosed private paths.':'Private paths excluded.'}</p>
  <dl>{#each rows as row}<div><dt>{row.label}</dt><dd>{row.value}</dd></div>{/each}
   {#each paths as row}<div class="path"><dt>{row.label}</dt><dd>{row.value}</dd></div>{/each}
  </dl>
  <Button busy={$controller.busy} onclick={()=>controller.chooseDestination()}>Choose export destination</Button>
  <Button id="support-review-export" disabled={!$controller.destination||$controller.busy} onclick={()=>controller.reviewExport().then(()=>{})}>Review diagnostic export</Button>
  <p class="help">The destination stays attached to this preview. Review before exporting.</p>
 </article>{/if}
</section>
<style>.support{display:grid;gap:1.2rem;max-inline-size:70rem;padding:1.5rem;margin-inline:auto;min-inline-size:0}header,article{display:grid;gap:.85rem}article{padding:1rem;background:var(--bridge-surface);border:1px solid var(--bridge-border);border-radius:var(--bridge-radius)}h1,h2,p,dl{margin:0;overflow-wrap:anywhere}dl{display:grid;gap:.85rem}dl div{display:grid;grid-template-columns:minmax(8rem,1fr) 3fr;gap:.8rem}dt{font-weight:600}dd{margin:0;overflow-wrap:anywhere}.disclosure{display:flex;gap:.7rem;align-items:start}.disclosure input{inline-size:1.25rem;block-size:1.25rem}.help{color:var(--bridge-muted)}@media(max-width:36rem){.support{padding:1rem}dl div{grid-template-columns:1fr}}</style>
