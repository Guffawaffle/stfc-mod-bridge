import type { AstrometricsView } from '../../generated/protocol';
import type { DeepReadonly } from '../../client';
export function connectionSettings(view: DeepReadonly<AstrometricsView>): string {
  if (!view.enabled || !view.endpoint || !view.syncToken) return '';
  // Explicit empty proxy overrides the mod's inherited global sync proxy.
  return `[sync.targets.astrometrics]\nurl = "${view.endpoint}"\ntoken = "${view.syncToken}"\nproxy = ""\ndiagnostics = true\n`
    + ['away_assignments', 'battlelogs', 'buffs', 'buildings', 'haven', 'inventory', 'jobs', 'missions', 'officers', 'research', 'resources', 'ships', 'slots', 'tech', 'traits'].map(key => `${key} = false`).join('\n');
}
