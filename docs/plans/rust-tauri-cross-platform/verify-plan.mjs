// Validates planning artifacts only. It never executes implementation gates.
import { readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
import { registry } from '../../../scripts/next/gate-registry.mjs';

const directory = import.meta.dirname;
const root = path.resolve(directory, '../../..');
assert.equal(path.resolve(process.cwd()), root, 'Run from the owning Bridge checkout');
const runnerRoot = path.join(process.env.APPDATA, 'npm/node_modules/@smartergpt/lexrunner');
const cli = path.join(runnerRoot, 'dist/cli.js');
const runnerVersion = JSON.parse(readFileSync(path.join(runnerRoot, 'package.json'))).version;
const { ExecutionPlanV1Schema } = await import(pathToFileURL(path.join(runnerRoot, 'dist/package-schemas/execution-plan-v1.js')));
const load = name => JSON.parse(readFileSync(path.join(directory, name), 'utf8'));
const write = (name, value) => writeFileSync(path.join(directory, name), JSON.stringify(value, null, 2) + '\n');
const digest = name => createHash('sha256').update(readFileSync(path.join(directory, name))).digest('hex');
const work = load('work-packages.json');
const spec = load('feature-spec.json');
const execution = load('runner/execution-plan.json');
const projection = load('runner/plan.json');
const planning = load('runner/planning-state.json');
const checks = [];
function check(name, action) { action(); checks.push({ name, result: 'passed' }); }

check('Execution Plan v1 validates with installed LexRunner schema', () => ExecutionPlanV1Schema.parse(execution));
check('Expanded plan and Runner projection cover the same exact work packages', () => {
  assert.deepEqual(execution.sourceSpec, spec);
  assert.deepEqual(execution.subIssues.map(p => p.id), work.packages.map(p => p.id));
  assert.deepEqual(projection.items.map(p => p.name), work.packages.map(p => p.id));
  for (const [index, p] of work.packages.entries()) {
    assert.deepEqual(execution.subIssues[index].dependsOn, p.dependsOn);
    assert.deepEqual(projection.items[index].deps, p.dependsOn);
    assert.deepEqual(execution.subIssues[index].acceptanceCriteria, p.acceptanceCriteria);
    assert.equal(projection.items[index].gates[0].cwd, p.canonicalRoot);
    assert.equal(p.canonicalRoot, work.repositories[p.owner].root);
    assert.equal(projection.items[index].gates[0].env.BRIDGE_REQUIRED_SUITES, p.gates.map(g => g.id).join(','));
    assert.equal(projection.items[index].gates[0].env.BRIDGE_REQUIRED_HOST, p.host || 'any');
    assert.ok(projection.items[index].gates[0].run.includes(`-Package ${p.id}`) || projection.items[index].gates[0].run.includes(`--package ${p.id}`));
  }
});
check('All required native host coverage and unresolved bindings remain explicit', () => {
  assert.deepEqual(work.accepted.platforms, ['Windows x64', 'Apple Silicon macOS']);
  assert.ok(!work.packages.some(p => p.id === 'br-28'));
  for (const p of work.packages) {
    assert.ok(p.writeScope.length && p.gates.length && p.acceptanceCriteria.length);
    assert.ok(!p.requiredHosts.includes('macos-x86_64-native'));
    for (const g of p.gates) {
      if (g.state === 'planned-not-implemented') {
        assert.equal(g.availability, 'not-established');
      } else {
        const partialStates = {
          'implemented-awaiting-native-observation': 'not-established',
          'implemented-source-observation-only': 'single-host-source-observation-only'
        };
        assert.ok(Object.hasOwn(partialStates, g.state));
        assert.equal(g.availability, partialStates[g.state]);
        assert.equal(registry[g.id]?.packageAcceptanceAvailable, false,
          `${g.id}: partial implementation must not grant package acceptance`);
        assert.equal(g.receiptRequired, true);
      }
    }
    for (const g of p.gates.filter(g => g.requiredHost.startsWith('macos') || g.requiredHost === 'native-target-matrix')) {
      assert.equal(g.cwdResolution, 'required-on-matching-native-host-before-execution');
    }
  }
  assert.deepEqual(work.packages.find(p => p.id === 'br-05').requiredHosts, ['windows-x64', 'macos-arm64-native']);
});
check('UI autonomy and shared write ownership are enforced by the plan', () => {
  const byId = new Map(work.packages.map(p => [p.id, p]));
  function ancestors(id, seen = new Set()) { for (const dep of byId.get(id).dependsOn) { if (!seen.has(dep)) { seen.add(dep); ancestors(dep, seen); } } return seen; }
  for (const id of ['br-18', 'br-19', 'br-20', 'br-25']) {
    assert.ok(![...ancestors(id)].some(dep => ['br-04', 'br-05', 'br-07', 'br-08', 'br-09', 'br-10', 'br-11', 'br-17'].includes(dep)));
  }
  assert.ok(byId.get('br-15').dependsOn.includes('br-14'));
  assert.ok(byId.get('br-21').dependsOn.includes('br-03'));
  assert.ok(byId.get('br-26').dependsOn.includes('br-25'));
  assert.ok(byId.get('br-10').sharedResources.includes('profiles-native-composition'));
  assert.ok(byId.get('br-11').sharedResources.includes('profiles-native-composition'));
  assert.ok(byId.get('br-12').sharedResources.includes('frontend-composition'));
  assert.ok(byId.get('br-26').sharedResources.includes('frontend-composition'));
  assert.deepEqual(byId.get('br-30').dependsOn, ['br-27', 'br-29']);
});
check('Refined lessons retain the existing package graph and authority boundaries', () => {
  const refinement = work.implementationRefinement;
  assert.equal(work.packages.length, 30);
  assert.equal(work.packages.reduce((sum, p) => sum + p.dependsOn.length, 0), 68);
  assert.equal(Object.keys(refinement.packageMapping).length, 5);
  for (const ids of Object.values(refinement.packageMapping)) {
    assert.ok(ids.every(id => work.packages.some(p => p.id === id)));
    assert.ok(!ids.some(id => ['br-00', 'br-01', 'br-02', 'br-03', 'br-04', 'br-12', 'br-13'].includes(id)));
  }
  assert.ok(work.accepted.namedProfileLaunch.includes('current OS user'));
  assert.deepEqual(execution.epic.acceptanceCriteria, spec.acceptanceCriteria);
});
check('Planning validation cannot claim implementation or execution acceptance', () => {
  assert.equal(work.disposition, 'planning-only-not-execution-authority');
  assert.equal(planning.implementationTasksCompleted, 0);
  assert.equal(planning.implementationGatesPassed, 0);
  assert.equal(planning.runsCreated, 0);
  assert.equal(planning.attemptsCreated, 0);
  assert.equal(planning.workPackageSha256, digest('work-packages.json'));
  assert.equal(work.coordination.databasePath, 'D:/dev/stfc-workspace/.smartergpt/runner/coordination.db');
  assert.equal(work.coordination.destructiveMutationsEnabled, false);
  assert.equal(work.coordination.automaticLexFrameEmission, false);
});

const commands = [];
function runner(args) {
  const argv = ['--no-emit-frames', ...args];
  const start = Date.now();
  const result = spawnSync(process.execPath, [cli, ...argv], {
    cwd: root, encoding: 'utf8', windowsHide: true, timeout: 60000, maxBuffer: 2 * 1024 * 1024,
    env: { ...process.env, ALLOW_MUTATIONS: 'false', LEX_PR_EMIT_FRAMES: 'false' }
  });
  const record = { executable: process.execPath, cli, argv, cwd: root, exitCode: result.status, durationMs: Date.now() - start, stdout: result.stdout, stderr: result.stderr };
  commands.push(record);
  assert.equal(result.status, 0, `${args.join(' ')} failed: ${result.stderr || result.stdout || result.error}`);
  return JSON.parse(result.stdout);
}
const relative = 'docs/plans/rust-tauri-cross-platform/runner/plan.json';
const schema = runner(['schema', 'validate', relative, '--json']);
assert.equal(schema.valid, true);
const order = runner(['weave', 'merge-order', relative, '--json']);
// This is LexRunner's input-size allowance, not an agent token budget or an execution bypass.
const dryRun = runner(['--token-budget', '6000', 'gate', 'run', relative, '--dry-run', '--keep-cache', '--json']);
assert.equal(dryRun.dryRun, true);
assert.equal(dryRun.plan.itemCount, work.packages.length);
assert.deepEqual(dryRun.execution.levels.map(level => level.items), order.levels);
write('runner/dependency-order.json', order);
write('runner/dry-run.json', dryRun);
write('runner/verification.json', {
  disposition: 'planning-structure-validated-only', verifiedAt: new Date().toISOString(), runnerVersion,
  nodeVersion: process.version, bridgeRoot: root, baseline: work.repositories,
  packageCount: work.packages.length, dependencyEdges: work.packages.reduce((sum, p) => sum + p.dependsOn.length, 0), dependencyLevels: order.levels.length,
  artifactSha256: Object.fromEntries(['feature-spec.json', 'work-packages.json', 'runner/execution-plan.json', 'runner/plan.json', 'runner/planning-state.json'].map(name => [name, digest(name)])),
  controlInputSha256: {
    'docs/plans/rust-tauri-cross-platform/verify-plan.mjs': digest('verify-plan.mjs'),
    'scripts/next/gate-registry.mjs': createHash('sha256').update(readFileSync(path.join(root, 'scripts/next/gate-registry.mjs'))).digest('hex')
  },
  checks, commands, implementationGatesExecuted: 0, runsCreated: 0, attemptsCreated: 0,
  proofBoundary: 'Schema, cross-artifact coverage and dependency order are validated. Gate declarations distinguish initial plans from explicitly partial component implementations; zero-execution counters describe planning validation and acceptance requires separately bound receipts. No product implementation, native build, runtime qualification or release eligibility follows from this planning result.',
  priorProbe: { command: 'lexrunner --no-emit-frames gate run <plan> --dry-run --keep-cache --json', result: 'input-size-budget-exceeded', tokensEstimated: 5392, defaultLimit: 5000, resolution: 'Explicit 6000-token plan-input allowance; no gates run.' }
});
console.log(JSON.stringify({ runnerVersion, packages: work.packages.length, edges: schema.diagnostics.edges, levels: order.levels.length, planningChecks: checks.length, implementationGatesExecuted: 0 }));
