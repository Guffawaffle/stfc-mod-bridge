<script lang="ts">
import { untrack } from 'svelte';
import { Button, Notice } from '../../components';
import type { BackupReceiptRef } from '../../generated/protocol';
import type { DeepReadonly } from '../../client';
import type { ManagementController } from './controller';
import { available } from './presentation';
import Operations from './Operations.svelte';
export interface Props {
    controller: ManagementController;
}
let { controller }: Props = $props();
const facade = untrack(() => controller.facade);
const document = $derived($facade.work.draft?.draft.document), scope = $derived(document ? { kind: 'document' as const, document } : undefined);
async function history() { if (document && scope) {
    await controller.history(document);
    await controller.inspectActions(scope, ['restore_configuration']);
} }
async function restore(backup: DeepReadonly<BackupReceiptRef>) { if (!document || !scope)
    return; const intent = controller.restoreIntent(document, backup); if (intent)
    await controller.review(intent, scope, `management-backup-review-${backup.backupId}`); }
</script>
<section aria-label="Configuration backups"><h2>Configuration backups</h2>
 <p>These backups belong to the settings file for the selected installation and profile.</p>
 {#if document}<p>Document {document.documentId} · provider {document.schema.providerId}</p>
  <Button busy={$controller.busy} onclick={history}>Read this document's backup history</Button>
  {#if $controller.backups}
   {#if !$controller.historyComplete}<Notice title="Partial backup history"><p>Some entries are unavailable. This list does not establish complete history.</p></Notice>{/if}
   {#if $controller.backups.length===0}<p>No retained backups were reported for this document.</p>{/if}
   {#each $controller.backups as backup(backup.backupId)}<article><h3>Backup {backup.createdAt}</h3><p>Backup ID {backup.backupId}</p>
    <p>Review the saved backup before replacing current settings. The current file will be backed up for recovery.</p>
    <Button id={`management-backup-review-${backup.backupId}`} disabled={!scope||!available(controller.projection(scope,'restore_configuration',$controller))||$facade.work.dirty} onclick={()=>restore(backup)}>Review backup restore</Button>
   </article>{/each}
  {/if}
  {#if $facade.work.dirty}<p>Resolve the shared draft before reviewing a backup restore.</p>{/if}
 {:else}<Notice title="Settings file unavailable"><p>Open Settings for the selected installation and profile before reading or restoring its backups.</p></Notice>{/if}
</section>
<Operations {facade} onrefresh={operation=>controller.refreshOperation(operation)} onrecover={operation=>controller.recoverOperation(operation)}/>
<style>section,article{display:grid;gap:.85rem;min-inline-size:0}article{padding:1rem;border:1px solid var(--bridge-border);border-radius:var(--bridge-radius);background:var(--bridge-surface)}h2,h3,p{margin:0;overflow-wrap:anywhere}</style>
