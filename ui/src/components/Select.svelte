<script lang="ts">
  export interface SelectOption { value: string; label: string; disabled?: boolean }
  export interface Props {
    id: string;
    label: string;
    options: readonly SelectOption[];
    value?: string;
    description?: string;
    error?: string;
    required?: boolean;
    disabled?: boolean;
    name?: string;
    onchange?: (value: string) => void;
  }

  let { id, label, options, value = $bindable(''), description, error,
    required = false, disabled = false, name, onchange }: Props = $props();
  const describedBy = $derived([description ? `${id}-description` : '',
    error ? `${id}-error` : ''].filter(Boolean).join(' ') || undefined);

  function choose(event: Event & { currentTarget: HTMLSelectElement }): void {
    if (disabled) return;
    value = event.currentTarget.value;
    onchange?.(value);
  }
</script>

<div class="bridge-select">
  <label for={id}>{label}{#if required}<span class="bridge-sr-only"> (required)</span>{/if}</label>
  {#if description}<p id={`${id}-description`} class="description">{description}</p>{/if}
  <select {id} {name} {required} {disabled} bind:value aria-invalid={error ? 'true' : undefined}
    aria-describedby={describedBy} onchange={choose}>
    {#each options as option (option.value)}
      <option value={option.value} disabled={option.disabled}>{option.label}</option>
    {/each}
  </select>
  {#if error}<p id={`${id}-error`} class="error">{error}</p>{/if}
</div>

<style>
  .bridge-select { display: grid; gap: .4rem; min-inline-size: 0; }
  label { font-weight: 600; overflow-wrap: anywhere; }
  p { margin: 0; overflow-wrap: anywhere; }
  .description { color: var(--bridge-muted); font-size: .9375rem; }
  .error { color: var(--bridge-danger-text); font-size: .9375rem; }
  select { inline-size: 100%; min-inline-size: 0; min-block-size: 2.75rem;
    padding: .65rem .75rem; border: 1px solid var(--bridge-border);
    border-radius: var(--bridge-radius); background: var(--bridge-surface);
    color: var(--bridge-text); font: inherit; line-height: 1.4; }
  select[aria-invalid='true'] { border-color: var(--bridge-danger-text); border-width: 2px; }
  select:disabled { background: var(--bridge-disabled-surface); color: var(--bridge-disabled-text); }
</style>
