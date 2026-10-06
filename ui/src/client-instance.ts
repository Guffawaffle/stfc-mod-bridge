import { BridgeClient, UnavailableTransport } from './client';

// The native adapter is composed by its own package. An unbound application
// reports delivery unavailability through the same client used by that adapter.
export const bridgeClient = new BridgeClient(new UnavailableTransport(), {
  requestId: () => crypto.randomUUID()
});
