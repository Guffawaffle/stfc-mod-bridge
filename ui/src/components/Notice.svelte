<script lang="ts">
  import type { Snippet } from 'svelte';

  export interface Props {
    title: string;
    tone?: 'info' | 'success' | 'warning' | 'danger';
    live?: 'polite' | 'assertive' | 'off';
    children?: Snippet;
    actions?: Snippet;
  }

  let { title, tone = 'info', live = 'off', children, actions }: Props = $props();
  const id = $props.id();
</script>

<section class="bridge-notice" data-tone={tone}
  role={live === 'off' ? 'region' : live === 'assertive' ? 'alert' : 'status'}
  aria-live={live === 'off' ? undefined : live} aria-atomic={live === 'off' ? undefined : 'true'}
  aria-labelledby={`${id}-title`}>
  <h3 id={`${id}-title`}>{title}</h3>
  {#if children}<div class="content">{@render children()}</div>{/if}
  {#if actions}<div class="actions">{@render actions()}</div>{/if}
</section>

<style>
  .bridge-notice { border: 1px solid var(--bridge-border); border-inline-start: .25rem solid var(--bridge-accent);
    border-radius: var(--bridge-radius); padding: 1rem; background: var(--bridge-accent-soft);
    color: var(--bridge-text); overflow-wrap: anywhere; min-inline-size: 0; }
  h3 { margin: 0; font-size: 1rem; line-height: 1.5; }
  .content { margin-block-start: .4rem; }
  .actions { display: flex; flex-wrap: wrap; gap: .6rem; margin-block-start: .85rem; }
  .bridge-notice[data-tone='success'] { border-inline-start-color: var(--bridge-success); }
  .bridge-notice[data-tone='warning'] { background: var(--bridge-warning-soft); border-inline-start-color: var(--bridge-warning); }
  .bridge-notice[data-tone='danger'] { background: var(--bridge-danger-soft); border-inline-start-color: var(--bridge-danger-text); }
</style>
