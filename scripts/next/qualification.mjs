import assert from 'node:assert/strict';
import { realpathSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fingerprintInputRecords, InputInventoryBlocked } from './input-tree.mjs';

// These files control selection and execution, independent of a suite's own inventory.
export const controlInputs = Object.freeze([
  'docs/next/campaign.json',
  'docs/plans/rust-tauri-cross-platform/work-packages.json',
  'scripts/next/qualification.mjs',
  'scripts/next/input-tree.mjs',
  'scripts/next/qualify.mjs',
  'scripts/next/prerequisites.mjs',
  'scripts/next/gate-registry.mjs'
]);

export class QualificationBlocked extends Error {
  constructor(code, message) { super(message); this.name = 'QualificationBlocked'; this.code = code; }
}
export function parseArguments(argv) {
  const result = {};
  const allowed = new Set(['package', 'suite', 'host']);
  for (let i = 0; i < argv.length; i += 2) {
    const flag = argv[i];
    const key = flag?.startsWith('--') ? flag.slice(2) : '';
    const value = argv[i + 1];
    if (!allowed.has(key) || !value || value.startsWith('--') || Object.hasOwn(result, key)) {
      throw new QualificationBlocked('INVALID_ARGUMENTS', 'Use exactly one --package or --suite and one --host; unknown or duplicate arguments are rejected.');
    }
    result[key] = value;
  }
  if (Boolean(result.package) === Boolean(result.suite) || !result.host) {
    throw new QualificationBlocked('INVALID_ARGUMENTS', 'Specify --package or --suite, plus --host.');
  }
  return result;
}
export function selectQualification({ options, packages, registry, campaign, actualHost, root, cwd }) {
  if (realpathSync(root) !== realpathSync(cwd)) throw new QualificationBlocked('WRONG_CWD', 'Run from the canonical owning checkout.');
  const p = options.package ? packages.find(p => p.id === options.package) : packages.find(p => Array.isArray(p.gates) && p.gates.some(g => g?.id === options.suite));
  if (!p) throw new QualificationBlocked('UNKNOWN_PACKAGE_OR_SUITE', 'Requested package or suite is absent from the accepted graph.');
  if (p.owner !== 'Bridge') throw new QualificationBlocked('WRONG_OWNER', `Run package ${p.id} from its ${p.owner} repository.`);
  const binding = campaign.packages[p.id];
  if (binding?.owner !== p.owner || typeof binding.issue !== 'string' || !/^https:\/\/github\.com\/Guffawaffle\/stfc-mod-bridge\/issues\/[1-9][0-9]*$/.test(binding.issue)) {
    throw new QualificationBlocked('UNBOUND_WORK_PACKAGE', 'Bind a real owning-repository issue before qualification.');
  }
  const host = p.host || 'any';
  // A matrix suite may collect one explicitly named native host's observation.
  // It cannot establish package acceptance or satisfy a matrix prerequisite.
  const nativeHosts = ['windows-x64', 'macos-arm64-native'];
  const hostProbe = Boolean(options.suite) && host === 'native-target-matrix' && nativeHosts.includes(options.host);
  if (!hostProbe && options.host !== host) throw new QualificationBlocked('WRONG_REQUESTED_HOST', `Package requires ${host}; received ${options.host}.`);
  if (hostProbe) {
    if (options.host !== actualHost) throw new QualificationBlocked('WRONG_NATIVE_HOST', `Actual host ${actualHost} cannot collect ${options.host}.`);
    if (!Array.isArray(p.requiredHosts) || p.requiredHosts.length !== nativeHosts.length || new Set(p.requiredHosts).size !== nativeHosts.length || !nativeHosts.every(id => p.requiredHosts.includes(id))) {
      throw new QualificationBlocked('INVALID_HOST_MATRIX', 'A local probe requires the complete declared two-host native matrix.');
    }
  } else if (host !== 'any' && host !== actualHost) throw new QualificationBlocked('WRONG_NATIVE_HOST', `Actual host ${actualHost} cannot qualify ${host}. A matrix requires verified per-host aggregation.`);
  if (!Array.isArray(p.gates) || p.gates.length === 0 || p.gates.some(g => typeof g?.id !== 'string' || !/^[a-z][a-z0-9-]{0,63}$/.test(g.id)) || new Set(p.gates.map(g => g.id)).size !== p.gates.length) {
    throw new QualificationBlocked('INVALID_SUITE_INVENTORY', 'A package must declare a nonempty unique inventory of valid suite IDs.');
  }
  const suiteIds = options.package ? p.gates.map(g => g.id) : [options.suite];
  const suites = suiteIds.map(id => {
    const definition = registry[id];
    if (!definition?.argv?.length || !definition.inputs?.length) throw new QualificationBlocked('UNIMPLEMENTED_SUITE', `Suite ${id} has no executable definition and input inventory.`);
    if (options.package && definition.packageAcceptanceAvailable === false) throw new QualificationBlocked('PACKAGE_INTEGRATION_UNQUALIFIED', `Suite ${id} currently supplies source observations only; required native integration remains unqualified.`);
    if (hostProbe && (!Array.isArray(definition.nativeProbeHosts) || definition.nativeProbeHosts.length !== nativeHosts.length || new Set(definition.nativeProbeHosts).size !== nativeHosts.length || !nativeHosts.every(id => definition.nativeProbeHosts.includes(id)))) {
      throw new QualificationBlocked('UNIMPLEMENTED_HOST_PROBE', `Suite ${id} does not explicitly implement independent probes for both native hosts.`);
    }
    if (definition.host !== 'any' && definition.host !== actualHost) throw new QualificationBlocked('WRONG_NATIVE_HOST', `Suite ${id} needs ${definition.host}.`);
    return { id, ...definition };
  });
  return { package: p, binding, suites, packageAcceptance: Boolean(options.package),
    observationScope: hostProbe ? { kind: 'single-native-host-probe', host: actualHost, requiredHosts: [...p.requiredHosts], matrixAcceptance: false } : { kind: options.package ? 'local-package-gates' : 'local-suite-gates', host: actualHost } };
}
export function qualificationInputs(suites) {
  return [...new Set([...controlInputs, ...suites.flatMap(s => s.inputs)])].sort();
}
export function qualificationPassed({ checks, suites, before, after }) {
  return suites.length > 0 && checks.length > 0 && checks.length === suites.length &&
    checks.every((c, index) => c.id === suites[index].id && c.exitCode === 0 && !c.error) &&
    before.sha256 === after.sha256;
}
export function requireStableSourceHead(before, after) {
  if (typeof before !== 'string' || typeof after !== 'string' || !/^[a-f0-9]{40}$/.test(before) || !/^[a-f0-9]{40}$/.test(after)) {
    throw new QualificationBlocked('SOURCE_HEAD_UNAVAILABLE', 'Qualification requires exact Git head observations before and after execution.');
  }
  if (before !== after) throw new QualificationBlocked('SOURCE_HEAD_CHANGED', 'Git head changed during qualification; retry on the current source.');
}
export function fingerprintInputs(root, inputs) {
  let files;
  try { files = fingerprintInputRecords(root, inputs); }
  catch (error) {
    if (error instanceof InputInventoryBlocked) throw new QualificationBlocked(error.code, error.message);
    throw error;
  }
  return { files, sha256: createHash('sha256').update(JSON.stringify(files)).digest('hex') };
}
export function validateCampaign(campaign, work) {
  assert.equal(campaign.schemaVersion, 'bridge-campaign/v1');
  assert.deepEqual(campaign.platforms, ['windows-x64', 'macos-arm64-native']);
  assert.equal(campaign.mutationPolicy.runnerDestructiveMutations, false);
  assert.equal(campaign.mutationPolicy.automaticLexFrameEmission, false);
  assert.equal(campaign.coordinationDatabase, work.coordination.databasePath);
  assert.equal(campaign.packages['br-00'].owner, 'Bridge');
}
