<script lang="ts">
  import type { HTMLInputAttributes } from 'svelte/elements';

  export interface Props {
    id: string;
    label: string;
    value?: string;
    type?: 'text' | 'search' | 'email' | 'password' | 'number' | 'url' | 'tel';
    description?: string;
    error?: string;
    required?: boolean;
    disabled?: boolean;
    readonly?: boolean;
    placeholder?: string;
    name?: string;
    autocomplete?: HTMLInputAttributes['autocomplete'];
    inputmode?: HTMLInputAttributes['inputmode'];
    oninput?: (value: string) => void;
  }

  let { id, label, value = $bindable(''), type = 'text', description, error,
    required = false, disabled = false, readonly = false, placeholder, name,
    autocomplete, inputmode, oninput }: Props = $props();
  const describedBy = $derived([description ? `${id}-description` : '',
    error ? `${id}-error` : ''].filter(Boolean).join(' ') || undefined);

  function edit(event: Event & { currentTarget: HTMLInputElement }): void {
    if (disabled || readonly) return;
    value = event.currentTarget.value;
    oninput?.(value);
  }
</script>

<div class="bridge-field">
  <label for={id}>{label}{#if required}<span class="bridge-sr-only"> (required)</span>{/if}</label>
  {#if description}<p id={`${id}-description`} class="description">{description}</p>{/if}
  <input {id} {name} {type} {value} {required} {disabled} {readonly} {placeholder}
    {autocomplete} {inputmode} aria-invalid={error ? 'true' : undefined}
    aria-describedby={describedBy} oninput={edit} />
  {#if error}<p id={`${id}-error`} class="error">{error}</p>{/if}
</div>

<style>
  .bridge-field { display: grid; gap: .4rem; min-inline-size: 0; }
  label { font-weight: 600; overflow-wrap: anywhere; }
  p { margin: 0; overflow-wrap: anywhere; }
  .description { color: var(--bridge-muted); font-size: .9375rem; }
  .error { color: var(--bridge-danger-text); font-size: .9375rem; }
  input { inline-size: 100%; min-inline-size: 0; min-block-size: 2.75rem;
    padding: .65rem .75rem; border: 1px solid var(--bridge-border);
    border-radius: var(--bridge-radius); background: var(--bridge-surface);
    color: var(--bridge-text); font: inherit; line-height: 1.4; }
  input[aria-invalid='true'] { border-color: var(--bridge-danger-text); border-width: 2px; }
  input::placeholder { color: var(--bridge-muted); opacity: 1; }
  input:disabled { background: var(--bridge-disabled-surface); color: var(--bridge-disabled-text); }
</style>
