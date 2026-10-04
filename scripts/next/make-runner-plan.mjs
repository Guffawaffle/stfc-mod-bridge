import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { parseArguments, selectQualification, validateCampaign } from './qualification.mjs';
import { registry } from './gate-registry.mjs';
import { validatePrerequisites, readPrerequisiteReceipt } from './prerequisites.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const arguments_ = process.argv.slice(2);
const options = arguments_.length === 2 && arguments_[0] === '--package' ? { package: arguments_[1] } : parseArguments(arguments_);
const campaign = JSON.parse(readFileSync(path.join(root, 'docs/next/campaign.json')));
const work = JSON.parse(readFileSync(path.join(root, 'docs/plans/rust-tauri-cross-platform/work-packages.json')));
validateCampaign(campaign, work);
const actualHost = process.platform === 'win32' && process.arch === 'x64' ? 'windows-x64' : process.platform === 'darwin' && process.arch === 'arm64' ? 'macos-arm64-native' : `${process.platform}-${process.arch}`;
const p = options.package ? work.packages.find(p => p.id === options.package) : work.packages.find(p => p.gates.some(g => g.id === options.suite));
options.host ??= p?.host || 'any';
const selected = selectQualification({ options, packages: work.packages, campaign, registry, actualHost, root, cwd: process.cwd() });
const packageId = p.id;
const projectionId = options.suite ? `${packageId}.${options.suite}.${options.host}` : packageId;
const selector = options.suite ? `--suite ${options.suite}` : `--package ${packageId}`;
const latest = selected.observationScope.kind === 'single-native-host-probe' ? `probe-${actualHost}.json` : 'acceptance.json';
const head = spawnSync('git', ['--no-optional-locks', 'rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true });
if (head.status !== 0) throw new Error('Cannot bind prerequisite projection to source head.');
const prerequisites = validatePrerequisites({ selected, packages: work.packages, registry, campaign, actualHost, root, currentHead: head.stdout.trim(), readReceipt: id => readPrerequisiteReceipt({ root, packageId: id }) });
const plan = {
  schemaVersion: '1.0.0', target: 'main',
  policy: { requiredGates: ['acceptance'], optionalGates: [], maxWorkers: 1, retries: {}, overrides: {}, mergeRule: { type: 'strict-required' } },
  // One real package per gate invocation. Dependencies stay authoritative in
  // work-packages.json and are validated by the dispatcher before/after work.
  items: [{ name: projectionId, deps: [], gates: [{ name: 'acceptance', run: `node scripts/next/qualify.mjs ${selector} --host ${options.host}`, cwd: root, runtime: 'local', timeoutMs: selected.suites.reduce((sum, s) => sum + (s.timeoutMs || 120000), 30000), env: { BRIDGE_WORK_PACKAGE: p.id, BRIDGE_ISSUE: selected.binding.issue.split('/').at(-1) }, artifacts: [`artifacts/next/${p.id}/${latest}`] }] }]
};
const directory = path.join(root, 'artifacts/next/plans');
mkdirSync(directory, { recursive: true });
const file = path.join(directory, `${projectionId}.plan.json`);
writeFileSync(file, JSON.stringify(plan, null, 2) + '\n');
writeFileSync(path.join(directory, `${projectionId}.projection.json`), JSON.stringify({ schemaVersion: 'bridge-gate-projection/v1', package: p.id, observationScope: selected.observationScope, dependsOn: p.dependsOn, prerequisites, sourceHead: head.stdout.trim(), planSha256: createHash('sha256').update(readFileSync(file)).digest('hex'), boundary: 'single-package-command-gate-projection-not-a-merge-plan' }, null, 2) + '\n');
console.log(JSON.stringify({ plan: file, package: p.id, dependsOn: p.dependsOn, host: actualHost, authority: 'integration-gates-only' }));
