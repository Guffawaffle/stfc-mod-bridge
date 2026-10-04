import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import os from 'node:os';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { verifyDependencyBoundary, selectShellArtifact } from '../foundation-checks.mjs';
import { registry } from '../gate-registry.mjs';
import { fingerprintInputs, qualificationInputs } from '../qualification.mjs';

function fixture(extra = {}) {
  const names = ['bridge-contracts', 'bridge-domain', 'bridge-engine', 'bridge-app', 'bridge-desktop', 'tauri', 'wry'];
  return {
    packages: names.map(name => ({ id: name, name })), workspace_members: names.slice(0, 5),
    resolve: { nodes: names.map(id => ({ id, dependencies: extra[id] || (id === 'bridge-desktop' ? ['tauri'] : id === 'tauri' ? ['wry'] : []) })) }
  };
}

test('foundation freshness covers macOS source edits and new local crate membership', () => {
  const repository = path.resolve(import.meta.dirname, '../../..');
  const temporaryRoot = realpathSync(os.tmpdir());
  const root = mkdtempSync(path.join(temporaryRoot, 'bridge-foundation-inputs-'));
  const inputs = qualificationInputs([registry['workspace-foundation']]);
  try {
    for (const relative of inputs) {
      const target = path.join(root, relative);
      if (statSync(path.join(repository, relative)).isDirectory()) mkdirSync(target, { recursive: true });
      else {
        mkdirSync(path.dirname(target), { recursive: true });
        writeFileSync(target, 'Controlled source inventory fixture.');
      }
    }
    const macSource = 'crates/bridge-platform-macos/src/lib.rs';
    mkdirSync(path.dirname(path.join(root, macSource)), { recursive: true });
    writeFileSync(path.join(root, macSource), 'First Mac source.');
    const before = fingerprintInputs(root, inputs);
    assert.ok(before.files.some(record => record.path === macSource));
    writeFileSync(path.join(root, macSource), 'Changed Mac source.');
    const edited = fingerprintInputs(root, inputs);
    assert.notEqual(edited.sha256, before.sha256);
    const newSource = 'crates/bridge-fixture-added/src/lib.rs';
    mkdirSync(path.dirname(path.join(root, newSource)), { recursive: true });
    writeFileSync(path.join(root, newSource), 'New local crate source.');
    const added = fingerprintInputs(root, inputs);
    assert.ok(added.files.some(record => record.path === newSource));
    assert.notEqual(added.sha256, edited.sha256);
  } finally {
    const actual = realpathSync(root);
    assert.equal(path.dirname(actual), temporaryRoot);
    assert.ok(path.basename(actual).startsWith('bridge-foundation-inputs-'));
    rmSync(actual, { recursive: true, force: true });
  }
});
test('renderer dependencies in the shell do not contaminate core boundary', () => {
  assert.deepEqual(verifyDependencyBoundary(fixture())['bridge-engine'], ['bridge-engine']);
});
test('direct and transitive renderer contamination is rejected', () => {
  assert.throws(() => verifyDependencyBoundary(fixture({ 'bridge-engine': ['tauri'] })), /renderer package tauri/);
  assert.throws(() => verifyDependencyBoundary(fixture({ 'bridge-engine': ['bridge-domain'], 'bridge-domain': ['wry'] })), /renderer package wry/);
  assert.throws(() => verifyDependencyBoundary(fixture({ 'bridge-app': ['tauri'] })), /renderer package tauri/);
});
test('missing workspace roots or unresolved dependencies cannot prove independence', () => {
  const missing = fixture(); missing.workspace_members = [];
  assert.throws(() => verifyDependencyBoundary(missing), /Missing workspace/);
  assert.throws(() => verifyDependencyBoundary(fixture({ 'bridge-engine': ['absent'] })), /Unresolved dependency/);
});
test('artifact comes from this Cargo invocation and its explicit host route, not an old default path', () => {
  const root = path.join(os.tmpdir(), 'synthetic-bridge-root');
  const hostTarget = 'x86_64-pc-windows-msvc';
  const executable = path.join(root, 'target', hostTarget, 'release', 'bridge-desktop.exe');
  const artifact = { reason: 'compiler-artifact', package_id: 'desktop-id', target: { name: 'bridge-desktop', kind: ['bin'] }, features: ['custom-protocol'], executable, fresh: false };
  const options = { output: JSON.stringify(artifact), packageId: 'desktop-id', root, hostTarget, platform: 'win32' };
  assert.equal(selectShellArtifact(options).executable, executable);
  const staleDefault = { ...artifact, executable: path.join(root, 'target/release/bridge-desktop.exe') };
  assert.throws(() => selectShellArtifact({ ...options, output: JSON.stringify(staleDefault) }), /output route/);
  assert.throws(() => selectShellArtifact({ ...options, output: '' }), /exactly one/);
  assert.throws(() => selectShellArtifact({ ...options, output: `${options.output}\n${options.output}` }), /exactly one/);
  assert.throws(() => selectShellArtifact({ ...options, output: JSON.stringify({ ...artifact, features: [] }) }), /production frontend/);
});
