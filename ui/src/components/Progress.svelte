<script lang="ts">
  import { presentProgress, type ProgressConfidence } from './progress';

  export interface Props {
    label: string;
    value?: number;
    max?: number;
    detail?: string;
    confidence?: ProgressConfidence;
  }

  let { label, value, max = 100, detail, confidence = 'current' }: Props = $props();
  const id = $props.id();
  const presentation = $derived(presentProgress(value, max, confidence));
</script>

<div class="bridge-progress" data-confidence={confidence}>
  <div class="heading"><label for={`${id}-bar`}>{label}</label><span>{presentation.text}</span></div>
  {#if presentation.determinate}
    <progress id={`${id}-bar`} max={presentation.max} value={presentation.value}
      aria-describedby={`${id}-state${detail ? ` ${id}-detail` : ''}`}></progress>
  {:else}
    <progress id={`${id}-bar`} max={presentation.max}
      aria-describedby={`${id}-state${detail ? ` ${id}-detail` : ''}`}></progress>
  {/if}
  <span id={`${id}-state`} class="bridge-sr-only">{presentation.text}</span>
  {#if detail}<p id={`${id}-detail`}>{detail}</p>{/if}
</div>

<style>
  .bridge-progress { display: grid; gap: .55rem; min-inline-size: 0; }
  .heading { display: flex; justify-content: space-between; align-items: baseline;
    flex-wrap: wrap; gap: .4rem 1rem; overflow-wrap: anywhere; }
  label { font-weight: 600; }
  .heading span, p { color: var(--bridge-muted); }
  p { margin: 0; overflow-wrap: anywhere; }
  progress { inline-size: 100%; block-size: .75rem; appearance: none; border: 1px solid var(--bridge-border);
    border-radius: 999px; overflow: hidden; background: var(--bridge-disabled-surface); accent-color: var(--bridge-accent); }
  progress::-webkit-progress-bar { background: var(--bridge-disabled-surface); }
  progress::-webkit-progress-value { background: var(--bridge-accent); }
  progress::-moz-progress-bar { background: var(--bridge-accent); }
  progress:indeterminate { background: var(--bridge-disabled-surface); }
  progress:indeterminate::-moz-progress-bar { background: transparent; }
  @media (forced-colors: active) {
    progress { appearance: auto; border-color: CanvasText; }
  }
</style>
