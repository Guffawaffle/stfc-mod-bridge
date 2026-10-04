<script lang="ts">
  import { untrack } from 'svelte';
  import type { AnnouncementController } from '../state';
  let { controller }: { controller: AnnouncementController } = $props();
  let current = $state(untrack(() => controller.state));
  $effect(() => controller.subscribe(value => { current = value; }));
</script>
<div class="bridge-sr-only" role="status" aria-live="polite" aria-atomic="true">{#key current.polite.id}<span>{current.polite.message}</span>{/key}</div>
<div class="bridge-sr-only" role="alert" aria-live="assertive" aria-atomic="true">{#key current.assertive.id}<span>{current.assertive.message}</span>{/key}</div>
