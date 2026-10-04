<script lang="ts">
  import type { Snippet } from 'svelte';
  import Button from './Button.svelte';
  import { createModalController, nativeModalPort, type ModalController } from './modal';

  export interface Props {
    open: boolean;
    title: string;
    description?: string;
    busy?: boolean;
    onstay: () => void;
    children?: Snippet;
    actions?: Snippet;
    initialFocus?: () => HTMLElement | null;
  }

  let { open, title, description, busy = false, onstay, children, actions, initialFocus }: Props = $props();
  const id = $props.id();
  let controller = $state<ModalController>();

  function connect(dialog: HTMLDialogElement): { destroy: () => void } {
    const mounted = createModalController(nativeModalPort(dialog), {
      onstay: () => onstay(), busy: () => busy, initialFocus: () => initialFocus?.() ?? null
    });
    controller = mounted;
    return { destroy: () => { mounted.destroy(); controller = undefined; } };
  }

  $effect(() => { controller?.sync(open); });
</script>

<dialog use:connect class="bridge-dialog" tabindex="-1" aria-modal="true"
  aria-labelledby={`${id}-title`} aria-describedby={description ? `${id}-description` : undefined}
  aria-busy={busy ? 'true' : undefined} oncancel={event => controller?.cancel(event)}
  onkeydown={event => controller?.keydown(event)} onclose={() => controller?.closed()}>
  <h2 id={`${id}-title`}>{title}</h2>
  {#if description}<p id={`${id}-description`} class="description">{description}</p>{/if}
  <fieldset disabled={busy} aria-label={title}>
    {#if children}<div class="content">{@render children()}</div>{/if}
    <div class="actions">
      {@render actions?.()}
      <Button disabled={busy} onclick={() => controller?.stay()}>Stay</Button>
    </div>
  </fieldset>
</dialog>

<style>
  .bridge-dialog { inline-size: min(36rem, calc(100% - 2rem)); max-inline-size: calc(100% - 2rem);
    max-block-size: calc(100% - 2rem); overflow: auto; margin: auto;
    border: 1px solid var(--bridge-border); border-radius: var(--bridge-radius-large);
    padding: 1.5rem; background: var(--bridge-surface); color: var(--bridge-text);
    box-shadow: var(--bridge-shadow); overflow-wrap: anywhere; }
  .bridge-dialog::backdrop { background: var(--bridge-backdrop); }
  h2 { margin: 0; font-size: 1.35rem; line-height: 1.4; }
  .description { margin: .75rem 0 0; color: var(--bridge-muted); }
  fieldset { min-inline-size: 0; margin: 0; padding: 0; border: 0; }
  .content { margin-block-start: 1.25rem; }
  .actions { display: flex; justify-content: flex-end; flex-wrap: wrap; gap: .65rem; margin-block-start: 1.5rem; }
  @media (max-width: 36rem) { .bridge-dialog { padding: 1rem; } }
  @media (forced-colors: active) { .bridge-dialog { box-shadow: none; border: 2px solid CanvasText; } }
</style>
