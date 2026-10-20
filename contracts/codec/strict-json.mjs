// Shared browser/Node ingress framing. This module performs no I/O and has no
// platform dependency. Schema and semantic validation follow this parser.
export const MAX_WIRE_BYTES = 256 * 1024;
export const MAX_WIRE_DEPTH = 32;

export class WireInputError extends Error {
  constructor(code) { super(code); this.name = 'WireInputError'; this.code = code; }
}
const reject = code => { throw new WireInputError(code); };

function validUnicode(value) {
  for (let i = 0; i < value.length; i += 1) {
    const unit = value.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
  }
  return true;
}

export function decodeStrictJson(input) {
  let source;
  if (typeof input === 'string') {
    if (input.length > MAX_WIRE_BYTES) reject('wire_too_large');
    if (!validUnicode(input)) reject('invalid_unicode');
    if (new TextEncoder().encode(input).length > MAX_WIRE_BYTES) reject('wire_too_large');
    source = input;
  } else if (input instanceof Uint8Array) {
    if (input.byteLength > MAX_WIRE_BYTES) reject('wire_too_large');
    // Preserve a BOM so JSON grammar rejects it consistently with Rust.
    try { source = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(input); }
    catch { reject('invalid_utf8'); }
  } else reject('invalid_input');

  let offset = 0;
  const whitespace = () => { while (offset < source.length && /[\t\n\r ]/.test(source[offset])) offset += 1; };
  function string() {
    const start = offset++;
    let ended = false;
    while (offset < source.length) {
      const character = source[offset++];
      if (character === '"') { ended = true; break; }
      if (character === '\\') offset += 1;
    }
    if (!ended) reject('invalid_json');
    let result;
    try { result = JSON.parse(source.slice(start, offset)); }
    catch { reject('invalid_json'); }
    if (!validUnicode(result)) reject('invalid_unicode');
    return result;
  }
  function value(depth) {
    whitespace();
    const character = source[offset];
    if (character === '"') return string();
    if (character === '{' || character === '[') {
      if (depth + 1 > MAX_WIRE_DEPTH) reject('wire_too_deep');
      offset += 1;
      whitespace();
      if (character === '{') {
        const object = Object.create(null);
        const keys = new Set();
        if (source[offset] === '}') { offset += 1; return object; }
        while (offset < source.length) {
          if (source[offset] !== '"') reject('invalid_json');
          const key = string();
          if (keys.has(key)) reject('duplicate_key');
          keys.add(key);
          whitespace();
          if (source[offset++] !== ':') reject('invalid_json');
          object[key] = value(depth + 1);
          whitespace();
          const separator = source[offset++];
          if (separator === '}') return object;
          if (separator !== ',') reject('invalid_json');
          whitespace();
        }
      } else {
        const array = [];
        if (source[offset] === ']') { offset += 1; return array; }
        while (offset < source.length) {
          array.push(value(depth + 1));
          whitespace();
          const separator = source[offset++];
          if (separator === ']') return array;
          if (separator !== ',') reject('invalid_json');
        }
      }
      reject('invalid_json');
    }
    for (const [literal, result] of [['true', true], ['false', false], ['null', null]]) {
      if (source.startsWith(literal, offset)) { offset += literal.length; return result; }
    }
    const number = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(source.slice(offset));
    if (!number) reject('invalid_json');
    // Version/PID fields use lexical safe integers. Decimal counters and
    // fractional provider values use typed strings, avoiding JS rounding and
    // Rust's integer-versus-float deserialization disagreement.
    if (/[.eE]/.test(number[0]) || number[0] === '-0') reject('non_integer_number');
    offset += number[0].length;
    const result = Number(number[0]);
    if (!Number.isFinite(result)) reject('invalid_number');
    if (Number.isInteger(result) && !Number.isSafeInteger(result)) reject('unsafe_number');
    return result;
  }
  const parsed = value(0);
  whitespace();
  if (offset !== source.length) reject('trailing_input');
  return parsed;
}
