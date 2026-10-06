import { createHash } from 'node:crypto';
import { closeSync, constants, fstatSync, lstatSync, openSync, opendirSync, readSync, realpathSync } from 'node:fs';
import path from 'node:path';

export const MAX_INPUT_DEPTH = 32;
export const MAX_INPUT_RECORDS = 4096;

export class InputInventoryBlocked extends Error {
  constructor(code, message) {
    super(message);
    this.name = 'InputInventoryBlocked';
    this.code = code;
  }
}

const messages = {
  INPUT_ROOT_INVALID: 'Input inventory requires an absolute directory root.',
  INPUT_DESCRIPTOR_INVALID: 'Input descriptors must be nonempty relative path strings.',
  INPUT_ROOT_SELECTION: 'Select individual source files or directories, not the repository root.',
  INPUT_ESCAPE: 'An input escaped the owning directory root.',
  INPUT_LINK: 'Input trees may not contain symbolic links or junctions.',
  INPUT_NON_REGULAR: 'Inputs must be regular files or directories.',
  INPUT_UNAVAILABLE: 'An input could not be inspected or read.',
  INPUT_CHANGED: 'An input changed while its inventory was observed.',
  INPUT_DEPTH_LIMIT: 'The input tree exceeded the traversal depth limit.',
  INPUT_RECORD_LIMIT: 'The input tree exceeded the record limit.'
};
const block = code => { throw new InputInventoryBlocked(code, messages[code]); };
const io = action => {
  try { return action(); }
  catch (error) {
    if (error instanceof InputInventoryBlocked) throw error;
    block(error?.code === 'ELOOP' ? 'INPUT_LINK' : 'INPUT_UNAVAILABLE');
  }
};
const outside = relative => relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative);
const portable = relative => relative.split(path.sep).join('/');
const sha256 = value => createHash('sha256').update(value).digest('hex');
const sameIdentity = (first, second) => first.dev === second.dev && first.ino === second.ino && first.mode === second.mode;
const unchanged = (first, second) => sameIdentity(first, second) && first.size === second.size && first.mtimeNs === second.mtimeNs && first.ctimeNs === second.ctimeNs;

function descriptor(value) {
  if (typeof value !== 'string' || !value || value.includes('\0') || value.length > 4096) block('INPUT_DESCRIPTOR_INVALID');
  if (path.isAbsolute(value) || path.win32.parse(value).root) block('INPUT_ESCAPE');
  const pieces = value.replaceAll('\\', '/').split('/');
  if (pieces.includes('..')) block('INPUT_ESCAPE');
  if (pieces.some(piece => piece.includes(':'))) block('INPUT_DESCRIPTOR_INVALID');
  const selected = pieces.filter(piece => piece && piece !== '.');
  if (!selected.length) block('INPUT_ROOT_SELECTION');
  if (selected.length > MAX_INPUT_DEPTH) block('INPUT_DEPTH_LIMIT');
  return selected.join(path.sep);
}

/**
 * Observe only explicitly selected files/trees, afresh on every call.
 * Directory records use a trailing slash and hash immediate name/type membership,
 * including empty directories. This is a disk observation, not a retained lock.
 */
export function fingerprintInputRecords(root, inputs) {
  if (typeof root !== 'string' || !path.isAbsolute(root) || root.includes('\0')) block('INPUT_ROOT_INVALID');
  if (!Array.isArray(inputs)) block('INPUT_DESCRIPTOR_INVALID');
  const absoluteRoot = path.resolve(root);
  const rootStat = io(() => lstatSync(absoluteRoot, { bigint: true }));
  if (rootStat.isSymbolicLink()) block('INPUT_LINK');
  if (!rootStat.isDirectory()) block('INPUT_ROOT_INVALID');
  const physicalRoot = io(() => realpathSync.native(absoluteRoot));
  const records = new Map();
  const visited = new Set();

  function inspect(requested) {
    const lexical = path.relative(physicalRoot, requested);
    if (outside(lexical)) block('INPUT_ESCAPE');
    const stat = io(() => lstatSync(requested, { bigint: true }));
    if (stat.isSymbolicLink()) block('INPUT_LINK');
    if (!stat.isFile() && !stat.isDirectory()) block('INPUT_NON_REGULAR');
    const physical = io(() => realpathSync.native(requested));
    const relative = path.relative(physicalRoot, physical);
    if (outside(relative)) block('INPUT_ESCAPE');
    return { physical, relative: portable(relative), stat };
  }

  function inspectPath(requested) {
    const relative = path.relative(physicalRoot, requested);
    if (outside(relative)) block('INPUT_ESCAPE');
    const pieces = relative.split(path.sep).filter(Boolean);
    if (pieces.length > MAX_INPUT_DEPTH) block('INPUT_DEPTH_LIMIT');
    let current = physicalRoot;
    for (let i = 0; i < pieces.length; i++) {
      current = path.join(current, pieces[i]);
      const entry = inspect(current);
      if (i < pieces.length - 1 && !entry.stat.isDirectory()) block('INPUT_NON_REGULAR');
    }
    return inspect(requested);
  }

  function names(directory) {
    return io(() => {
      const handle = opendirSync(directory);
      try {
        const result = [];
        let entry;
        while ((entry = handle.readSync()) !== null) {
          result.push(entry.name);
          if (result.length > MAX_INPUT_RECORDS) block('INPUT_RECORD_LIMIT');
        }
        return result.sort();
      } finally { handle.closeSync(); }
    });
  }

  function assertUnchanged(entry) {
    const after = inspectPath(entry.physical);
    if (after.physical !== entry.physical || !unchanged(entry.stat, after.stat)) block('INPUT_CHANGED');
  }

  function fileDigest(entry) {
    return io(() => {
      const flags = constants.O_RDONLY | (constants.O_NOFOLLOW || 0) | (constants.O_NONBLOCK || 0);
      const fd = openSync(entry.physical, flags);
      try {
        const before = fstatSync(fd, { bigint: true });
        if (!before.isFile() || !unchanged(entry.stat, before)) block('INPUT_CHANGED');
        assertUnchanged(entry);
        if (before.size > BigInt(Number.MAX_SAFE_INTEGER)) block('INPUT_UNAVAILABLE');
        const hash = createHash('sha256');
        const buffer = Buffer.allocUnsafe(64 * 1024);
        let position = 0;
        const size = Number(before.size);
        while (position < size) {
          const count = readSync(fd, buffer, 0, Math.min(buffer.length, size - position), position);
          if (!count) block('INPUT_CHANGED');
          hash.update(buffer.subarray(0, count));
          position += count;
        }
        if (!unchanged(before, fstatSync(fd, { bigint: true }))) block('INPUT_CHANGED');
        assertUnchanged(entry);
        return hash.digest('hex');
      } finally { closeSync(fd); }
    });
  }

  function observe(requested) {
    const entry = inspectPath(requested);
    if (!entry.relative) block('INPUT_ROOT_SELECTION');
    const key = entry.stat.isDirectory() ? `${entry.relative}/` : entry.relative;
    if (visited.has(key)) return entry.stat.isDirectory() ? 'directory' : 'file';
    if (visited.size >= MAX_INPUT_RECORDS) block('INPUT_RECORD_LIMIT');
    visited.add(key);
    if (entry.stat.isFile()) {
      records.set(key, { path: key, sha256: fileDigest(entry) });
    } else {
      const children = names(entry.physical);
      assertUnchanged(entry);
      const membership = children.map(name => ({ name, kind: observe(path.join(entry.physical, name)) }));
      if (JSON.stringify(children) !== JSON.stringify(names(entry.physical))) block('INPUT_CHANGED');
      assertUnchanged(entry);
      records.set(key, { path: key, sha256: sha256(`bridge-input-directory/v1\n${JSON.stringify(membership)}`) });
    }
    return entry.stat.isDirectory() ? 'directory' : 'file';
  }

  for (const selected of [...new Set(inputs.map(descriptor))].sort()) observe(path.resolve(physicalRoot, selected));
  const afterRoot = io(() => lstatSync(absoluteRoot, { bigint: true }));
  if (afterRoot.isSymbolicLink()) block('INPUT_LINK');
  if (!sameIdentity(rootStat, afterRoot) || io(() => realpathSync.native(absoluteRoot)) !== physicalRoot) block('INPUT_CHANGED');
  return [...records.values()].sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}
