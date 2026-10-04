import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const context = rustContext({ root });
ownedArtifactPath(root, 'target', 'directory', { allowMissing: true });
ownedArtifactPath(root, `target/${context.hostTarget}`, 'directory', { allowMissing: true });
const result = spawnSync(context.cargo, [`+${context.pin}`, ...process.argv.slice(2)], { cwd: root, env: context.env, stdio: 'inherit', windowsHide: true });
if (result.error) console.error(result.error.message);
process.exitCode = result.status ?? 1;
