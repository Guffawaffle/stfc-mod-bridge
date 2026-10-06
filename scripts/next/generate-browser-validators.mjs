import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import Ajv from 'ajv';
import addFormats from 'ajv-formats';
import standaloneCode from 'ajv/dist/standalone/index.js';
import { build } from 'esbuild';
import ts from 'typescript';

const root = path.resolve(import.meta.dirname, '../..');
const args = process.argv.slice(2);
assert.ok(args.length === 0 || args.length === 1 && args[0] === '--check', 'Use generate-browser-validators.mjs [--check]');
const check = args[0] === '--check';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const schemaInputs = [];
const ajv = new Ajv({ strict: true, allErrors: false, ownProperties: true,
  coerceTypes: false, useDefaults: false, removeAdditional: false, messages: false,
  code: { source: true, esm: true, lines: true }
});
addFormats(ajv, { mode: 'full', formats: ['date-time'] });
const names = {};
for (const kind of ['request', 'reply', 'event']) {
  const file = `contracts/generated/${kind}.schema.json`;
  const bytes = readFileSync(path.join(root, file));
  const schema = JSON.parse(bytes.toString('utf8'));
  assert.equal(schema.$schema, 'http://json-schema.org/draft-07/schema#');
  schemaInputs.push({ path: file, sha256: hash(bytes) });
  const id = `bridge-v1-${kind}`;
  ajv.addSchema(schema, id);
  names[`validate${kind[0].toUpperCase()}${kind.slice(1)}`] = id;
}
const code = standaloneCode(ajv, names);
const bundled = await build({
  stdin: { contents: code, resolveDir: root, sourcefile: 'bridge-v1-validators.generated.mjs' },
  bundle: true, write: false, format: 'esm', platform: 'browser', target: 'es2022',
  legalComments: 'inline', charset: 'utf8', metafile: true, logLevel: 'silent'
});
assert.equal(bundled.outputFiles.length, 1);
assert.ok(Object.values(bundled.metafile.outputs).every(output => output.imports.length === 0), 'Browser validators must have no external runtime dependency');
const module = bundled.outputFiles[0].text;
// Inspect executable syntax rather than inert .code strings retained by Ajv's
// runtime helpers. A text grep would misclassify those generation hints.
const syntax = ts.createSourceFile('validators.mjs', module, ts.ScriptTarget.ES2022, true, ts.ScriptKind.JS);
assert.equal(syntax.parseDiagnostics.length, 0, 'Bundled validator syntax must parse');
function inspect(node) {
  if ((ts.isCallExpression(node) || ts.isNewExpression(node)) && ts.isIdentifier(node.expression)) {
    assert.ok(!['eval', 'Function', 'fetch', 'require'].includes(node.expression.text), 'Browser validators cannot compile code, fetch or require at runtime');
  }
  if (ts.isPropertyAccessExpression(node) && ts.isIdentifier(node.expression)) {
    assert.ok(!['process', 'Buffer'].includes(node.expression.text), 'Browser validators cannot read a native runtime');
  }
  ts.forEachChild(node, inspect);
}
inspect(syntax);
const helpers = Object.keys(bundled.metafile.inputs).filter(file => file !== 'bridge-v1-validators.generated.mjs').sort().map(file => {
  const normalized = file.replaceAll('\\', '/');
  assert.ok(normalized.startsWith('node_modules/'), 'Validator helper must be a locked local dependency');
  return { path: normalized, sha256: hash(readFileSync(path.resolve(root, file))) };
});
const files = new Map();
files.set('ui/src/generated/validators.mjs', `/*! Generated from Rust-derived schemas; do not edit. Helper licenses: validators.LICENSE.txt. */\n${module}`);
files.set('ui/src/generated/validators.d.mts', `/* Generated schema type guards; Rust owns cross-field/native semantic validation. */
import type { Request, Reply, Event } from './protocol.js';
export function validateRequest(value: unknown): value is Request;
export function validateReply(value: unknown): value is Reply;
export function validateEvent(value: unknown): value is Event;
`);
const licenses = [['ajv', 'LICENSE'], ['ajv-formats', 'LICENSE'], ['esbuild', 'LICENSE.md']]
  .map(([name, file]) => `${name}\n${readFileSync(path.join(root, 'node_modules', name, file), 'utf8').trim()}\n`).join('\n');
files.set('ui/src/generated/validators.LICENSE.txt', licenses);
const pins = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).devDependencies;
files.set('contracts/generated/browser-validator-manifest.json', JSON.stringify({
  schemaVersion: 'bridge-browser-validators/v1', protocolVersion: 1,
  sourceProtocolManifestSha256: hash(readFileSync(path.join(root, 'contracts/generated/manifest.json'))),
  toolchain: { ajv: pins.ajv, formats: pins['ajv-formats'], esbuild: pins.esbuild, typescript: pins.typescript },
  validation: { strict: true, ownProperties: true, coerceTypes: false, useDefaults: false,
    removeAdditional: false, dateTimeFormat: 'full', offlineCompilation: true },
  schemas: schemaInputs, bundledHelpers: helpers,
  files: [...files].map(([file, bytes]) => ({ path: file, sha256: hash(bytes) })),
  boundary: 'browser schema/type guards and bundled helpers; no Rust cross-field or native authority'
}, null, 2) + '\n');
for (const [file, bytes] of files) {
  const target = path.join(root, file);
  if (check) assert.equal(readFileSync(target, 'utf8'), bytes, `Browser validator drift: ${file}`);
  else { mkdirSync(path.dirname(target), { recursive: true }); writeFileSync(target, bytes); }
}
console.log(JSON.stringify({ result: check ? 'checked' : 'generated', files: files.size,
  bundledHelpers: helpers.length, runtimeImports: 0, rustBuildInvoked: false }));
