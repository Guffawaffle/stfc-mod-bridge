import { invoke, isTauri } from '@tauri-apps/api/core';
import { BridgeClient, UnavailableTransport, type RawTransport, type RawFrame, type TransportFault } from './client';
import { TauriTransport, TauriRegistrationKeys } from './client/tauri';

/** A native epoch binds this exact document to the root-owned observation host.
 * Browsers remain unbound; development scenarios still inject their own facade. */
class NativeTransport implements RawTransport {
  private readonly binding: Promise<TauriTransport>;
  constructor() {
    this.binding = invoke<unknown>('bridge_bootstrap').then(value => {
      if (!value || typeof value !== 'object' || Array.isArray(value)
        || Object.keys(value).length !== 1 || !('documentEpoch' in value) || typeof value.documentEpoch !== 'string') {
        throw new Error('unavailable_binding');
      }
      const documentEpoch = value.documentEpoch;
      return new TauriTransport({ keys: new TauriRegistrationKeys(documentEpoch),
        invoke: (command, arguments_) => invoke(command, { ...arguments_, documentEpoch }) });
    });
    // Subscribe/exchange attach their own failure delivery; avoid an unhandled
    // rejection if a native document closes before a caller observes bootstrap.
    void this.binding.catch(() => {});
  }
  exchange(request: string, options: { signal: AbortSignal }): Promise<RawFrame> {
    return this.binding.then(transport => transport.exchange(request, options), () => Promise.reject({ code: 'unavailable_binding', delivery: 'not_sent' }));
  }
  subscribe(onEvent: (frame: RawFrame) => void, onFault?: (fault: TransportFault) => void): () => void {
    let active = true, stop: (() => void) | undefined;
    void this.binding.then(transport => { if (active) stop = transport.subscribe(onEvent, onFault); },
      () => { if (active) onFault?.({ code: 'unavailable_binding', delivery: 'not_sent' }); });
    return () => { active = false; stop?.(); };
  }
}
export const nativeObservations = isTauri();
export const bridgeClient = new BridgeClient(nativeObservations ? new NativeTransport() : new UnavailableTransport(), {
  requestId: () => crypto.randomUUID()
});
