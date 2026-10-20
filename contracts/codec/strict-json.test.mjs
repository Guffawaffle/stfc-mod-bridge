import test from 'node:test';
import assert from 'node:assert/strict';
import { decodeStrictJson, MAX_WIRE_BYTES, MAX_WIRE_DEPTH, WireInputError } from './strict-json.mjs';
const refuses = (source, code) => assert.throws(() => decodeStrictJson(source), error => error instanceof WireInputError && error.code === code);

test('parses legal JSON, preserving containers and safe numbers without inherited keys', () => {
  const parsed = decodeStrictJson(' {"label":"synthetic \\ud83d\\ude80", "values":[true,false,null,"1.25",4294967295],"__proto__":{"x":1}}\n');
  assert.equal(Object.getPrototypeOf(parsed), null);
  assert.equal(Object.getPrototypeOf(parsed.__proto__), null);
  assert.equal(parsed.label, 'synthetic 🚀');
  assert.deepEqual(parsed.values, [true, false, null, '1.25', 4294967295]);
  assert.equal({}.x, undefined);
  assert.equal(decodeStrictJson('"\\\\\\\""'), '\\"');
});
test('decoded duplicate keys reject before replacement, including nested objects', () => {
  for (const input of ['{"x":1,"x":2}', '{"x":1,"\\u0078":2}', '{"outer":[{"x":1,"x":2}]}', '{"type":"query","type":"command"}']) refuses(input, 'duplicate_key');
  assert.equal(decodeStrictJson('{"a":{"x":1},"b":{"x":2}}').b.x, 2);
});
test('only one complete value and exact JSON whitespace are accepted', () => {
  for (const source of ['', '{', '[1,]', '{"a":1,}', '01', 'true false', '{}{}', '{} trailing', '\ufeff{}', '\u00a0{}', '"raw\nnewline"', '"\\x20"']) assert.throws(() => decodeStrictJson(source), WireInputError);
  for (const source of ['0', '100', 'null', 'true', '[]', '{}', '"text"']) assert.doesNotThrow(() => decodeStrictJson(source));
});
test('Unicode and UTF8 failures never become replacement characters', () => {
  for (const source of ['"\\ud800"', '"\\udc00"', '"\\ud800a"', '"\ud800"']) refuses(source, 'invalid_unicode');
  refuses(new Uint8Array([0xc3, 0x28]), 'invalid_utf8');
  refuses(new Uint8Array([0xef, 0xbb, 0xbf, 0x7b, 0x7d]), 'invalid_json');
  assert.equal(decodeStrictJson(new TextEncoder().encode('"valid 🚀"')), 'valid 🚀');
});
test('wire size is an actual UTF8 byte bound and container depth is bounded', () => {
  const exact = '"' + 'x'.repeat(MAX_WIRE_BYTES - 2) + '"';
  assert.equal(decodeStrictJson(exact).length, MAX_WIRE_BYTES - 2);
  refuses(exact + ' ', 'wire_too_large');
  refuses('"' + 'é'.repeat(MAX_WIRE_BYTES / 2) + '"', 'wire_too_large');
  refuses(new Uint8Array(MAX_WIRE_BYTES + 1), 'wire_too_large');
  assert.doesNotThrow(() => decodeStrictJson('['.repeat(MAX_WIRE_DEPTH) + '0' + ']'.repeat(MAX_WIRE_DEPTH)));
  refuses('['.repeat(MAX_WIRE_DEPTH + 1) + '0' + ']'.repeat(MAX_WIRE_DEPTH + 1), 'wire_too_deep');
});
test('unsafe numeric transport values reject; u64 decimal strings remain exact', () => {
  for (const source of ['9007199254740992', '18446744073709551615', '-9007199254740992']) refuses(source, 'unsafe_number');
  for (const source of ['-0', '1.25', '1e999', '1e2', '1e-9999', '9007199254740991.1', '1.00000000000000001', '3.00000000000000001']) refuses(source, 'non_integer_number');
  assert.equal(decodeStrictJson('"18446744073709551615"'), '18446744073709551615');
  assert.equal(decodeStrictJson('9007199254740991'), Number.MAX_SAFE_INTEGER);
});
test('errors carry closed framing codes and never echo private payloads', () => {
  const sentinel = 'synthetic-secret-do-not-echo';
  try { decodeStrictJson(`{"${sentinel}":0,"${sentinel}":1}`); assert.fail(); }
  catch (error) { assert.equal(error.code, 'duplicate_key'); assert.ok(!String(error).includes(sentinel)); }
  refuses({ password: sentinel }, 'invalid_input');
});
