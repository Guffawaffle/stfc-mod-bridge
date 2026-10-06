export interface Announcement { readonly id: number; readonly message: string; }
export interface Announcements { readonly polite: Announcement; readonly assertive: Announcement; }
export class AnnouncementController {
  private sequence = 0;
  private current: Announcements = Object.freeze({ polite: Object.freeze({ id: 0, message: '' }), assertive: Object.freeze({ id: 0, message: '' }) });
  private listeners = new Set<(state: Announcements) => void>();
  get state(): Announcements { return this.current; }
  subscribe(listener: (state: Announcements) => void): () => void { this.listeners.add(listener); listener(this.current); return () => { this.listeners.delete(listener); }; }
  announce(message: string, priority: 'polite' | 'assertive' = 'polite'): void {
    if (!message.trim() || message.length > 512) return;
    this.current = Object.freeze({ ...this.current, [priority]: Object.freeze({ id: ++this.sequence, message }) });
    for (const listener of this.listeners) listener(this.current);
  }
}
export interface FocusTarget { readonly isConnected: boolean; focus(options?: { preventScroll?: boolean }): void; }
/** Explicit element registration avoids selectors derived from backend data. */
export class FocusController {
  private readonly targets = new Map<string, () => FocusTarget | null | undefined>();
  register(key: string, target: () => FocusTarget | null | undefined): () => void {
    if (!key || key.length > 128 || !this.targets.has(key) && this.targets.size >= 128) throw new RangeError('focus_limit');
    this.targets.set(key, target);
    return () => { if (this.targets.get(key) === target) this.targets.delete(key); };
  }
  restore(key?: string): boolean {
    if (!key) return false;
    const target = this.targets.get(key)?.();
    if (!target?.isConnected) return false;
    target.focus({ preventScroll: true }); return true;
  }
}
