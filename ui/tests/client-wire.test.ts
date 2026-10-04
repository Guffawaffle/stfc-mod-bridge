import { expect, test } from 'vitest';
import { readFileSync } from 'node:fs';
import { captureData, captureRequest, canonicalData, ClientBoundaryError, decodeEvent, decodeReply, decodeRequest } from '../src/client/wire';

const fixtures = new URL('../../contracts/fixtures/', import.meta.url);
const index: { fixtures: { id: string; path: string; kind: 'request' | 'reply' | 'event'; expectedWire: boolean; expectedSemantic: boolean }[] } = JSON.parse(readFileSync(new URL('index.json', fixtures), 'utf8'));
const codecs = { request: decodeRequest, reply: decodeReply, event: decodeEvent };

for (const fixture of index.fixtures) {
  test(`generated schema/framing parity: ${fixture.id}`, () => {
    const raw = readFileSync(new URL(fixture.path, fixtures));
    if (fixture.expectedWire) expect(() => codecs[fixture.kind](raw)).not.toThrow();
    else expect(() => codecs[fixture.kind](raw)).toThrow(ClientBoundaryError);
    // expectedSemantic is independently established by Rust; browser shape
    // acceptance never relabels its cross-field or native decisions as proven.
  });
}

test('capture detaches and deeply freezes data, preserving safe prototype keys', () => {
  const input = { nested: [{ value: 'original' }], ['__proto__']: { safe: true } };
  const capture = captureData(input);
  input.nested[0].value = 'later';
  expect(capture.nested[0].value).toBe('original');
  expect(Object.getPrototypeOf(capture)).toBeNull();
  expect(Object.getPrototypeOf(capture.__proto__)).toBeNull();
  expect(Object.isFrozen(capture.nested[0])).toBe(true);
  expect(() => Object.assign(capture.nested[0], { value: 'mutated' })).toThrow();
  expect(canonicalData(capture)).toContain('"__proto__":{"safe":true}');
});

test('accessors and serialization hooks never execute', () => {
  let invoked = 0;
  const getter = Object.defineProperty({}, 'private', { enumerable: true, get() { invoked++; return 'secret-canary'; } });
  const hook = { toJSON() { invoked++; return {}; } };
  expect(() => captureData(getter)).toThrow('invalid_capture');
  expect(() => captureData(hook)).toThrow('invalid_capture');
  expect(invoked).toBe(0);
});

test.each([
  () => ({ value: undefined }), () => ({ value: () => {} }), () => ({ value: Symbol('private') }), () => ({ value: 1n }),
  () => new Date(), () => new Map(), () => Object.create({ inherited: true }), () => [,,],
  () => Object.assign([1], { named: 2 }), () => ({ value: NaN }), () => ({ value: Infinity }),
  () => ({ value: 0.5 }), () => ({ value: -0 }), () => ({ value: Number.MAX_SAFE_INTEGER + 1 }),
  () => Object.defineProperty({}, 'hidden', { value: 1 }), () => ({ [Symbol('key')]: 1 }),
  () => { const value: { self?: unknown } = {}; value.self = value; return value; },
  () => ({ value: 'x'.repeat(256 * 1024) }), () => Array.from({ length: 256 * 1024 + 1 }, () => 0),
  () => { let value: unknown = {}; for (let i = 0; i < 32; i++) value = { child: value }; return value; },
])('reject unsupported data-only capture %s', makeInput => expect(() => captureData(makeInput())).toThrow('invalid_capture'));

test('request capture uses the accepted parser and exact generated request validator', () => {
  const raw = readFileSync(new URL('sc14-admit-request.json', fixtures), 'utf8');
  const input = JSON.parse(raw);
  const capture = captureRequest(input);
  input.body.command.input.planRef.reviewDigest = 'private-canary';
  expect(capture.encoded).not.toContain('private-canary');
  expect(canonicalData(decodeRequest(capture.encoded))).toBe(canonicalData(decodeRequest(raw)));
  expect(Object.isFrozen(capture.request.body)).toBe(true);
});

test('local boundary diagnostics contain only safe codes', () => {
  const frame = '{"secret-canary":"C:/private-path","protocolVersion":1,"protocolVersion":1}';
  try { decodeReply(frame); throw new Error('missing failure'); }
  catch (error) { expect(error).toBeInstanceOf(ClientBoundaryError); expect(String(error)).toBe('ClientBoundaryError: framing'); }
});
