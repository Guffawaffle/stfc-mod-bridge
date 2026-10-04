import { closeSync, fstatSync, openSync, readSync, realpathSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import os from 'node:os';
import path from 'node:path';
import { QualificationBlocked, fingerprintInputs, qualificationInputs, selectQualification } from './qualification.mjs';

export const MAX_RECEIPT_BYTES = 1024 * 1024;
const packagePattern = /^[a-z][a-z0-9-]{0,63}$/;
const digestPattern = /^[a-f0-9]{64}$/;
const receiptPath = id => `artifacts/next/${id}/acceptance.json`;
const block = (code, message) => { throw new QualificationBlocked(code, message); };
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
function confined(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative);
}

// This reader accepts a package ID, never a receipt-supplied path. It observes
// local bytes only: it does not authenticate a signer, review, Run or release.
export function readPrerequisiteReceipt({ root, packageId }) {
  if (typeof packageId !== 'string' || !packagePattern.test(packageId)) block('PREREQUISITE_CONTEXT_INVALID', 'A safe package ID is required.');
  let descriptor;
  try {
    const physicalRoot = realpathSync(root);
    const requested = path.resolve(root, receiptPath(packageId));
    const physical = realpathSync(requested);
    if (!confined(physicalRoot, physical)) block('PREREQUISITE_ESCAPE', `Receipt for ${packageId} escapes the owning checkout.`);
    descriptor = openSync(physical, 'r');
    const before = fstatSync(descriptor);
    if (!before.isFile()) block('PREREQUISITE_RECEIPT_INVALID', `Receipt for ${packageId} is not a regular file.`);
    if (before.size <= 0 || before.size > MAX_RECEIPT_BYTES) block('PREREQUISITE_TOO_LARGE', `Receipt for ${packageId} exceeds the supported nonempty byte bound.`);
    const buffer = Buffer.alloc(MAX_RECEIPT_BYTES + 1);
    let length = 0;
    while (length < buffer.length) {
      const bytes = readSync(descriptor, buffer, length, buffer.length - length, null);
      if (bytes === 0) break;
      length += bytes;
    }
    if (length === 0 || length > MAX_RECEIPT_BYTES) block('PREREQUISITE_TOO_LARGE', `Receipt for ${packageId} exceeds the supported nonempty byte bound.`);
    const after = fstatSync(descriptor);
    if (before.dev !== after.dev || before.ino !== after.ino || before.size !== after.size || before.mtimeMs !== after.mtimeMs || length !== after.size || realpathSync(requested) !== physical) {
      block('PREREQUISITE_INPUTS_CHANGED', `Receipt for ${packageId} changed while being read.`);
    }
    const bytes = buffer.subarray(0, length);
    let receipt;
    try { receipt = JSON.parse(bytes.toString('utf8')); }
    catch { block('PREREQUISITE_RECEIPT_INVALID', `Receipt for ${packageId} is not valid JSON.`); }
    return { receipt, reference: { path: receiptPath(packageId), sha256: createHash('sha256').update(bytes).digest('hex'), bytes: length } };
  } catch (error) {
    if (error instanceof QualificationBlocked) throw error;
    if (error.code === 'ENOENT' || error.code === 'ENOTDIR') block('PREREQUISITE_MISSING', `Receipt for ${packageId} is missing.`);
    block('PREREQUISITE_READ_FAILED', `Receipt for ${packageId} could not be read.`);
  } finally {
    if (descriptor !== undefined) closeSync(descriptor);
  }
}

// readReceipt(id) returns { receipt, reference } from the bounded reader above.
// Dependencies are validated in postorder; references are deduplicated, bounded
// observations and confer no review, Run, native or release authority.
export function validatePrerequisites({ selected, packages, registry, campaign, actualHost, root, currentHead, readReceipt }) {
  if (!record(selected?.package) || !Array.isArray(packages) || packages.length > 512 || typeof readReceipt !== 'function' || typeof currentHead !== 'string' || !/^[a-f0-9]{40}$/.test(currentHead) || typeof actualHost !== 'string' || !/^[a-z0-9-]{1,64}$/.test(actualHost)) {
    block('PREREQUISITE_CONTEXT_INVALID', 'Prerequisite validation requires a selected package, bounded graph, exact source head, host and reader.');
  }
  const byId = new Map();
  for (const item of packages) {
    if (!record(item) || typeof item.id !== 'string' || !packagePattern.test(item.id) || byId.has(item.id) || !Array.isArray(item.dependsOn) || item.dependsOn.length > 512 || item.dependsOn.some(id => typeof id !== 'string' || !packagePattern.test(id)) || new Set(item.dependsOn).size !== item.dependsOn.length) {
      block('PREREQUISITE_GRAPH_INVALID', 'The prerequisite graph has invalid or duplicate identities/dependencies.');
    }
    byId.set(item.id, item);
  }
  const selectedPackage = byId.get(selected.package.id);
  if (!selectedPackage || !isDeepStrictEqual(selectedPackage, selected.package)) block('PREREQUISITE_CONTEXT_INVALID', 'Selected work must match the current authoritative graph.');
  const physicalRoot = realpathSync(root);
  const active = new Set();
  const done = new Set();
  const references = [];
  const inventories = [];

  function validateReceipt(item) {
    if (item.owner !== 'Bridge') block('PREREQUISITE_OWNER_UNIMPLEMENTED', `Cross-repository prerequisite ${item.id} needs an explicit owner/evidence handoff implementation.`);
    if (['native-target-matrix', 'evidence-aggregation'].includes(item.host)) block('PREREQUISITE_HOST_AGGREGATION_UNIMPLEMENTED', `Prerequisite ${item.id} needs implemented independent host aggregation.`);
    const binding = campaign?.packages?.[item.id];
    if (binding?.owner !== 'Bridge' || typeof binding.issue !== 'string' || binding.issue.length > 200 || !/^https:\/\/github\.com\/Guffawaffle\/stfc-mod-bridge\/issues\/[1-9][0-9]*$/.test(binding.issue)) {
      block('PREREQUISITE_ISSUE_UNBOUND', `Prerequisite ${item.id} needs a real owning-repository issue binding.`);
    }
    const expected = selectQualification({ options: { package: item.id, host: item.host || 'any' }, packages, registry, campaign, actualHost, root, cwd: root });
    const inventory = qualificationInputs(expected.suites);
    const currentInputs = fingerprintInputs(root, inventory);
    inventories.push({ inventory, fingerprint: currentInputs.sha256 });
    let loaded;
    try { loaded = readReceipt(item.id); }
    catch (error) {
      if (error instanceof QualificationBlocked) throw error;
      block('PREREQUISITE_READ_FAILED', `Receipt for ${item.id} could not be read.`);
    }
    if (!loaded) block('PREREQUISITE_MISSING', `Receipt for ${item.id} is missing.`);
    const receipt = loaded.receipt;
    const reference = loaded.reference;
    if (!record(reference) || reference.path !== receiptPath(item.id) || typeof reference.sha256 !== 'string' || !digestPattern.test(reference.sha256) || !Number.isInteger(reference.bytes) || reference.bytes <= 0 || reference.bytes > MAX_RECEIPT_BYTES) {
      block('PREREQUISITE_REFERENCE_INVALID', `Receipt reference for ${item.id} is not confined and bounded.`);
    }
    let serialized;
    try { serialized = JSON.stringify(receipt); }
    catch { block('PREREQUISITE_RECEIPT_INVALID', `Receipt for ${item.id} is not a supported JSON object.`); }
    if (!record(receipt) || !serialized || Buffer.byteLength(serialized) > MAX_RECEIPT_BYTES || receipt.schemaVersion !== 'bridge-qualification-receipt/v1' || receipt.result !== 'passed' || receipt.packageAcceptance !== true || receipt.inputsStable !== true || receipt.package !== item.id || receipt.owner !== 'Bridge' || receipt.issue !== binding.issue || receipt.nativeRuntimeQualified !== false || receipt.releaseQualified !== false) {
      block('PREREQUISITE_RECEIPT_INVALID', `Receipt for ${item.id} does not establish complete local package acceptance.`);
    }
    if (receipt.source?.headSha !== currentHead) block('PREREQUISITE_STALE_SOURCE', `Prerequisite ${item.id} belongs to a different source head.`);
    if (receipt.host?.id !== actualHost || receipt.host?.processArchitecture !== process.arch || receipt.host?.node !== process.version || receipt.host?.osRelease !== os.release()) {
      block('PREREQUISITE_WRONG_HOST', `Prerequisite ${item.id} belongs to a different host/toolchain context.`);
    }
    if (!Array.isArray(receipt.checks) || receipt.checks.length === 0 || receipt.checks.length !== expected.suites.length) block('PREREQUISITE_CHECKS_INCOMPLETE', `Prerequisite ${item.id} lacks every required suite result.`);
    for (const [index, suite] of expected.suites.entries()) {
      const check = receipt.checks[index];
      if (!record(check) || check.id !== suite.id || !isDeepStrictEqual(check.argv, suite.argv) || !isDeepStrictEqual(check.criteria, suite.criteria) || check.boundary !== suite.boundary) {
        block('PREREQUISITE_CHECKS_INCOMPLETE', `Prerequisite ${item.id} has a different suite/command inventory.`);
      }
      if (check.exitCode !== 0 || (check.error !== null && check.error !== undefined) || !Number.isFinite(check.durationMs) || check.durationMs < 0) block('PREREQUISITE_CHECK_FAILED', `Prerequisite ${item.id} has an incomplete or failed execution.`);
      let sameContext = false;
      try { sameContext = realpathSync(check.cwd) === physicalRoot && realpathSync(check.executable) === realpathSync(process.execPath); }
      catch { /* Unresolvable command/cwd is not evidence. */ }
      if (!sameContext) block('PREREQUISITE_CHECKS_INCOMPLETE', `Prerequisite ${item.id} used a different executable or owning cwd.`);
    }
    if (!isDeepStrictEqual(receipt.source?.inputs, currentInputs)) block('PREREQUISITE_INPUTS_STALE', `Prerequisite ${item.id} does not match the full current input inventory.`);
    references.push({ package: item.id, owner: 'Bridge', issue: binding.issue, sourceHead: currentHead, host: actualHost, inputSha256: currentInputs.sha256, receipt: { path: reference.path, sha256: reference.sha256, bytes: reference.bytes }, proofBoundary: 'local-package-gate-observation-only', nativeRuntimeQualified: false, releaseQualified: false });
  }

  function visit(id, requireReceipt) {
    if (active.has(id)) block('PREREQUISITE_CYCLE', 'The prerequisite graph contains a dependency cycle.');
    if (done.has(id)) return;
    const item = byId.get(id);
    if (!item) block('PREREQUISITE_UNKNOWN', `Prerequisite ${id} is absent from the current graph.`);
    active.add(id);
    for (const dependency of item.dependsOn) visit(dependency, true);
    if (requireReceipt) validateReceipt(item);
    active.delete(id);
    done.add(id);
  }
  visit(selectedPackage.id, false);
  for (const item of inventories) {
    if (fingerprintInputs(root, item.inventory).sha256 !== item.fingerprint) block('PREREQUISITE_INPUTS_CHANGED', 'Prerequisite inputs changed during validation.');
  }
  return references;
}
