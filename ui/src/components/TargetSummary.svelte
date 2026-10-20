<script lang="ts">
  import type { Snippet } from 'svelte';

  export interface Props {
    title?: string;
    installation: string;
    profile: string;
    session?: string;
    status?: string;
    children?: Snippet;
  }

  let { title = 'Selected target', installation, profile, session, status, children }: Props = $props();
  const id = $props.id();
</script>

<section class="bridge-target-summary" aria-labelledby={`${id}-title`}>
  <div class="heading"><h2 id={`${id}-title`}>{title}</h2>{#if status}<p class="status">{status}</p>{/if}</div>
  <dl>
    <div><dt>Installation</dt><dd>{installation}</dd></div>
    <div><dt>Profile</dt><dd>{profile}</dd></div>
    {#if session}<div><dt>Session</dt><dd>{session}</dd></div>{/if}
  </dl>
  {#if children}<div class="details">{@render children()}</div>{/if}
</section>

<style>
  .bridge-target-summary { border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius);
    background: var(--bridge-surface); padding: 1.15rem; min-inline-size: 0; overflow-wrap: anywhere;
    container: bridge-target / inline-size; }
  .heading { display: flex; justify-content: space-between; align-items: baseline; flex-wrap: wrap; gap: .5rem 1rem; }
  h2 { margin: 0; font-size: 1.125rem; line-height: 1.5; }
  .status { margin: 0; color: var(--bridge-muted); font-size: .9375rem; }
  dl { display: grid; gap: .85rem; margin: 1rem 0 0; }
  dl > div { display: grid; grid-template-columns: minmax(0, 1fr); gap: .25rem .75rem; }
  dt { color: var(--bridge-muted); }
  dd { margin: 0; }
  .details { margin-block-start: 1rem; }
  @container bridge-target (min-width: 24rem) { dl > div { grid-template-columns: minmax(0, 8rem) minmax(0, 1fr); } }
</style>
