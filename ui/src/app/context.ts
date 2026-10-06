import { getContext, setContext } from 'svelte';
import type { BridgeFacade } from '../state';
const key = Symbol('bridge-screen-context');
export function provideBridge(facade: BridgeFacade): BridgeFacade { return setContext(key, facade); }
export function useBridge(): BridgeFacade {
  const facade = getContext<BridgeFacade | undefined>(key);
  if (!facade) throw new Error('Bridge screen context is unavailable');
  return facade;
}
