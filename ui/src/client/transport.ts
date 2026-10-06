/** Raw frames preserve strict framing checks at the common client boundary. */
export type RawFrame = string | Uint8Array;

/** Safe transport metadata only; adapters never include native errors or frames. */
export interface TransportFault {
  readonly code: 'unavailable_binding' | 'disconnected' | 'delivery_failed';
  readonly delivery: 'not_sent' | 'may_have_reached_backend';
}

export interface RawTransport {
  exchange(request: string, options: { signal: AbortSignal }): Promise<RawFrame>;
  subscribe(onEvent: (frame: RawFrame) => void, onFault?: (fault: TransportFault) => void): () => void;
}

/** An unbound production composition cannot activate a synthetic backend. */
export class UnavailableTransport implements RawTransport {
  exchange(_request: string, _options: { signal: AbortSignal }): Promise<RawFrame> {
    return Promise.reject({ code: 'unavailable_binding', delivery: 'not_sent' } satisfies TransportFault);
  }
  subscribe(_onEvent: (frame: RawFrame) => void, _onFault?: (fault: TransportFault) => void): () => void {
    return () => {};
  }
}
