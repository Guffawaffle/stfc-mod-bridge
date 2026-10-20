import { decodeStrictJson, MAX_WIRE_BYTES, MAX_WIRE_DEPTH } from '../../../contracts/codec/strict-json.mjs';
import { validateRequest, validateReply, validateEvent } from '../generated/validators.mjs';
import type { Request, Reply, Event } from '../generated/protocol';
import type { RawFrame } from './transport';

export type DeepReadonly<T> = T extends object ? { readonly [K in keyof T]: DeepReadonly<T[K]> } : T;
export type BoundaryCode = 'framing' | 'schema' | 'incompatible_envelope' | 'invalid_capture';

/** No raw input, validator diagnostics or native text escapes this boundary. */
export class ClientBoundaryError extends Error {
  constructor(readonly code: BoundaryCode) { super(code); this.name = 'ClientBoundaryError'; }
}
const reject = (): never => { throw new ClientBoundaryError('invalid_capture'); };

function encodeData(item: unknown, sortKeys = false): string {
  if (Array.isArray(item)) return '[' + item.map(value => encodeData(value, sortKeys)).join(',') + ']';
  if (item !== null && typeof item === 'object') {
    const object = item as Record<string, unknown>;
    const keys = Object.keys(object);
    if (sortKeys) keys.sort();
    return '{' + keys.map(key => JSON.stringify(key) + ':' + encodeData(object[key], sortKeys)).join(',') + '}';
  }
  return JSON.stringify(item);
}

/** Copy own data properties before encoding. No conversion hooks are invoked. */
export function captureData<T>(value: T): DeepReadonly<T> {
  const ancestors = new Set<object>();
  let budget = MAX_WIRE_BYTES;
  const spend = (cost: number) => { budget -= cost; if (budget < 0) reject(); };
  const copy = (input: unknown, depth: number): unknown => {
    if (input === null || typeof input === 'boolean') { spend(input === null || input === true ? 4 : 5); return input; }
    if (typeof input === 'string') { spend(input.length + 2); return input; }
    if (typeof input === 'number') {
      if (!Number.isSafeInteger(input) || Object.is(input, -0)) reject();
      spend(String(input).length); return input;
    }
    if (typeof input !== 'object' || !input || depth >= MAX_WIRE_DEPTH || ancestors.has(input)) reject();
    const source = input as object;
    const array = Array.isArray(input);
    const prototype = Object.getPrototypeOf(input);
    if (array ? prototype !== Array.prototype : prototype !== Object.prototype && prototype !== null) reject();
    const ownKeys = Reflect.ownKeys(source);
    if (ownKeys.length > MAX_WIRE_BYTES || ownKeys.some(key => typeof key !== 'string')) reject();
    ancestors.add(source);
    spend(2);
    let output: unknown;
    if (array) {
      const length = Object.getOwnPropertyDescriptor(source, 'length')?.value;
      if (!Number.isSafeInteger(length) || length > MAX_WIRE_BYTES || ownKeys.length !== length + 1) reject();
      const items: unknown[] = [];
      for (let index = 0; index < length; index++) {
        const descriptor = Object.getOwnPropertyDescriptor(source, String(index));
        if (!descriptor || !('value' in descriptor) || !descriptor.enumerable) return reject();
        if (index) spend(1);
        items.push(copy(descriptor.value, depth + 1));
      }
      output = Object.freeze(items);
    } else {
      const record: Record<string, unknown> = Object.create(null);
      for (const [index, property] of ownKeys.entries()) {
        const key = property as string;
        const descriptor = Object.getOwnPropertyDescriptor(source, key);
        if (!descriptor || !('value' in descriptor) || !descriptor.enumerable) return reject();
        spend(key.length + 3 + (index ? 1 : 0));
        Object.defineProperty(record, key, { value: copy(descriptor.value, depth + 1), enumerable: true });
      }
      output = Object.freeze(record);
    }
    ancestors.delete(source);
    return output;
  };
  try {
    const detached = copy(value, 0) as DeepReadonly<T>;
    // The common parser also checks UTF-8 bytes, escapes and scalar Unicode.
    decodeStrictJson(encodeData(detached));
    return detached;
  } catch { throw new ClientBoundaryError('invalid_capture'); }
}

/** Structural comparison only: object key order is irrelevant; list order stays exact. */
export function canonicalData(value: unknown): string {
  return encodeData(captureData(value), true);
}

function decode<T>(frame: RawFrame, validator: (value: unknown) => value is T): DeepReadonly<T> {
  let parsed: unknown;
  try { parsed = decodeStrictJson(frame); } catch { throw new ClientBoundaryError('framing'); }
  if (parsed !== null && typeof parsed === 'object' && Object.hasOwn(parsed, 'protocolVersion')
    && (parsed as { protocolVersion: unknown }).protocolVersion !== 1) throw new ClientBoundaryError('incompatible_envelope');
  if (!validator(parsed)) throw new ClientBoundaryError('schema');
  return captureData(parsed);
}
export const decodeRequest = (frame: RawFrame): DeepReadonly<Request> => decode(frame, validateRequest);
export const decodeReply = (frame: RawFrame): DeepReadonly<Reply> => decode(frame, validateReply);
export const decodeEvent = (frame: RawFrame): DeepReadonly<Event> => decode(frame, validateEvent);

export interface RequestCapture {
  readonly request: DeepReadonly<Request>;
  readonly encoded: string;
}
export function captureRequest(value: unknown): RequestCapture {
  const encoded = encodeData(captureData(value));
  return Object.freeze({ request: decodeRequest(encoded), encoded });
}
