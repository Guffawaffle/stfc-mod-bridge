export interface FocusTarget {
  readonly isConnected: boolean;
  focus(options?: FocusOptions): void;
}

/** Native modal operations are isolated here so lifecycle behavior has a small testable port. */
export interface ModalPort {
  readonly open: boolean;
  showModal(): void;
  close(): void;
  focus(): void;
  activeElement(): FocusTarget | null;
  contains(target: FocusTarget): boolean;
  focusable(): readonly FocusTarget[];
}

export interface ModalController {
  sync(open: boolean): void;
  stay(): void;
  cancel(event: { preventDefault(): void }): void;
  keydown(event: { key: string; shiftKey: boolean; preventDefault(): void }): void;
  closed(): void;
  destroy(): void;
}

export function createModalController(port: ModalPort, options: {
  onstay: () => void;
  busy: () => boolean;
  initialFocus?: () => FocusTarget | null;
}): ModalController {
  let active = false;
  let requested = false;
  let destroyed = false;
  let opener: FocusTarget | null = null;

  function focusInside(): void {
    const preferred = options.initialFocus?.();
    const target = preferred?.isConnected && port.contains(preferred) ? preferred : port.focusable()[0];
    if (target) target.focus({ preventScroll: true });
    else port.focus();
  }

  function restore(): void {
    const target = opener;
    opener = null;
    if (target?.isConnected) target.focus({ preventScroll: true });
  }

  function close(): void {
    active = false;
    requested = false;
    if (port.open) port.close();
    restore();
  }

  function stay(): void {
    if (!active || destroyed || requested || options.busy()) return;
    requested = true;
    options.onstay();
  }

  return {
    sync(open) {
      if (destroyed) return;
      if (open && !active) {
        opener = port.activeElement();
        port.showModal();
        active = true;
        requested = false;
        focusInside();
      } else if (!open && active) close();
    },
    stay,
    cancel(event) { event.preventDefault(); stay(); },
    keydown(event) {
      if (!active || event.key !== 'Tab') return;
      const candidates = port.focusable();
      const first = candidates[0];
      const last = candidates[candidates.length - 1];
      const current = port.activeElement();
      if (!first || !last) { event.preventDefault(); port.focus(); }
      else if (!current || !port.contains(current) || (event.shiftKey ? current === first : current === last)) {
        event.preventDefault();
        (event.shiftKey ? last : first).focus({ preventScroll: true });
      }
    },
    closed() {
      // A native close event may be queued by the previous modal generation.
      if (!active || destroyed || port.open) return;
      if (options.busy()) { port.showModal(); focusInside(); return; }
      stay();
      close();
    },
    destroy() {
      if (destroyed) return;
      destroyed = true;
      if (active) close();
    }
  };
}

export function nativeModalPort(dialog: HTMLDialogElement): ModalPort {
  return {
    get open() { return dialog.open; },
    showModal: () => dialog.showModal(),
    close: () => dialog.close(),
    focus: () => dialog.focus({ preventScroll: true }),
    activeElement: () => dialog.ownerDocument.activeElement as HTMLElement | null,
    contains: target => target instanceof dialog.ownerDocument.defaultView!.HTMLElement && dialog.contains(target),
    focusable: () => Array.from(dialog.querySelectorAll<HTMLElement>(
      'a[href],button,input,select,textarea,[tabindex],[contenteditable="true"]'
    )).filter(element => element.tabIndex >= 0 && !element.matches(':disabled')
      && !element.closest('[inert]') && element.getClientRects().length > 0)
      .sort((left, right) => {
        const l = left.tabIndex > 0 ? left.tabIndex : Number.MAX_SAFE_INTEGER;
        const r = right.tabIndex > 0 ? right.tabIndex : Number.MAX_SAFE_INTEGER;
        return l - r;
      })
  };
}
