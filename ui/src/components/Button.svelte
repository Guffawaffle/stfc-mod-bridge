<script lang="ts">
  import type { Snippet } from 'svelte';

  export interface Props {
    children?: Snippet;
    variant?: 'primary' | 'secondary' | 'danger' | 'quiet';
    type?: 'button' | 'submit' | 'reset';
    disabled?: boolean;
    busy?: boolean;
    onclick?: (event: MouseEvent) => void | Promise<void>;
    id?: string;
    ariaLabel?: string;
    describedBy?: string;
  }

  let { children, variant = 'secondary', type = 'button', disabled = false,
    busy = false, onclick, id, ariaLabel, describedBy }: Props = $props();
  let pending = $state(false);
  const blocked = $derived(disabled || busy || pending);

  async function activate(event: MouseEvent): Promise<void> {
    if (blocked) { event.preventDefault(); return; }
    if (!onclick) return;
    pending = true;
    try { await onclick(event); }
    finally { pending = false; }
  }
</script>

<button {id} {type} class="bridge-button" data-variant={variant}
  disabled={blocked} aria-busy={busy || pending ? 'true' : undefined}
  aria-label={ariaLabel} aria-describedby={describedBy} onclick={activate}>
  {@render children?.()}
</button>

<style>
  .bridge-button { display: inline-flex; align-items: center; justify-content: center;
    min-block-size: 2.75rem; max-inline-size: 100%; padding: .65rem 1rem;
    border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius);
    background: var(--bridge-surface); color: var(--bridge-text); font: inherit;
    font-weight: 600; line-height: 1.35; white-space: normal; overflow-wrap: anywhere;
    text-align: center; cursor: pointer; }
  .bridge-button[data-variant='primary'] { background: var(--bridge-accent);
    color: var(--bridge-on-accent); border-color: var(--bridge-accent); }
  .bridge-button[data-variant='danger'] { background: var(--bridge-danger);
    color: var(--bridge-on-danger); border-color: var(--bridge-danger); }
  .bridge-button[data-variant='quiet'] { background: transparent; }
  .bridge-button:hover:not(:disabled) { filter: brightness(.94); }
  .bridge-button:disabled { cursor: default; background: var(--bridge-disabled-surface);
    color: var(--bridge-disabled-text); border-color: var(--bridge-border); }
  @media (forced-colors: active) {
    .bridge-button:hover:not(:disabled) { filter: none; }
    .bridge-button:disabled { border-color: GrayText; }
  }
</style>
