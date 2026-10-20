import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import os from 'node:os';
import { randomUUID } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { parseArguments, selectQualification, fingerprintInputs, validateCampaign, controlInputs, qualificationInputs, qualificationPassed, requireStableSourceHead, QualificationBlocked } from './qualification.mjs';
import { registry } from './gate-registry.mjs';
import { readPrerequisiteReceipt, validatePrerequisites } from './prerequisites.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const actualHost = process.platform === 'win32' && process.arch === 'x64' ? 'windows-x64' : process.platform === 'darwin' && process.arch === 'arm64' ? 'macos-arm64-native' : `${process.platform}-${process.arch}`;
try {
  const options = parseArguments(process.argv.slice(2));
  const controlBefore = fingerprintInputs(root, controlInputs);
  const campaign = JSON.parse(readFileSync(path.join(root, 'docs/next/campaign.json'), 'utf8'));
  const work = JSON.parse(readFileSync(path.join(root, 'docs/plans/rust-tauri-cross-platform/work-packages.json'), 'utf8'));
  validateCampaign(campaign, work);
  const selected = selectQualification({ options, packages: work.packages, registry, campaign, actualHost, root, cwd: process.cwd() });
  const startedAt = new Date().toISOString();
  const inventory = qualificationInputs(selected.suites);
  const inputs = fingerprintInputs(root, inventory);
  if (controlBefore.sha256 !== fingerprintInputs(root, controlInputs).sha256) throw new QualificationBlocked('INPUTS_CHANGED', 'Qualification control inputs changed during selection.');
  const head = spawnSync('git', ['--no-optional-locks', 'rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true });
  if (head.status !== 0) throw new Error('Cannot bind qualification to a Git source head.');
  const prerequisiteContext = { selected, packages: work.packages, registry, campaign, actualHost, root, currentHead: head.stdout.trim(), readReceipt: id => readPrerequisiteReceipt({ root, packageId: id }) };
  const prerequisites = validatePrerequisites(prerequisiteContext);
  const operationId = randomUUID();
  const directory = path.join(root, 'artifacts/next', selected.package.id, operationId);
  mkdirSync(directory, { recursive: true });
  const checks = [];
  for (const suite of selected.suites) {
    const start = Date.now();
    const result = spawnSync(process.execPath, suite.argv, { cwd: root, encoding: 'utf8', windowsHide: true, timeout: suite.timeoutMs || 120000, maxBuffer: 4 * 1024 * 1024 });
    writeFileSync(path.join(directory, `${suite.id}.log`), `${result.stdout || ''}\n${result.stderr || ''}`);
    checks.push({ id: suite.id, executable: process.execPath, argv: suite.argv, cwd: root, durationMs: Date.now() - start, exitCode: result.status, error: result.error?.message || null, criteria: suite.criteria, boundary: suite.boundary, log: path.join(directory, `${suite.id}.log`) });
    if (result.status !== 0) break;
  }
  const after = fingerprintInputs(root, inventory);
  const headAfter = spawnSync('git', ['--no-optional-locks', 'rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true });
  requireStableSourceHead(head.stdout.trim(), headAfter.status === 0 ? headAfter.stdout.trim() : undefined);
  if (!isDeepStrictEqual(prerequisites, validatePrerequisites({ ...prerequisiteContext, currentHead: headAfter.stdout.trim() }))) throw new QualificationBlocked('PREREQUISITES_CHANGED', 'Prerequisite receipt references changed during execution.');
  const passed = qualificationPassed({ checks, suites: selected.suites, before: inputs, after });
  const receipt = { schemaVersion: 'bridge-qualification-receipt/v1', operationId, package: selected.package.id, issue: selected.binding.issue, owner: 'Bridge', startedAt, completedAt: new Date().toISOString(), host: { id: actualHost, osRelease: os.release(), processArchitecture: process.arch, node: process.version }, observationScope: selected.observationScope, source: { headSha: head.stdout.trim(), inputs }, inputsStable: inputs.sha256 === after.sha256, prerequisites, checks, result: passed ? 'passed' : 'failed', packageAcceptance: selected.packageAcceptance && passed, nativeRuntimeQualified: false, releaseQualified: false };
  const receiptPath = path.join(directory, 'acceptance.json');
  writeFileSync(receiptPath, JSON.stringify(receipt, null, 2) + '\n');
  const latest = selected.observationScope.kind === 'single-native-host-probe' ? `probe-${actualHost}.json` : 'acceptance.json';
  writeFileSync(path.join(root, 'artifacts/next', selected.package.id, latest), JSON.stringify(receipt, null, 2) + '\n');
  console.log(JSON.stringify({ package: selected.package.id, result: receipt.result, packageAcceptance: receipt.packageAcceptance, receipt: receiptPath, nativeRuntimeQualified: false, releaseQualified: false }));
  process.exitCode = passed ? 0 : 1;
} catch (error) {
  console.error(JSON.stringify({ result: 'blocked', code: error.code || 'QUALIFICATION_ERROR', message: error.message, nativeRuntimeQualified: false, releaseQualified: false }));
  process.exitCode = 2;
}
