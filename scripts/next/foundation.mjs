import assert from 'node:assert/strict';
import { spawnSync, spawn } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync, readdirSync, realpathSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import path from 'node:path';
import os from 'node:os';
import { verifyDependencyBoundary, selectShellArtifact } from './foundation-checks.mjs';
import { rustContext } from './rust-context.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const host = process.platform === 'win32' && process.arch === 'x64' ? 'windows-x64' : process.platform === 'darwin' && process.arch === 'arm64' ? 'macos-arm64-native' : `${process.platform}-${process.arch}`;
assert.ok(['windows-x64', 'macos-arm64-native'].includes(host), `Unsupported foundation host ${host}`);
if (process.env.BRIDGE_EXPECTED_HOST) assert.equal(host, process.env.BRIDGE_EXPECTED_HOST, 'CI native host does not match its declared matrix target');
const expectedNode = JSON.parse(readFileSync(path.join(root, 'package.json'))).engines.node;
assert.equal(process.version, `v${expectedNode}`, 'Node runtime differs from the workspace pin');
const directory = path.join(root, 'artifacts/next/foundation', randomUUID());
mkdirSync(directory, { recursive: true });
const checks = [];
function run(id, argv, timeout = 900000) {
  return runTool(id, process.execPath, argv, process.env, timeout);
}
function runTool(id, executable, argv, env, timeout = 900000) {
  const started = Date.now();
  console.log(`Foundation: ${id}`);
  const result = spawnSync(executable, argv, { cwd: root, env, encoding: 'utf8', windowsHide: true, timeout, maxBuffer: 16 * 1024 * 1024 });
  const log = path.join(directory, `${id}.log`);
  writeFileSync(log, `${result.stdout || ''}\n${result.stderr || ''}`);
  checks.push({ id, executable, argv, cwd: root, exitCode: result.status, durationMs: Date.now() - started, log });
  if (result.status !== 0 || result.error) throw new Error(`${id} failed: ${result.error?.message || (result.stderr || result.stdout || '').slice(-5000)}`);
  return result.stdout;
}
async function browserStartup() {
  const child = spawn(process.execPath, [path.join(root, 'ui/node_modules/vite/bin/vite.js'), '--host', '127.0.0.1', '--port', '0', '--strictPort'], { cwd: path.join(root, 'ui'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  try {
    const url = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Browser development server did not start in 30 seconds')), 30000);
      const fail = error => { clearTimeout(timer); reject(error); };
      child.once('error', fail);
      child.once('exit', code => fail(new Error(`Browser development server exited: ${code}`)));
      child.stderr.on('data', bytes => { output = (output + bytes).slice(-8000); });
      child.stdout.on('data', bytes => {
        output = (output + bytes).slice(-8000);
        const match = output.replace(/\u001b\[[0-9;]*m/g, '').match(/http:\/\/127\.0\.0\.1:\d+\//);
        if (match) { clearTimeout(timer); resolve(match[0]); }
      });
    });
    const response = await fetch(url, { signal: AbortSignal.timeout(10000) });
    assert.equal(response.status, 200);
    assert.match(await response.text(), /\/src\/main\.ts/);
    const entry = await fetch(new URL('src/main.ts', url), { signal: AbortSignal.timeout(10000) });
    assert.equal(entry.status, 200);
    assert.match(await entry.text(), /mount/);
    return { boundary: 'isolated-browser-startup-check', actualHost: host, transport: 'browser-only-vite', gameRequired: false, rustBuildInvoked: false, nativeLibraryRequired: false };
  } finally {
    writeFileSync(path.join(directory, 'browser-startup.log'), output);
    if (child.exitCode === null) {
      const exited = new Promise(resolve => child.once('exit', resolve));
      child.kill();
      await Promise.race([exited, new Promise(resolve => setTimeout(resolve, 5000))]);
      assert.ok(child.exitCode !== null || child.signalCode !== null, 'Owned development server did not exit');
    }
  }
}

run('dispatcher-tests', ['--test', 'scripts/next/tests/qualification.test.mjs', 'scripts/next/tests/prerequisites.test.mjs', 'scripts/next/tests/input-tree.test.mjs', 'scripts/next/tests/foundation.test.mjs', 'scripts/next/tests/rust-context.test.mjs']);
run('frontend-typecheck', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']);
run('frontend-tests', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'test']);
run('frontend-build', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'build']);
run('frontend-production-audit', ['scripts/next/pnpm.mjs', 'audit', '--prod', '--json']);
const rust = rustContext({ root });
const compilerVersion = runTool('rustc-version', rust.rustc, [`+${rust.pin}`, '-vV'], rust.env);
assert.ok(compilerVersion.split(/\r?\n/).includes(`release: ${rust.pin}`), 'Actual compiler differs from the exact Rust pin');
assert.ok(compilerVersion.split(/\r?\n/).includes(`host: ${rust.hostTarget}`), 'Actual compiler host differs from the native target');
const cargoVersion = run('cargo-version', ['scripts/next/cargo.mjs', '--version']).trim();
assert.equal(cargoVersion.split(' ')[1], rust.pin, 'Actual Cargo differs from the pinned Rust toolchain');
run('rust-format', ['scripts/next/cargo.mjs', 'fmt', '--all', '--', '--check']);
run('rust-clippy', ['scripts/next/cargo.mjs', 'clippy', '--workspace', '--all-targets', '--locked', '--', '-D', 'warnings']);
run('rust-tests', ['scripts/next/cargo.mjs', 'test', '--workspace', '--all-targets', '--locked']);
const metadata = JSON.parse(run('cargo-metadata', ['scripts/next/cargo.mjs', 'metadata', '--format-version', '1', '--locked']));
const dependencyBoundary = verifyDependencyBoundary(metadata);
const buildOutput = run('native-shell-build', ['scripts/next/cargo.mjs', 'build', '--locked', '--release', '--target', rust.hostTarget, '--features', 'custom-protocol', '--message-format', 'json', '-p', 'bridge-desktop']);
const desktopPackage = metadata.packages.find(p => p.name === 'bridge-desktop' && metadata.workspace_members.includes(p.id));
assert.ok(desktopPackage, 'Desktop package identity is missing');
const artifact = selectShellArtifact({ output: buildOutput, packageId: desktopPackage.id, root, hostTarget: rust.hostTarget, platform: process.platform });
const binary = realpathSync(artifact.executable);
const relation = path.relative(realpathSync(root), binary);
assert.ok(relation !== '..' && !relation.startsWith(`..${path.sep}`) && !path.isAbsolute(relation), 'Native build output link escaped the owning root');
const bytes = readFileSync(binary);
if (process.platform === 'win32') { assert.equal(bytes.toString('ascii', 0, 2), 'MZ'); assert.equal(bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4), 0x8664); }
else { assert.equal(bytes.readUInt32LE(0), 0xfeedfacf); assert.equal(bytes.readUInt32LE(4), 0x0100000c); }
const browser = await browserStartup();
const retainedBinary = path.join(directory, process.platform === 'win32' ? 'unsigned-shell.exe' : 'unsigned-shell');
writeFileSync(retainedBinary, bytes);
for (const file of readdirSync(path.join(root, 'ui/dist/assets'))) {
  if (!file.endsWith('.js')) continue;
  const script = readFileSync(path.join(root, 'ui/dist/assets', file), 'utf8');
  assert.doesNotMatch(script, /tauri-plugin-wdio|wdio-webdriver|browser\.tauri\.execute/);
}
const receipt = {
  schemaVersion: 'bridge-foundation-observation/v1', host: { id: host, osRelease: os.release(), architecture: process.arch, node: process.version },
  completedAt: new Date().toISOString(), checks, dependencyBoundary, browser,
  compiler: { pin: rust.pin, hostTarget: rust.hostTarget, rustc: compilerVersion.trim(), cargo: cargoVersion },
  nativeBuild: { path: path.relative(root, retainedBinary), cargoExecutable: relation, features: artifact.features, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex'), boundary: 'unbundled-unsigned-shell-build-with-production-assets' },
  automation: { service: '@wdio/tauri-service@1.4.0', driverProvider: 'embedded', nativeTestsExecuted: false, supplyChainQualification: 'pending-issue-235' },
  nativeRuntimeQualified: false, releaseQualified: false
};
writeFileSync(path.join(directory, 'observation.json'), JSON.stringify(receipt, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', host, receipt: path.join(directory, 'observation.json'), nativeRuntimeQualified: false, releaseQualified: false }));
