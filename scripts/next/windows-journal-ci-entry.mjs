// Fixed Node entry under the launcher's owned Job. Native tests observe their
// own token; this script does not claim a Node-token observation.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { closeSync, fstatSync, lstatSync, mkdirSync, openSync, readSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { performance } from 'node:perf_hooks';
import { fingerprintInputRecords } from './input-tree.mjs';
import { ownedArtifactPath } from './owned-artifact.mjs';
import { resolveHostTool } from './host-tools.mjs';
import { GATE_ARGS, PLAN_ARGS, PROJECTION_PATH, requireCiNodeContext, requirePinnedRunnerPackage, requireSelectedPlan } from './windows-journal-ci-entry-policy.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const relative = value => path.relative(root, value).replaceAll('\\', '/');
const sha = value => createHash('sha256').update(value).digest('hex');
const sameStat = (a, b) => ['dev', 'ino', 'nlink', 'size', 'mtimeNs', 'ctimeNs'].every(key => a[key] === b[key]);
const OUTPUT_CAP = 16 * 1024 * 1024;
const inputs = ['scripts/next', '.github/workflows/next-foundation.yml', 'dependencies/next-toolchain.json', 'docs/next/campaign.json', 'docs/plans/rust-tauri-cross-platform/work-packages.json'];
function observe(route, { privateFile = false } = {}) {
  assert.ok(path.isAbsolute(route));
  const lexical = lstatSync(route, { bigint: true });
  assert.ok(lexical.isFile() && !lexical.isSymbolicLink(), 'Observed file route cannot redirect');
  const physical = realpathSync.native(route), initial = lstatSync(physical, { bigint: true });
  assert.ok(initial.isFile() && !initial.isSymbolicLink());
  assert.ok(sameStat(lexical, initial), 'Observed route and physical resource must agree');
  if (privateFile) { assert.equal(physical, route); assert.equal(initial.nlink, 1n, 'Private plan and evidence copies require one link'); }
  const fd = openSync(physical, 'r');
  try {
    assert.ok(sameStat(initial, fstatSync(fd, { bigint: true })));
    assert.ok(initial.size > 0n && initial.size <= 256n * 1024n * 1024n, 'Size bounded before allocation');
    const bytes = Buffer.alloc(Number(initial.size));
    let offset = 0;
    while (offset < bytes.length) { const count = readSync(fd, bytes, offset, bytes.length - offset, offset); assert.ok(count > 0); offset += count; }
    assert.ok(sameStat(initial, fstatSync(fd, { bigint: true })) && sameStat(initial, lstatSync(physical, { bigint: true })));
    assert.equal(realpathSync.native(route), physical);
    return { bytes, observation: { route, physical, bytes: bytes.length, sha256: sha(bytes) } };
  } finally { closeSync(fd); }
}

let directory, receipt, checks = [];
try {
  requireCiNodeContext({ argv: process.argv, execArgv: process.execArgv, platform: process.platform, arch: process.arch, version: process.version, environment: process.env });
  assert.equal(realpathSync.native(process.cwd()), realpathSync.native(root), 'Canonical owning cwd required');
  assert.ok(lstatSync(root).isDirectory() && !lstatSync(root).isSymbolicLink());
  const sources = fingerprintInputRecords(root, inputs);
  const node = observe(process.execPath).observation;
  // Resolve the installed npm shim to select its adjacent package; never run
  // the batch shim or accept a caller-selected JS entry from its contents.
  const shim = resolveHostTool('lexrunner.cmd', process.env);
  const prefix = path.dirname(shim);
  assert.ok(lstatSync(prefix).isDirectory() && !lstatSync(prefix).isSymbolicLink());
  assert.equal(realpathSync.native(prefix), prefix, 'npm prefix cannot redirect');
  const manifestRoute = ownedArtifactPath(prefix, 'node_modules/@smartergpt/lexrunner/package.json');
  const cliRoute = ownedArtifactPath(prefix, 'node_modules/@smartergpt/lexrunner/dist/cli.js');
  assert.equal(manifestRoute, path.join(prefix, 'node_modules', '@smartergpt', 'lexrunner', 'package.json'));
  assert.equal(cliRoute, path.join(prefix, 'node_modules', '@smartergpt', 'lexrunner', 'dist', 'cli.js'));
  const manifest = observe(manifestRoute);
  requirePinnedRunnerPackage(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(manifest.bytes)));
  const cli = observe(cliRoute).observation;
  const git = observe(resolveHostTool('git.exe', process.env)).observation;
  const tools = [node, observe(shim).observation, manifest.observation, cli, git];
  const generated = [], retained = [];
  const fence = () => {
    assert.deepEqual(fingerprintInputRecords(root, inputs), sources, 'CI entry source changed');
    assert.deepEqual(tools.map(item => observe(item.route).observation), tools, 'CI entry tool route/bytes changed');
    assert.equal(ownedArtifactPath(prefix, 'node_modules/@smartergpt/lexrunner/package.json'), manifestRoute);
    assert.equal(ownedArtifactPath(prefix, 'node_modules/@smartergpt/lexrunner/dist/cli.js'), cliRoute);
    for (const item of generated) {
      assert.equal(ownedArtifactPath(root, relative(item.route)), item.route);
      assert.deepEqual(observe(item.route, { privateFile: true }).observation, item, 'Generated plan/projection changed');
    }
    for (const item of retained) {
      assert.equal(ownedArtifactPath(root, relative(item.route)), item.route);
      assert.deepEqual(observe(item.route, { privateFile: true }).observation, item, 'Retained plan/projection/prerequisite changed');
    }
  };
  directory = ownedArtifactPath(root, `artifacts/next/windows-journal-ci-entry/${randomUUID()}`, 'directory', { allowMissing: true });
  mkdirSync(directory, { recursive: true });
  ownedArtifactPath(root, relative(directory), 'directory');
  const save = (name, bytes) => {
    const file = ownedArtifactPath(root, `${relative(directory)}/${name}`, 'file', { allowMissing: true });
    writeFileSync(file, bytes, { flag: 'wx' });
    return { path: relative(file), bytes: bytes.length, sha256: sha(bytes) };
  };
  const startedAt = new Date().toISOString(), deadline = performance.now() + 640000;
  receipt = { schemaVersion: 'bridge-windows-journal-ci-entry/v1', result: 'failed', startedAt, inputs, sources, tools, generated, retained, checks, nodeTokenDirectlyObserved: false, mappedImageAttested: false, packageAcceptance: false, fullBr06Accepted: false, nativeRuntimeQualified: false, releaseQualified: false };
  const run = (id, argv, budget, executable = node.physical) => {
    fence(); const remaining = Math.floor(deadline - performance.now()); assert.ok(remaining > 0);
    const started = new Date().toISOString();
    const result = spawnSync(executable, argv, { cwd: root, env: process.env, windowsHide: true, encoding: null, timeout: Math.min(budget, remaining), maxBuffer: OUTPUT_CAP });
    const stdout = result.stdout ?? Buffer.alloc(0), stderr = result.stderr ?? Buffer.alloc(0);
    assert.ok(stdout.length + stderr.length <= OUTPUT_CAP * 2, 'Failure capture ceiling exceeded');
    const row = { id, executable, argv, cwd: root, startedAt: started, completedAt: new Date().toISOString(), pid: result.pid ?? null, exitCode: result.status, signal: result.signal ?? null, error: result.error?.code ?? null, captureComplete: !result.error, outputBoundSatisfied: stdout.length + stderr.length <= OUTPUT_CAP, stdout: save(`${id}.stdout.log`, stdout), stderr: save(`${id}.stderr.log`, stderr) };
    checks.push(row);
    console.log(JSON.stringify({ ciEntryCommand: id, pid: row.pid, exitCode: row.exitCode, error: row.error }));
    fence(); assert.equal(result.status, 0); assert.equal(result.signal, null); assert.ok(!result.error && row.outputBoundSatisfied && Number.isInteger(row.pid) && row.pid > 0);
    return stdout;
  };
  const head = () => new TextDecoder('utf-8', { fatal: true }).decode(run(`source-head-${checks.length}`, ['--no-optional-locks', 'rev-parse', 'HEAD'], 10000, git.physical)).trim();
  const sourceHead = head(); assert.match(sourceHead, /^[0-9a-f]{40}$/); receipt.sourceHead = sourceHead;
  run('bind-selected-plan', PLAN_ARGS, 30000);
  const plan = observe(ownedArtifactPath(root, GATE_ARGS[GATE_ARGS.indexOf('--plan') + 1]), { privateFile: true });
  const projection = observe(ownedArtifactPath(root, PROJECTION_PATH), { privateFile: true });
  const decodeJson = bytes => JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  const projectionData = decodeJson(projection.bytes);
  requireSelectedPlan({ plan: decodeJson(plan.bytes), projection: projectionData, root, sourceHead, planSha256: plan.observation.sha256 });
  generated.push(plan.observation, projection.observation);
  for (const item of projectionData.prerequisites) {
    const read = observe(ownedArtifactPath(root, item.receipt.path), { privateFile: true });
    assert.equal(read.observation.bytes, item.receipt.bytes); assert.equal(read.observation.sha256, item.receipt.sha256);
    generated.push(read.observation);
  }
  for (const [name, bytes] of [['selected.plan.json', plan.bytes], ['selected.projection.json', projection.bytes]]) {
    const copy = save(name, bytes), observed = observe(ownedArtifactPath(root, copy.path), { privateFile: true }).observation;
    assert.equal(observed.bytes, copy.bytes); assert.equal(observed.sha256, copy.sha256); retained.push(observed);
  }
  fence(); assert.equal(head(), sourceHead);
  const gateArgs = [...GATE_ARGS]; gateArgs[gateArgs.indexOf('--plan') + 1] = retained[0].route;
  run('execute-selected-gate', [cli.physical, ...gateArgs], 630000);
  assert.equal(head(), sourceHead);
  fence(); receipt.result = 'passed';
} catch (error) {
  receipt ??= { schemaVersion: 'bridge-windows-journal-ci-entry/v1', result: 'blocked-before-gate', checks, nodeTokenDirectlyObserved: false, packageAcceptance: false, fullBr06Accepted: false, nativeRuntimeQualified: false, releaseQualified: false };
  receipt.failure = { name: error?.name ?? 'Error', code: error?.code ?? null, message: String(error?.message ?? 'CI entry failed').slice(0, 4096) };
  process.exitCode = 1;
} finally {
  receipt.completedAt = new Date().toISOString();
  if (directory) {
    const file = ownedArtifactPath(root, `${relative(directory)}/entry.json`, 'file', { allowMissing: true });
    const bytes = Buffer.from(JSON.stringify(receipt, null, 2) + '\n'); writeFileSync(file, bytes, { flag: 'wx' });
    console.log(JSON.stringify({ result: receipt.result, receipt: { path: relative(file), bytes: bytes.length, sha256: sha(bytes) }, nodeTokenDirectlyObserved: false, releaseQualified: false }));
  } else console.error(JSON.stringify(receipt));
}
