import assert from 'node:assert/strict';
import { realpathSync } from 'node:fs';
import path from 'node:path';

// Select only executable test subjects emitted by this successful Cargo call.
// The caller additionally owns/confines, hashes and checks their architecture.
export function selectHostArtifacts(output, { root, targets, physical = realpathSync.native }) {
  const messages = output.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
  const finished = messages.filter(message => message.reason === 'build-finished');
  assert.equal(finished.length, 1, 'Require one complete Cargo build result');
  assert.equal(finished[0].success, true, 'Current Cargo build must succeed');
  const artifacts = messages.filter(message => message.reason === 'compiler-artifact'
    && message.executable && message.profile?.test === true);
  assert.equal(artifacts.length, targets.length, 'Require only the declared current test executables');
  return targets.map(target => {
    const selected = artifacts.filter(artifact => artifact.target?.name === target.name);
    assert.equal(selected.length, 1, `Require one current ${target.name} compiler artifact`);
    const artifact = selected[0];
    assert.deepEqual(artifact.target.kind, [target.kind], 'Cargo test subject kind differs from its declared target');
    assert.equal(physical(artifact.manifest_path), physical(path.join(root, target.manifest)), 'Cargo test subject manifest mismatch');
    assert.equal(physical(artifact.target.src_path), physical(path.join(root, target.source)), 'Cargo test subject source mismatch');
    assert.equal(typeof artifact.executable, 'string', 'Cargo test subject executable must be a path');
    assert.ok(path.isAbsolute(artifact.executable), 'Cargo executable path must be absolute');
    return artifact;
  });
}
