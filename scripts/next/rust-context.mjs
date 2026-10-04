import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';

export class RustContextBlocked extends Error {
  constructor(code, message) { super(message); this.name = 'RustContextBlocked'; this.code = code; }
}
const block = (code, message) => { throw new RustContextBlocked(code, message); };
const compilerOverrides = new Set([
  'RUSTC', 'RUSTC_WRAPPER', 'RUSTC_WORKSPACE_WRAPPER', 'RUSTDOC', 'RUSTFMT',
  'CARGO', 'CARGO_ALIAS_FMT', 'CARGO_ALIAS_CLIPPY',
  'CARGO_BUILD_RUSTC', 'CARGO_BUILD_RUSTC_WRAPPER', 'CARGO_BUILD_RUSTC_WORKSPACE_WRAPPER', 'CARGO_BUILD_RUSTDOC'
]);
const releasePin = /^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$/;

function trackedPin(root) {
  let metadata;
  let toolchain;
  try {
    metadata = JSON.parse(readFileSync(path.join(root, 'dependencies/next-toolchain.json'), 'utf8'));
    toolchain = readFileSync(path.join(root, 'rust-toolchain.toml'), 'utf8').replace(/^\uFEFF/, '');
  } catch { block('RUST_PIN_INVALID', 'The tracked Rust dependency and toolchain files must be readable and valid.'); }
  if (metadata?.schemaVersion !== 'bridge-toolchain/v1' || typeof metadata.rust !== 'string' || metadata.rust.length > 64 || !releasePin.test(metadata.rust)) {
    block('RUST_PIN_INVALID', 'Rust requires an exact tracked release version.');
  }
  let section = '';
  let toolchainSections = 0;
  const channels = [];
  for (const raw of toolchain.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const table = /^\[\s*([A-Za-z0-9_.-]+)\s*\]\s*(?:#.*)?$/.exec(line);
    if (table) {
      section = table[1];
      if (section === 'toolchain') toolchainSections += 1;
      continue;
    }
    if (section === 'toolchain' && /^channel\s*=/.test(line)) {
      const channel = /^channel\s*=\s*(["'])([^"']+)\1\s*(?:#.*)?$/.exec(line);
      if (!channel || !releasePin.test(channel[2])) block('RUST_PIN_INVALID', 'The Rust toolchain channel must be one exact quoted release version.');
      channels.push(channel[2]);
    }
  }
  if (toolchainSections !== 1 || channels.length !== 1) block('RUST_PIN_INVALID', 'The tracked Rust toolchain must declare one toolchain section and one channel.');
  if (metadata.rust !== channels[0]) block('RUST_PIN_MISMATCH', 'Rust dependency metadata and the tracked toolchain channel disagree.');
  return metadata.rust;
}

// Creates child-process context only; it neither installs a toolchain nor edits
// the caller's environment. Callers still pass +pin and verify actual versions.
export function rustContext({ root, environment = process.env, platform = process.platform, architecture = process.arch, exists = existsSync }) {
  if (typeof root !== 'string' || !path.isAbsolute(root) || environment === null || typeof environment !== 'object' || Array.isArray(environment) || typeof exists !== 'function') {
    block('RUST_CONTEXT_INVALID', 'Rust context requires an absolute owning root and a child environment.');
  }
  const hostTarget = platform === 'win32' && architecture === 'x64' ? 'x86_64-pc-windows-msvc' : platform === 'darwin' && architecture === 'arm64' ? 'aarch64-apple-darwin' : undefined;
  if (!hostTarget) block('RUST_HOST_UNSUPPORTED', 'Only native Windows x64 and Apple Silicon Rust hosts are supported.');
  const owningRoot = path.resolve(root);
  const pin = trackedPin(owningRoot);
  const env = { ...environment };
  const canonicalKey = key => platform === 'win32' ? key.toUpperCase() : key;
  for (const key of Object.keys(env)) {
    const canonical = canonicalKey(key);
    if (!compilerOverrides.has(canonical)) continue;
    const value = env[key];
    const nonblank = value !== undefined && value !== '' && (!canonical.startsWith('CARGO_BUILD_') || typeof value !== 'string' || value.trim() !== '');
    if (nonblank) block('RUST_COMPILER_OVERRIDE', `Custom ${canonical} compiler routing is unsupported.`);
    delete env[key];
  }
  function set(key, value) {
    for (const existing of Object.keys(env)) if (canonicalKey(existing) === key) delete env[existing];
    env[key] = value;
  }
  set('RUSTUP_TOOLCHAIN', pin);
  set('CARGO_TARGET_DIR', path.join(owningRoot, 'target'));
  set('CARGO_BUILD_TARGET', hostTarget);

  const extension = platform === 'win32' ? '.exe' : '';
  const scopedRoot = path.join(owningRoot, 'artifacts/toolchain');
  const bin = path.join(scopedRoot, 'cargo/bin');
  const cargo = path.join(bin, `cargo${extension}`);
  const rustc = path.join(bin, `rustc${extension}`);
  const rustdoc = path.join(bin, `rustdoc${extension}`);
  const scoped = exists(cargo);
  if (scoped && (!exists(rustc) || !exists(rustdoc))) block('RUST_SCOPED_SHIM_MISSING', 'The scoped Cargo route is missing its matching compiler or rustdoc shim.');
  // Dedicated Cargo environment variables override build.rustc/build.rustdoc;
  // empty wrapper values reset configured wrappers, rather than inheriting them.
  // https://doc.rust-lang.org/cargo/reference/environment-variables.html
  set('RUSTC', scoped ? rustc : `rustc${extension}`);
  set('RUSTDOC', scoped ? rustdoc : `rustdoc${extension}`);
  set('RUSTC_WRAPPER', '');
  set('RUSTC_WORKSPACE_WRAPPER', '');
  if (!scoped) return { pin, hostTarget, env, cargo: `cargo${extension}`, rustc: `rustc${extension}` };
  const priorPath = Object.keys(environment).filter(key => canonicalKey(key) === 'PATH').map(key => environment[key]).filter(value => typeof value === 'string');
  if (priorPath.length > 1) block('RUST_CONTEXT_INVALID', 'The child environment contains conflicting PATH spellings.');
  set('CARGO_HOME', path.join(scopedRoot, 'cargo'));
  set('RUSTUP_HOME', path.join(scopedRoot, 'rustup'));
  set('PATH', `${bin}${priorPath[0] ? `${platform === 'win32' ? ';' : ':'}${priorPath[0]}` : ''}`);
  return { pin, hostTarget, env, cargo, rustc };
}
