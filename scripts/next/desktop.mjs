import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const context = rustContext({ root });
const result = spawnSync(process.execPath, [path.join(root, 'ui/node_modules/@tauri-apps/cli/tauri.js'), ...process.argv.slice(2)], { cwd: path.join(root, 'apps/desktop'), env: context.env, stdio: 'inherit', windowsHide: true });
if (result.error) console.error(result.error.message);
process.exitCode = result.status ?? 1;
