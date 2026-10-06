import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { rustContext } from './rust-context.mjs';

const root = path.resolve(import.meta.dirname, '../..');
assert.equal(process.argv.length, 2, 'The protocol suite accepts no overrides');
const rust = rustContext({ root });
const directory = path.join(root, 'artifacts/next/protocol', randomUUID());
mkdirSync(directory, { recursive: true });
const checks = [];
const run = (id, argv, timeout = 180000) => {
  const result = spawnSync(process.execPath, argv, {
    cwd: root, env: process.env, windowsHide: true, encoding: 'utf8', timeout,
    maxBuffer: 4 * 1024 * 1024
  });
  const observation = {
    id, executable: process.execPath, argv, cwd: root,
    exitCode: result.status, error: result.error?.code ?? null,
    stdout: result.stdout ?? '', stderr: result.stderr ?? ''
  };
  checks.push(observation);
  writeFileSync(path.join(directory, `${id}.json`), JSON.stringify(observation, null, 2) + '\n');
  assert.equal(result.status, 0, `Protocol check ${id} failed; inspect its retained observation`);
  assert.ok(!result.error, `Protocol check ${id} did not complete`);
};
run('wire-and-transcript-tests', ['--test', 'contracts/codec/strict-json.test.mjs', 'contracts/codec/transcript.test.mjs', 'scripts/next/tests/protocol-files.test.mjs', 'scripts/next/tests/scenario-coverage.test.mjs']);
run('rust-format', ['scripts/next/cargo.mjs', 'fmt', '-p', 'bridge-contracts', '--', '--check']);
run('rust-clippy', ['scripts/next/cargo.mjs', 'clippy', '--locked', '-p', 'bridge-contracts', '--all-targets', '--', '-D', 'warnings']);
run('rust-contract-tests', ['scripts/next/cargo.mjs', 'test', '--locked', '-p', 'bridge-contracts', '--all-targets']);
run('generated-drift', ['scripts/next/generate-protocol.mjs', '--check']);
run('typed-fixture-sources', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'exec', 'tsc', '--project', '../contracts/fixtures/tsconfig.json']);
run('fixture-source-drift', ['scripts/next/generate-fixtures.mjs', '--check']);
run('generated-types', ['scripts/next/pnpm.mjs', '--dir', 'ui', 'check']);
const receipt = {
  schemaVersion: 'bridge-protocol-observation/v1',
  boundary: 'strict wire decoding, normalized DTO semantics, generated schema/type drift and synthetic transcript relationships',
  actualHost: { platform: process.platform, architecture: process.arch, node: process.version },
  toolchainBinding: { pin: rust.pin, target: rust.hostTarget }, checks,
  engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false
};
writeFileSync(path.join(directory, 'protocol.json'), JSON.stringify(receipt, null, 2) + '\n');
console.log(JSON.stringify({ result: 'passed', receipt: path.join(directory, 'protocol.json'), checks: checks.length, engineExecutionQualified: false, nativeRuntimeQualified: false, releaseQualified: false }));
