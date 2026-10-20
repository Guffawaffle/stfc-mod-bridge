<script lang="ts">
  export interface NavigationItem { id: string; label: string; disabled?: boolean }
  export interface Props {
    label: string;
    items: readonly NavigationItem[];
    current: string;
    onselect: (id: string) => void;
  }

  let { label, items, current, onselect }: Props = $props();
</script>

<nav aria-label={label} class="bridge-navigation">
  <ul>
    {#each items as item (item.id)}
      <li><button type="button" disabled={item.disabled}
        aria-current={item.id === current ? 'page' : undefined}
        onclick={() => { if (!item.disabled) onselect(item.id); }}>{item.label}</button></li>
    {/each}
  </ul>
</nav>

<style>
  ul { display: flex; flex-wrap: wrap; gap: .35rem; list-style: none; margin: 0; padding: 0; }
  li { min-inline-size: 0; max-inline-size: 100%; }
  button { min-block-size: 2.75rem; max-inline-size: 100%; padding: .65rem 1rem;
    border: 1px solid transparent; border-radius: var(--bridge-radius); background: transparent;
    color: var(--bridge-muted); font: inherit; font-weight: 600; line-height: 1.4;
    overflow-wrap: anywhere; white-space: normal; cursor: pointer; }
  button[aria-current='page'] { color: var(--bridge-accent-text); background: var(--bridge-accent-soft);
    border-color: var(--bridge-accent); text-decoration: underline; text-underline-offset: .3em; }
  button:hover:not(:disabled) { border-color: var(--bridge-border); }
  button:disabled { color: var(--bridge-disabled-text); cursor: default; }
</style>
