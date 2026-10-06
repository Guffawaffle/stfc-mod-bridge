import { readFileSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '../..');
const manifest = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8'));
if (process.version !== `v${manifest.engines.node}`) throw new Error(`Frontend tooling requires Node ${manifest.engines.node}; actual ${process.version}.`);
const cli = path.join(root, 'node_modules/pnpm/bin/pnpm.mjs');
if (!existsSync(cli)) throw new Error('Bootstrap locked dependencies first: npx pnpm@11.19.0 install --ignore-scripts.');
const packageManifest = JSON.parse(readFileSync(path.join(root, 'node_modules/pnpm/package.json'), 'utf8'));
if (packageManifest.version !== manifest.devDependencies.pnpm) throw new Error('Installed pnpm differs from the workspace pin.');
const result = spawnSync(process.execPath, [cli, ...process.argv.slice(2)], { cwd: root, stdio: 'inherit', windowsHide: true });
if (result.error) console.error(result.error.message);
process.exitCode = result.status ?? 1;
