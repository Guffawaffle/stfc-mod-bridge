import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { compile } from 'json-schema-to-typescript';

const root = path.resolve(import.meta.dirname, '../..');
const arguments_ = process.argv.slice(2);
assert.ok(arguments_.length === 0 || (arguments_.length === 1 && arguments_[0] === '--check'), 'Use generate-protocol.mjs [--check]');
const check = arguments_[0] === '--check';
const result = spawnSync(process.execPath, ['scripts/next/cargo.mjs', 'run', '--locked', '--quiet', '-p', 'bridge-contracts', '--bin', 'bridge-protocol', '--', 'schema'], {
  cwd: root, encoding: 'utf8', windowsHide: true, timeout: 180000, maxBuffer: 16 * 1024 * 1024
});
assert.equal(result.status, 0, `Protocol schema export failed: ${(result.stderr || '').slice(-3000)}`);
assert.ok(!result.error, 'Protocol schema exporter did not complete');
const exported = JSON.parse(result.stdout);
assert.equal(exported.schemaVersion, 'bridge-protocol-schema/v1');
assert.equal(exported.protocolVersion, 1);
assert.equal(exported.draft, 'draft-07');
assert.deepEqual(Object.keys(exported.schemas).sort(), ['aggregate', 'event', 'reply', 'request']);

function sorted(value) {
  if (Array.isArray(value)) return value.map(sorted);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, sorted(value[key])]));
  return value;
}
function localReferences(value) {
  if (!value || typeof value !== 'object') return;
  if (Object.hasOwn(value, '$ref')) assert.ok(typeof value.$ref === 'string' && value.$ref.startsWith('#'), 'Protocol schemas may contain only local references');
  for (const child of Object.values(value)) localReferences(child);
}
const generated = new Map();
for (const [name, schema] of Object.entries(exported.schemas)) {
  assert.equal(schema.$schema, 'http://json-schema.org/draft-07/schema#');
  localReferences(schema);
  const bytes = JSON.stringify(sorted(schema), null, 2) + '\n';
  generated.set(`contracts/generated/${name}.schema.json`, bytes);
}
const types = await compile(exported.schemas.aggregate, 'BridgeProtocolContract', {
  bannerComment: '/* Generated from Rust DTOs by scripts/next/generate-protocol.mjs. Do not edit. */',
  cwd: root, unreachableDefinitions: true, additionalProperties: false,
  enableConstEnums: false, strictIndexSignatures: true, unknownAny: true,
  style: { singleQuote: true, semi: true, tabWidth: 2, printWidth: 100 }
});
generated.set('ui/src/generated/protocol.ts', types.replaceAll('\r\n', '\n'));
const files = [...generated.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([file, bytes]) => ({ path: file, sha256: createHash('sha256').update(bytes).digest('hex') }));
generated.set('contracts/generated/manifest.json', JSON.stringify({
  schemaVersion: 'bridge-generated-protocol/v1', protocolVersion: 1, draft: 'draft-07',
  source: 'crates/bridge-contracts/src/v1', files,
  generationBoundary: 'wire/schema/types; no native or operation execution qualification'
}, null, 2) + '\n');
for (const [relative, bytes] of generated) {
  const destination = path.join(root, relative);
  if (check) {
    assert.equal(readFileSync(destination, 'utf8'), bytes, `Generated protocol drift: ${relative}`);
  } else {
    mkdirSync(path.dirname(destination), { recursive: true });
    writeFileSync(destination, bytes);
  }
}
console.log(JSON.stringify({ result: check ? 'checked' : 'generated', files: generated.size, protocolVersion: 1, operationalQualification: false }));
